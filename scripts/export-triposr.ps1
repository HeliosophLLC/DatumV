# Exports TripoSR from upstream PyTorch weights to two ONNX graphs.
#
# Upstream: stabilityai/TripoSR (MIT, Tochilkin et al., 2024)
# Paper: "TripoSR: Fast 3D Object Reconstruction from a Single Image"
# Repo:  https://github.com/VAST-AI-Research/TripoSR
#
# TripoSR is a feedforward single-image -> 3D model: image goes in, a
# triplane neural field comes out, and you query that field on a 3D grid
# to extract a mesh via marching cubes. Architecture:
#   1. DINOv1 image encoder    -> image tokens
#   2. Learnable triplane tokens + cross-attention transformer
#                              -> triplane tokens
#   3. Upsampler post-processor -> triplane features [3, C, 64, 64]
#   4. Small NeRF MLP queried at (x, y, z) -> (density, color features)
#
# Steps 1-3 run once per image. Step 4 runs many times (chunked over a
# 128/256/384 voxel grid). Marching cubes runs on the host over the
# resulting density grid; we do NOT export rendering / MC into ONNX.
#
# Therefore the export produces TWO graphs:
#   triplane.onnx   image  -> triplane                         (large; ~1.6 GB)
#   nerf.onnx       (triplane, xyz) -> (density, color_feats)  (small)
#
# The big graph spills weights to a triplane.onnx_data sidecar via
# external_data_format=True, so the .onnx file stays under the 2 GB
# protobuf limit. ORT loads sidecar weights automatically when the two
# files share a directory.
#
# TripoSR is not on PyPI. We clone the repo into a working directory and
# add it to PYTHONPATH for the trace; the venv only needs torch +
# transformers + the TripoSR runtime deps.
#
# Requirements:
#   - Python 3.10 (`py -3.10 --version`).
#   - ~6 GB free disk: 1.68 GB ckpt download + ~1.6 GB ONNX + ~1.5 GB
#     for torch / transformers / cu124 wheels + cloned repo.
#   - Internet for the initial fetch (HF Hub + GitHub clone).
#   - git on PATH.
#
# Idempotent: safe to rerun. Reuses .venv and the cloned TripoSR repo.
#
# Usage:
#   ./scripts/export-triposr.ps1
#       -- exports to $env:DATUM_MODELS\triposr-staging\
#   ./scripts/export-triposr.ps1 -OutputDirectory C:\foo
#       -- exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'triposr-staging'
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputDirectory <path>.'
        }
    ),

    [Parameter()]
    [string]$ModelId = 'stabilityai/TripoSR',

    # Where to clone the TripoSR architecture repo. Reused across runs.
    [Parameter()]
    [string]$TripoSrRepo = $(Join-Path $PSScriptRoot '..\\.cache\TripoSR'),

    # If set, post-process the fp32 traces into fp16 siblings
    # (triplane_fp16.onnx, nerf_fp16.onnx). Halves on-disk + VRAM cost.
    # The fp32 outputs are left in place so you can A/B.
    [Parameter()]
    [switch]$Fp16
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Resolve-Path "$PSScriptRoot\.."
$venvPython = Join-Path $repoRoot '.venv\Scripts\python.exe'

# Canonical dependency pin set for this export. Kept in one place so the
# script is the single source of truth, then mirrored to the output
# directory as requirements.txt after a successful export so anyone with
# just the ONNX files can recreate the working environment.
#
# Two install stages because torch + torchvision come from PyTorch's
# cu124 index, not PyPI.
$pypiRequirements = @'
# TripoSR ONNX export -- PyPI dependencies.
# Install with: pip install -r requirements.txt
# Then install torch separately from the cu124 index (see requirements-torch.txt).

transformers==4.45.2
einops>=0.7
omegaconf>=2.3
jaxtyping>=0.2.20
huggingface-hub>=0.24
trimesh>=4.0
imageio[ffmpeg]>=2.31
# rembg is intentionally NOT installed: it pulls onnxruntime, which we
# uninstall to avoid ABI conflicts with other export scripts sharing this
# venv. The export script stubs `rembg` in sys.modules before importing
# the TripoSR architecture, since background removal runs host-side
# (or not at all) and never touches the ONNX trace.
Pillow>=10.1
onnxconverter-common>=1.14
accelerate
onnx>=1.16
'@

$torchRequirements = @'
# TripoSR ONNX export -- PyTorch (cu124).
# Install with: pip install -r requirements-torch.txt --index-url https://download.pytorch.org/whl/cu124

