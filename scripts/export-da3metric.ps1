# Exports Depth Anything 3 Metric (DA3METRIC-LARGE) from upstream
# safetensors to ONNX.
#
# Upstream: depth-anything/DA3METRIC-LARGE (Apache-2.0, ByteDance-Seed,
#           "Depth Anything 3: Recovering the Visual Space from Any Views")
# Code:     github.com/ByteDance-Seed/Depth-Anything-3 (pip: depth-anything-3)
#
# DA3METRIC is the metric-depth mono variant of the DA3 family. The HF repo
# ships only safetensors + config.json (loaded via the official
# depth_anything_3 package, not transformers), so we trace it to ONNX
# ourselves.
#
# Output contract of the exported graph:
#   - input  "image": float32 [batch, 3, H, W], ImageNet-normalized
#     (mean [0.485, 0.456, 0.406], std [0.229, 0.224, 0.225]). Batch is
#     dynamic; H and W are pinned to the trace resolution (see -Height /
#     -Width below) — resize inputs to match.
#   - output "depth": canonical depth. Metric meters are recovered as
#         metric_depth = depth * focal_px / 300
#     (per the upstream README), so the consumer needs the focal length /
#     fov of the source image.
#   - output "sky" (if the checkpoint produces it): sky-region logits,
#     threshold > 0 to mask sky pixels.
#   Exact output names/shapes are probed from the checkpoint at export
#   time and printed.
#
# Why not trace DepthAnything3.forward as-is: upstream wraps the body in
# torch.autocast(bfloat16) — ONNX has no usable bf16 story — and decorates
# it with @torch.inference_mode(), which the tracer can trip over. The
# embedded python below neutralizes the autocast (full fp32 trace) and
# calls the undecorated forward, instead of hand-patching the installed
# package the way the community exports do.
#
# Requirements:
#   - Python 3.10 (`py -3.10 --version`).
#   - ~4 GB free disk: ~1.4 GB safetensors download + ~1.4 GB ONNX output
#     + installs.
#   - Internet for the initial fetch. Subsequent runs reuse the HF cache.
#
# Idempotent: safe to rerun. Reuses .venv if present.
#
# Usage:
#   ./scripts/export-da3metric.ps1
#       — exports to $env:DATUMV_MODELS\da3metric-staging\
#   ./scripts/export-da3metric.ps1 -OutputDirectory C:\foo -Height 280 -Width 504
#   ./scripts/export-da3metric.ps1 -Fp16
#       — also writes a model_fp16.onnx sibling

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'da3metric-staging'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
        }
    ),

    # HF model id or a local directory containing model.safetensors +
    # config.json. DA3METRIC-LARGE is the only Apache-licensed metric
    # variant; the non-metric DA3 sizes need the multi-view pipeline and
    # are exported differently.
    [Parameter()]
    [string]$ModelId = 'depth-anything/DA3METRIC-LARGE',

    # Trace resolution. Must be divisible by 14 (ViT-L patch size).
    # Spatial dims are PINNED in the exported graph: DINOv2's position-
    # embedding interpolation bakes the patch-token count into the trace
    # (verified — a 280x504 input against a 504x504 trace dies in ORT on
    # a 721-vs-1297 broadcast). Batch stays dynamic. Re-run with
    # -Height/-Width to produce a graph for a different resolution;
    # consumers resize inputs to the trace shape.
    [Parameter()]
    [int]$Height = 504,

    [Parameter()]
    [int]$Width = 504,

    # If set, post-process the fp32 trace into an fp16 sibling
    # (model_fp16.onnx) using onnxconverter-common.float16, keep_io_types
    # so the inference layer still sees fp32 at the wire boundary. Same
    # pattern as export-zoedepth.ps1.
    [Parameter()]
    [switch]$Fp16
)

$ErrorActionPreference = 'Stop'

if (($Height % 14) -ne 0 -or ($Width % 14) -ne 0) {
    throw "Height and Width must be divisible by 14 (got ${Height}x${Width})."
}

$repoRoot   = Resolve-Path "$PSScriptRoot\.."
$venvPython = Join-Path $repoRoot '.venv\Scripts\python.exe'