torch<2.5
torchvision<0.20
'@

# 1. Project-local Python 3.10 venv at .venv (gitignored). Shared with
#    the other export-* scripts in this directory.
if (-not (Test-Path $venvPython)) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv (Join-Path $repoRoot '.venv')
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

& (Join-Path $repoRoot '.venv\Scripts\Activate.ps1')

# 2. Install conversion tooling.
#    See export-zoedepth.ps1 for the long-form rationale on the
#    uninstall-first dance + torch/torchvision ABI pin. Same shape here.
Write-Host 'Cleaning stale optimum / transformers / torchvision packages ...' -ForegroundColor Cyan
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers torchvision *>$null } catch { }

# Write the requirements blocks (defined at the top of the script) to
# temp files so pip install -r can consume them, and so the same content
# can be mirrored into the output directory once the export succeeds.
$pypiReqTemp  = Join-Path $env:TEMP "datum-triposr-requirements-$(Get-Random).txt"
$torchReqTemp = Join-Path $env:TEMP "datum-triposr-requirements-torch-$(Get-Random).txt"
Set-Content -Path $pypiReqTemp  -Encoding utf8 -Value $pypiRequirements
Set-Content -Path $torchReqTemp -Encoding utf8 -Value $torchRequirements

Write-Host 'Installing PyPI requirements (transformers / einops / rembg / ...) ...' -ForegroundColor Cyan
pip install --quiet -r $pypiReqTemp

Write-Host 'Installing torch 2.4 + torchvision 0.19 (CUDA 12.4) ...' -ForegroundColor Cyan
pip install --quiet --upgrade -r $torchReqTemp --index-url https://download.pytorch.org/whl/cu124

# 3. Clone the TripoSR repo for its architecture module (tsr/). The HF
#    weights at stabilityai/TripoSR ship just config.yaml + model.ckpt;
#    the nn.Module classes that consume them live here.
if (-not (Test-Path (Join-Path $TripoSrRepo 'tsr\system.py'))) {
    Write-Host "Cloning TripoSR architecture repo to $TripoSrRepo ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path (Split-Path $TripoSrRepo -Parent) | Out-Null
    git clone --depth 1 https://github.com/VAST-AI-Research/TripoSR.git $TripoSrRepo
    if ($LASTEXITCODE -ne 0) {
        deactivate
        throw "git clone failed with exit code $LASTEXITCODE."
    }
} else {
    Write-Host "TripoSR repo already cloned at $TripoSrRepo, reusing." -ForegroundColor DarkGray
}

# 4. Stage output.
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# 5. Trace both wrappers via torch.onnx.export. The python helper:
#      a. Adds the cloned TripoSR repo to sys.path so `from tsr.system import TSR` works.
#      b. Loads the model from stabilityai/TripoSR via TSR.from_pretrained.
#      c. Wraps the encoder + tokenizer + backbone + post_processor in
#         a tensor-in/tensor-out module so torch.onnx can trace it
#         without choking on the image-preprocessing PIL path.
#      d. Wraps query_triplane + decoder in a second module: takes the
#         triplane feature tensor + a chunk of xyz points, returns
#         density and color features. grid_sample needs opset 17+.
#      e. Exports both with external_data_format on the big one.
$pythonHelper = @'
import os, sys
from pathlib import Path
import torch

triposr_repo = os.environ["TRIPOSR_REPO"]
model_id     = sys.argv[1]
output_dir   = Path(sys.argv[2])
output_dir.mkdir(parents=True, exist_ok=True)

sys.path.insert(0, triposr_repo)

# TripoSR's tsr/system.py + tsr/utils.py import several modules at load
# time that we don't actually need during ONNX export. Stub them out
# rather than installing them, since each pulls a heavy dependency:
#
#   torchmcubes -- CUDA C++ extension, needs CUDA toolchain + MSVC to
#                  build from source on Windows. Used only for runtime
#                  mesh extraction, which we do on the host instead.
#   rembg       -- Transitively imports onnxruntime, which we deliberately
#                  uninstalled to avoid ABI conflicts with other export
#                  scripts in this venv. Used only for background removal
#                  preprocessing, which our TriplaneExport wrapper skips.
#
# Both are module-load imports, so they have to be in sys.modules before
# `from tsr.system import TSR` runs.
import types as _types

def _unused_stub_factory(name):
    def _stub(*args, **kwargs):
        raise RuntimeError(f"{name} stub: not used during ONNX export.")
    return _stub

_torchmcubes = _types.ModuleType("torchmcubes")
_torchmcubes.marching_cubes = _unused_stub_factory("torchmcubes.marching_cubes")
sys.modules["torchmcubes"] = _torchmcubes

_rembg = _types.ModuleType("rembg")
_rembg.remove      = _unused_stub_factory("rembg.remove")
_rembg.new_session = _unused_stub_factory("rembg.new_session")
sys.modules["rembg"] = _rembg

from tsr.system import TSR

print(f"Loading {model_id} ...", flush=True)
model = TSR.from_pretrained(
    model_id,
    config_name="config.yaml",
    weight_name="model.ckpt",
)
# Disable internal chunking on the renderer -- we'll chunk ourselves on
# the host. set_chunk_size(0) means "no chunking", which is what we want
# for the per-call ONNX graph (one chunk per ORT.Run).
model.renderer.set_chunk_size(0)
model.eval()

# --- Wrapper 1: image -> triplane -------------------------------------
# Bypasses TSR.image_processor (PIL-side resize / normalize) since that
# path doesn't trace. The caller is expected to pre-resize to 512x512
# and normalize to [0, 1] before invoking the ONNX graph.
class TriplaneExport(torch.nn.Module):
    def __init__(self, tsr):
        super().__init__()
        self.image_tokenizer = tsr.image_tokenizer
        self.tokenizer       = tsr.tokenizer
        self.backbone        = tsr.backbone
        self.post_processor  = tsr.post_processor

    def forward(self, image):
        # image: [B, 3, 512, 512], float32, normalized to [0, 1]
        # image_tokenizer expects [B, Nv, C, H, W]; we trace single-view (Nv=1).
        rgb_cond = image.unsqueeze(1)
        input_image_tokens = self.image_tokenizer(rgb_cond, modulation_cond=None)
        # [B, Nv, Ct, Nt] -> [B, Nv*Nt, Ct]
        B, Nv, Ct, Nt = input_image_tokens.shape
        input_image_tokens = input_image_tokens.permute(0, 1, 3, 2).reshape(B, Nv * Nt, Ct)
        tokens = self.tokenizer(B)
        tokens = self.backbone(tokens, encoder_hidden_states=input_image_tokens)
        scene_codes = self.post_processor(self.tokenizer.detokenize(tokens))
        return scene_codes   # [B, 3, C, 64, 64]

# --- Wrapper 2: (triplane, xyz) -> (density, color) -------------------
# Outputs:
#   density_act -- activated density field; the value MC isolevels run on
#   color       -- activated RGB; sample at MC vertex positions for per-
#                  vertex color in a second pass after MC.
#
# query_triplane's real signature on this TripoSR revision is
# (decoder, positions, triplane) -- decoder is the first arg, not a
# separate post-processing step. It already calls decoder internally,
# applies density / color activations, and returns a dict containing
# density, density_act, features, and color.
#
# query_triplane uses F.grid_sample internally, which requires opset 17.
# Renderer.chunk_size=0 (set above) means the trace skips the python-side
# chunk_batch loop and emits a single grid_sample + MLP path.
class NerfExport(torch.nn.Module):
    def __init__(self, tsr):
        super().__init__()
        self.renderer = tsr.renderer
        self.decoder  = tsr.decoder

    def forward(self, triplane, xyz):
        # triplane: [1, 3, C, 64, 64]    xyz: [K, 3] in [-radius, radius]
        # NOTE: query_triplane expects an unbatched triplane [3, C, H, W],
        # since it rearranges with Np=3 on the leading axis. The export
        # graph takes the batched form for symmetry with triplane.onnx
        # and squeezes the batch axis here.
        out = self.renderer.query_triplane(self.decoder, xyz, triplane.squeeze(0))
        return out["density_act"], out["color"]

triplane_module = TriplaneExport(model).eval()
nerf_module     = NerfExport(model).eval()

# --- Export wrapper 1: image -> triplane ------------------------------
dummy_image = torch.zeros(1, 3, 512, 512)
triplane_path = output_dir / "triplane.onnx"
print(f"Tracing image -> triplane -> {triplane_path}", flush=True)
torch.onnx.export(
    triplane_module,
    (dummy_image,),
    str(triplane_path),
    input_names=["image"],
    output_names=["triplane"],
    dynamic_axes={
        "image":    {0: "batch"},
        "triplane": {0: "batch"},
    },
    opset_version=17,
    do_constant_folding=True,
    # The weights blow past the 2 GB protobuf limit (or sit close enough
    # that it's fragile). Spill them to a sidecar .onnx_data file; ORT
    # picks it up automatically when both files share a directory.
    export_params=True,
)
# torch.onnx.export with external_data_format requires a follow-up save
# via the onnx package on torch < 2.4. On torch 2.4+ the kwarg works
# directly. Use the explicit save_as_external_data path to be portable.
import onnx
m = onnx.load(str(triplane_path), load_external_data=True)
onnx.save_model(
    m, str(triplane_path),
    save_as_external_data=True,
    all_tensors_to_one_file=True,
    location="triplane.onnx_data",
    size_threshold=1024,
    convert_attribute=False,
)