# 1. Shared project venv (reused across export scripts). Created on first
#    use, kept gitignored.
if (-not (Test-Path $venvPython)) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv (Join-Path $repoRoot '.venv')
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

& (Join-Path $repoRoot '.venv\Scripts\Activate.ps1')

# 2. Dependencies. Additive like export-swinir.ps1 — no wipe of the
#    optimum/transformers pins the other scripts manage. torch pin matches
#    the rest of the export scripts (cu124 wheel, CPU fallback off-CUDA).
#
#    depth-anything-3 goes in with --no-deps: its declared dependency list
#    hard-requires xformers (no Windows wheel for this torch/python combo,
#    so pip attempts a flash-attention source build and dies in cl.exe)
#    plus a pile of demo/eval-only packages (open3d, evo, pycolmap,
#    fastapi, moviepy, gradio ...). The actual model code only touches
#    xformers inside a try/except ImportError with a pure-torch SwiGLU
#    fallback, so none of that is needed for inference/export. The curated
#    list below is what the api + dinov2 + metric-head import path really
#    uses.
Write-Host 'Ensuring torch / depth-anything-3 / onnx are installed ...' -ForegroundColor Cyan
pip install --quiet 'torch<2.5' 'torchvision<0.20' --index-url https://download.pytorch.org/whl/cu124
pip install --quiet --no-deps 'depth-anything-3==0.1.1'
#    api.py transitively imports moviepy 1.x, addict, matplotlib and evo at
#    module level (video/GS-export and pose-align utils we never call, but
#    the imports still have to resolve). addict/matplotlib aren't even in
#    the upstream dependency list — they normally arrive via open3d/evo.
pip install --quiet `
    einops `
    'numpy<2' `
    opencv-python `
    omegaconf `
    pillow `
    safetensors `
    huggingface_hub `
    e3nn `
    trimesh `
    imageio `
    plyfile `
    'moviepy==1.0.3' `
    addict `
    matplotlib `
    evo `
    onnx `
    'onnxruntime>=1.17' `
    'onnxconverter-common>=1.14'

# 3. Stage the output directory.
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# 4. Run the Python export. Single-quoted here-string: no PowerShell
#    interpolation, parameters travel via environment variables.
$env:DA3_MODEL_ID = $ModelId
$env:DA3_OUTPUT   = $OutputDirectory
$env:DA3_HEIGHT   = $Height
$env:DA3_WIDTH    = $Width

$exportScript = @'
import inspect
import os
import shutil
import sys

import numpy as np
import torch

model_id   = os.environ['DA3_MODEL_ID']
output_dir = os.environ['DA3_OUTPUT']
H          = int(os.environ['DA3_HEIGHT'])
W          = int(os.environ['DA3_WIDTH'])

# Neutralize autocast BEFORE importing depth_anything_3, in case it binds
# a local reference at import time. Upstream api.py opens
# torch.autocast(dtype=bfloat16|float16) inside forward; ONNX export needs
# a clean fp32 trace, so autocast becomes a no-op context manager.
class _NullAutocast:
    def __init__(self, *args, **kwargs):
        pass
    def __enter__(self):
        return None
    def __exit__(self, *exc):
        return False

torch.autocast = _NullAutocast
torch.amp.autocast = _NullAutocast

from depth_anything_3.api import DepthAnything3

print(f'Loading {model_id} ...', flush=True)
model = DepthAnything3.from_pretrained(model_id).to('cpu').eval()

# Upstream decorates forward with @torch.inference_mode(); the decorator
# is functools.wraps-based, so __wrapped__ recovers the plain function.
# Tracing under inference_mode is flaky across torch versions — bypass it.
raw_forward = getattr(DepthAnything3.forward, '__wrapped__', DepthAnything3.forward)

def run(image):
    # DA3 consumes [batch, views, 3, H, W]; the metric mono variant is
    # single-view, so the wrapper owns the views=1 unsqueeze.
    return raw_forward(
        model, image.unsqueeze(1),
        extrinsics=None, intrinsics=None,
        export_feat_layers=[], infer_gs=False,
    )