# --- Export wrapper 2: (triplane, xyz) -> (density, color_features) ----
# Dummy shapes pick a representative chunk size for tracing. Dynamic
# axes let inference vary the chunk dimension freely.
# Triplane channel count C varies by config; read it from a real forward
# pass so the dummy shape lines up with what TriplaneExport produces.
with torch.no_grad():
    dummy_triplane = triplane_module(dummy_image)
print(f"Triplane trace produced shape {tuple(dummy_triplane.shape)}", flush=True)

dummy_xyz = torch.zeros(65536, 3)
nerf_path = output_dir / "nerf.onnx"
print(f"Tracing (triplane, xyz) -> (density, color) -> {nerf_path}", flush=True)
torch.onnx.export(
    nerf_module,
    (dummy_triplane, dummy_xyz),
    str(nerf_path),
    input_names=["triplane", "xyz"],
    output_names=["density", "color"],
    dynamic_axes={
        "triplane": {0: "batch"},
        "xyz":      {0: "points"},
        "density":  {0: "points"},
        "color":    {0: "points"},
    },
    opset_version=17,
    do_constant_folding=True,
)

print("Export complete.", flush=True)
'@

$helperPath = Join-Path $env:TEMP "datum-export-triposr-$(Get-Random).py"
Set-Content -Path $helperPath -Encoding utf8 -Value $pythonHelper

$env:TRIPOSR_REPO = (Resolve-Path $TripoSrRepo).Path

Write-Host "Exporting $ModelId -> $OutputDirectory ..." -ForegroundColor Cyan
try {
    python $helperPath $ModelId $OutputDirectory
    $exportExit = $LASTEXITCODE
} finally {
    Remove-Item $helperPath -ErrorAction SilentlyContinue
}

if ($exportExit -ne 0) {
    Remove-Item $pypiReqTemp, $torchReqTemp -ErrorAction SilentlyContinue
    deactivate
    throw "torch.onnx.export failed with exit code $exportExit - no ONNX files were written."
}

# Mirror reproducibility artifacts into the output directory:
#   requirements.txt           - curated PyPI pin set (canonical)
#   requirements-torch.txt     - torch + torchvision (cu124 index)
#   requirements-freeze.txt    - full pip freeze, transitive closure
#   README.txt                 - what's here, how to recreate
#
# Anyone with just the ONNX files in this directory can rebuild the
# working environment from requirements.txt + requirements-torch.txt,
# or pin against requirements-freeze.txt for byte-identical wheels.
Copy-Item $pypiReqTemp  (Join-Path $OutputDirectory 'requirements.txt')       -Force
Copy-Item $torchReqTemp (Join-Path $OutputDirectory 'requirements-torch.txt') -Force
& $venvPython -m pip freeze | Out-File -Encoding utf8 (Join-Path $OutputDirectory 'requirements-freeze.txt')

# Capture the TripoSR repo commit so reproducers know which architecture
# revision the ONNX graphs were traced against.
$triposrCommit = (& git -C $TripoSrRepo rev-parse HEAD).Trim()
$triposrRemote = (& git -C $TripoSrRepo config --get remote.origin.url).Trim()

$readme = @"
TripoSR ONNX export -- reproducibility manifest
================================================

Produced by:   scripts/export-triposr.ps1 (DatumIngest)
Date (UTC):    $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))
Model:         $ModelId   (HuggingFace Hub)
Architecture:  $triposrRemote
  commit:      $triposrCommit

Files in this directory
-----------------------
  triplane.onnx              image[B,3,512,512] -> triplane[B,3,C,64,64]
  triplane.onnx_data         external weights for triplane.onnx (~1.6 GB)
  nerf.onnx                  (triplane, xyz[K,3]) -> (density[K], color[K,*])
  requirements.txt           PyPI pins used during export
  requirements-torch.txt     torch + torchvision (PyTorch cu124 index)
  requirements-freeze.txt    full pip freeze (transitive closure)

How to recreate the ONNX files from scratch
-------------------------------------------
  py -3.10 -m venv .venv
  .\.venv\Scripts\Activate.ps1
  pip install -r requirements.txt
  pip install -r requirements-torch.txt --index-url https://download.pytorch.org/whl/cu124
  git clone https://github.com/VAST-AI-Research/TripoSR.git
  cd TripoSR && git checkout $triposrCommit && cd ..
  # then run the trace -- see scripts/export-triposr.ps1 in DatumIngest

How to use the ONNX files at inference time
-------------------------------------------
1. Run triplane.onnx once per image to get the triplane tensor.
2. Chunk a (res * res * res, 3) xyz grid (typically 256^3 in chunks of
   ~256K points) and run nerf.onnx per chunk, accumulating density into
   a [res, res, res] float array.
3. Run marching cubes on the density grid on the host (DatumIngest's
   mesh_compute_* family) to extract vertices + indices.
4. Optionally run nerf.onnx one more time at the vertex positions to
   read per-vertex color from the `color` output.

Notes
-----
- The image input expects float32 in [0, 1], shape [B, 3, 512, 512].
  Resize and normalize on the host before invoking the ONNX graph.
- nerf.onnx uses grid_sample (opset 17). Use an ONNX Runtime build that
  supports opset 17 or higher (any 1.16+ release does).
- triplane.onnx_data must sit alongside triplane.onnx in the same
  directory; ORT loads external-data sidecars implicitly by path.

License
-------
TripoSR weights and architecture: MIT (Tochilkin et al., 2024).
"@

Set-Content -Path (Join-Path $OutputDirectory 'README.txt') -Encoding utf8 -Value $readme

# Temp requirements files no longer needed -- the canonical copies live
# in the output directory now.
Remove-Item $pypiReqTemp, $torchReqTemp -ErrorAction SilentlyContinue

# 6. Optional fp16 siblings. keep_io_types=True keeps the wire-boundary
#    tensors (image / triplane / xyz / density / color) in fp32, so the
#    DatumIngest inference layer doesn't need to know about half
#    precision -- only internal weights/activations run in fp16.
#    Matches the pattern in export-trocr-base-printed-fp16.ps1 +
#    export-zoedepth.ps1.
if ($Fp16) {
    foreach ($name in @('triplane', 'nerf')) {
        $fp32Path = Join-Path $OutputDirectory "$name.onnx"
        $fp16Path = Join-Path $OutputDirectory "${name}_fp16.onnx"
        Write-Host "Converting $fp32Path -> $fp16Path (fp16) ..." -ForegroundColor Cyan
        # triplane.onnx uses external data; load_external_data=True pulls
        # the sidecar in before conversion. We then save the fp16 sibling
        # with its own external-data file so the .onnx stays small.
        $convertScript = @"
import onnx
from onnxconverter_common import float16

m = onnx.load(r'$fp32Path', load_external_data=True)
m16 = float16.convert_float_to_float16(m, keep_io_types=True)
onnx.save_model(
    m16, r'$fp16Path',
    save_as_external_data=True,
    all_tensors_to_one_file=True,
    location=r'${name}_fp16.onnx_data',
    size_threshold=1024,
    convert_attribute=False,
)
"@
        & $venvPython -c $convertScript
        if ($LASTEXITCODE -ne 0) {
            deactivate
            throw "fp16 conversion of $name failed with exit code $LASTEXITCODE."
        }
    }
}

deactivate

Write-Host ''
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host 'Expected files:' -ForegroundColor DarkGray
Write-Host '  triplane.onnx              (graph; small)' -ForegroundColor DarkGray
Write-Host '  triplane.onnx_data         (weights; ~1.6 GB)' -ForegroundColor DarkGray
Write-Host '  nerf.onnx                  (graph + weights; small)' -ForegroundColor DarkGray
if ($Fp16) {
    Write-Host '  triplane_fp16.onnx + triplane_fp16.onnx_data' -ForegroundColor DarkGray
    Write-Host '  nerf_fp16.onnx' -ForegroundColor DarkGray
}
Write-Host '  requirements.txt           (PyPI pins; reproducibility)' -ForegroundColor DarkGray
Write-Host '  requirements-torch.txt     (torch + torchvision; cu124 index)' -ForegroundColor DarkGray
Write-Host '  requirements-freeze.txt    (pip freeze; transitive closure)' -ForegroundColor DarkGray
Write-Host '  README.txt                 (provenance + reproduction steps)' -ForegroundColor DarkGray
Write-Host "Verify with: Get-ChildItem $OutputDirectory" -ForegroundColor DarkGray