# Probe the checkpoint for its actual output dict before committing to
# ONNX output names. Metric checkpoints emit depth (+ sky, + confidence
# depending on head config).
print(f'Probing forward pass at {H}x{W} ...', flush=True)
dummy = torch.randn(1, 3, H, W)
with torch.no_grad():
    probe = run(dummy)

tensor_keys = [k for k, v in probe.items() if isinstance(v, torch.Tensor)]
preferred   = ['depth', 'conf', 'confidence', 'sky']
out_keys    = [k for k in preferred if k in tensor_keys]
out_keys   += [k for k in sorted(tensor_keys) if k not in out_keys]
if 'depth' not in out_keys:
    print(f'FATAL: no "depth" in forward outputs {sorted(probe.keys())}', file=sys.stderr)
    sys.exit(2)

for k in out_keys:
    print(f'  output {k}: shape {tuple(probe[k].shape)} dtype {probe[k].dtype}')

class Wrapper(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.model = model
    def forward(self, image):
        out = run(image)
        return tuple(out[k].float() for k in out_keys)

wrapped = Wrapper().eval()

# Dynamic axes: batch only. Spatial dims stay static — the ViT position-
# embedding interpolation traces to a constant token count, so a graph
# traced at HxW is only valid at HxW (confirmed empirically: ORT raises a
# broadcast error on any other resolution). Batch>1 correctness is
# validated below.
dynamic_axes = {'image': {0: 'batch'}}
for k in out_keys:
    dynamic_axes[k] = {0: 'batch'}

# Force the legacy TorchScript exporter where torch made dynamo the
# default — same feature-detect as export-swinir.ps1.
extra_kwargs = {}
if 'dynamo' in inspect.signature(torch.onnx.export).parameters:
    extra_kwargs['dynamo'] = False

onnx_path = os.path.join(output_dir, 'model.onnx')
print(f'Tracing -> {onnx_path}', flush=True)
torch.onnx.export(
    wrapped,
    (dummy,),
    onnx_path,
    input_names=['image'],
    output_names=out_keys,
    dynamic_axes=dynamic_axes,
    opset_version=17,
    do_constant_folding=True,
    **extra_kwargs,
)

import onnx
onnx.checker.check_model(onnx_path)

# ---- Validation against onnxruntime ----------------------------------
# 1. fp32 parity at the trace resolution (torch vs ORT).
# 2. batch=2 consistency — a duplicated image must reproduce the batch=1
#    result per-item (this is the failure mode that shipped a scrambled
#    SwinIR export; see export-swinir.ps1's pinned-batch notes).
import onnxruntime as ort

sess = ort.InferenceSession(onnx_path, providers=['CPUExecutionProvider'])

def rel_err(a, b):
    return float(np.max(np.abs(a - b)) / (np.max(np.abs(a)) + 1e-6))

def check(name, torch_outs, ort_outs, tol=5e-3):
    worst = max(
        rel_err(t.detach().numpy(), o)
        for t, o in zip(torch_outs, ort_outs)
    )
    status = 'PASS' if worst <= tol else 'FAIL'
    print(f'  [{status}] {name}: max relative error {worst:.2e}')
    return worst <= tol

ok = True

ort_outs = sess.run(out_keys, {'image': dummy.numpy()})
with torch.no_grad():
    torch_outs = wrapped(dummy)
ok &= check(f'parity @ {H}x{W} batch=1', torch_outs, ort_outs)

batched = torch.cat([dummy, dummy], dim=0)
ort_b2 = sess.run(out_keys, {'image': batched.numpy()})
ok &= check('batch=2 item[0] vs batch=1', torch_outs, [o[0:1] for o in ort_b2])
ok &= check('batch=2 item[1] vs batch=1', torch_outs, [o[1:2] for o in ort_b2])

if not ok:
    print('Validation FAILED — do not register this export.', file=sys.stderr)
    sys.exit(3)

# Carry the upstream config.json alongside the ONNX for provenance.
if os.path.isdir(model_id):
    src_config = os.path.join(model_id, 'config.json')
    if os.path.isfile(src_config):
        shutil.copy(src_config, os.path.join(output_dir, 'config.json'))
else:
    from huggingface_hub import hf_hub_download
    shutil.copy(hf_hub_download(model_id, 'config.json'),
                os.path.join(output_dir, 'config.json'))

print('Export complete.', flush=True)
'@

$tmpPy = Join-Path $env:TEMP "da3metric-export-$(Get-Random).py"
Set-Content -Path $tmpPy -Value $exportScript -Encoding UTF8

try {
    Write-Host "Exporting $ModelId -> $OutputDirectory ..." -ForegroundColor Cyan
    & $venvPython $tmpPy
    if ($LASTEXITCODE -ne 0) {
        throw "ONNX export failed with exit code $LASTEXITCODE - check output above for traceback."
    }
} finally {
    Remove-Item -Path $tmpPy -ErrorAction SilentlyContinue
}

# 5. Optional fp16 sibling, keep_io_types=True so the inference layer
#    keeps talking fp32 at the boundary. Parity-checked against the fp32
#    graph — fp16 conversion is where overflow (>65504 activations) and
#    precision loss bite, and a depth model that silently saturates is
#    worse than a big one.
if ($Fp16) {
    $fp32Path = Join-Path $OutputDirectory 'model.onnx'
    $fp16Path = Join-Path $OutputDirectory 'model_fp16.onnx'
    Write-Host "Converting $fp32Path -> $fp16Path (fp16) ..." -ForegroundColor Cyan
    $convertScript = @"
import sys
import numpy as np
import onnx
from onnx import TensorProto
from onnxconverter_common import float16

m = onnx.load(r'$fp32Path')
m16 = float16.convert_float_to_float16(m, keep_io_types=True)

# onnxconverter-common retypes value_infos to fp16 but leaves the model's
# pre-existing Cast(to=FLOAT) attributes untouched (DA3's DINOv2 upcasts
# pos-embeds for interpolation), which ORT rejects as a type mismatch.
# Realign each Cast's `to` with the declared type of its output tensor;
# casts whose value_info already agrees (the converter's own boundary
# casts around blocked ops like Resize, and the keep_io_types graph-output
# casts) are untouched.
vi_type = {}
for vi in list(m16.graph.value_info) + list(m16.graph.output) + list(m16.graph.input):
    vi_type[vi.name] = vi.type.tensor_type.elem_type
for node in m16.graph.node:
    if node.op_type != 'Cast':
        continue
    declared = vi_type.get(node.output[0])
    if declared not in (TensorProto.FLOAT, TensorProto.FLOAT16):
        continue
    for attr in node.attribute:
        if attr.name == 'to' and attr.i in (TensorProto.FLOAT, TensorProto.FLOAT16):
            attr.i = declared

onnx.save(m16, r'$fp16Path')

import onnxruntime as ort
s32 = ort.InferenceSession(r'$fp32Path', providers=['CPUExecutionProvider'])
s16 = ort.InferenceSession(r'$fp16Path', providers=['CPUExecutionProvider'])
shape = [d if isinstance(d, int) else 1 for d in s32.get_inputs()[0].shape]
rng = np.random.default_rng(0)
x = rng.standard_normal(shape, dtype=np.float32)
names = [o.name for o in s32.get_outputs()]
o32 = s32.run(names, {'image': x})
o16 = s16.run(names, {'image': x})
ok = True
for n, a, b in zip(names, o32, o16):
    if not np.all(np.isfinite(b)):
        print(f'  [FAIL] fp16 {n}: non-finite values (overflow)')
        ok = False
        continue
    err = float(np.max(np.abs(a - b)) / (np.max(np.abs(a)) + 1e-6))
    status = 'PASS' if err <= 2e-2 else 'FAIL'
    print(f'  [{status}] fp16 vs fp32 {n}: max relative error {err:.2e}')
    ok &= err <= 2e-2
if not ok:
    print('fp16 validation FAILED - use the fp32 model.', file=sys.stderr)
    sys.exit(3)
"@
    & $venvPython -c $convertScript
    if ($LASTEXITCODE -ne 0) {
        deactivate
        throw "fp16 conversion/validation failed with exit code $LASTEXITCODE."
    }
}

# 6. Tidy up — leave the parent shell in the same state it started.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Remember: metric depth = depth output * focal_px / 300." -ForegroundColor DarkGray
