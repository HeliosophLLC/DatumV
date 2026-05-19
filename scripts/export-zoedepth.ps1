# Exports ZoeDepth from upstream PyTorch weights to ONNX.
#
# Upstream: Intel/zoedepth-nyu-kitti (MIT, Bhat et al., 2023)
# Paper: "ZoeDepth: Zero-shot Transfer by Combining Relative and Metric Depth"
#
# ZoeDepth is the metric-depth counterpart to DPT-Large — same DPT-Large
# backbone with calibrated metric heads grafted on (NYU indoor depths +
# KITTI outdoor depths in this dual-head variant). Outputs depth in
# real-world units (meters), not just relative ordering. Use when scale
# matters: 3D point-cloud reconstruction at consistent scale across
# images, distance measurement, AR overlays.
#
# Intel publishes only safetensors / PyTorch weights at Intel/zoedepth-*;
# this script downloads those and converts to ONNX via Optimum, since
# the transformers library has a first-class ZoeDepthForDepthEstimation
# class as of 4.45+.
#
# Requirements:
#   - Python 3.10 (`py -3.10 --version`).
#   - ~3 GB free disk: ~1.3 GB PyTorch weights + ~1.3 GB ONNX output +
#     ~400 MB for transformer / optimum installs.
#   - Internet for the initial fetch.
#
# Idempotent: safe to rerun. Reuses .venv if present.
#
# Usage:
#   ./scripts/export-zoedepth.ps1
#       — exports to $env:DATUM_MODELS\zoedepth-staging\
#   ./scripts/export-zoedepth.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'zoedepth-staging'
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputDirectory <path>.'
        }
    ),

    # Override the upstream model id. The default is the NYU+KITTI dual-
    # head variant; Intel also publishes:
    #   - Intel/zoedepth-nyu     (indoor-only, ~5m max depth)
    #   - Intel/zoedepth-kitti   (outdoor-only, ~80m max depth)
    # The dual variant is the most general-purpose. Override if you
    # specifically want one domain.
    [Parameter()]
    [string]$ModelId = 'Intel/zoedepth-nyu-kitti',

    # If set, post-process the fp32 trace into an fp16 sibling
    # (model_fp16.onnx) using onnxconverter-common.float16. Cuts the
    # on-disk + VRAM footprint roughly in half. The fp32 model.onnx is
    # left in place so you can A/B or pick which to register.
    [Parameter()]
    [switch]$Fp16
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Resolve-Path "$PSScriptRoot\.."
$venvPython = Join-Path $repoRoot '.venv\Scripts\python.exe'

# 1. Project-local Python 3.10 venv at .venv (gitignored). Reused across
#    the other export-* scripts in this directory.
if (-not (Test-Path $venvPython)) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv (Join-Path $repoRoot '.venv')
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

& (Join-Path $repoRoot '.venv\Scripts\Activate.ps1')

# 2. Install conversion tooling, pinned to the same stack the other
#    optimum-cli-based export scripts use (see export-vit-gpt-image-captioning.ps1
#    for the long-form rationale on these pins).
#
#    Why uninstall first: pip won't reliably downgrade a satisfied
#    dependency, and the optimum 2.x → 1.24 transition trips on the
#    orphan optimum-onnx package (which assumes optimum~=2.1 and
#    breaks the CLI when 1.24 is in place). Wipe first, then install
#    at pinned versions.
#    Why torchvision is in the wipe list: transformers' image_utils imports
#    torchvision unconditionally. If a prior diffusion-export script
#    (export-sd-turbo, export-sdxl-turbo, etc.) left behind a torchvision
#    built against a different torch ABI, the next import explodes with
#    `operator torchvision::nms does not exist`. Wipe it and reinstall the
#    matching pair (torch 2.4 <-> torchvision 0.19) from the same cu124 index.
Write-Host 'Cleaning stale optimum / transformers / torchvision packages ...' -ForegroundColor Cyan
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers torchvision *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 + onnxconverter-common (pinned) ...' -ForegroundColor Cyan
pip install --quiet `
    'optimum[onnxruntime]==1.24.0' `
    'transformers==4.45.2' `
    'onnxconverter-common>=1.14' `
    sentencepiece `
    accelerate

# torch pin matches the rest of the export scripts. cu124 wheel; on
# machines without CUDA the resolver falls back to the CPU build.
# torchvision 0.19 is the matching ABI for torch 2.4 — install them in
# the same pip call so the resolver picks compatible wheels off cu124.
Write-Host 'Installing torch 2.4 + torchvision 0.19 (CUDA 12.4) ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'torch<2.5' 'torchvision<0.20' --index-url https://download.pytorch.org/whl/cu124

# 3. Stage output.
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# 4. Export. ZoeDepth is *not* in optimum 1.24's onnx-export model-type
#    registry (despite transformers shipping ZoeDepthForDepthEstimation
#    as of 4.45), so `optimum-cli export onnx` fails with "no custom
#    onnx configuration was passed". Skip optimum-cli and trace the
#    model directly with torch.onnx.export. The wrapper unwraps the
#    DepthEstimatorOutput dataclass so the exported graph has a single
#    tensor output (predicted_depth) instead of a structured return.
#
#    The export produces:
#       model.onnx                  (~1.3 GB; DPT-Large backbone weights
#                                    + ZoeDepth metric-bin head)
#       config.json
#       preprocessor_config.json
$pythonHelper = @'
import os, sys
from pathlib import Path
import torch
from transformers import ZoeDepthForDepthEstimation, ZoeDepthImageProcessor

model_id   = sys.argv[1]
output_dir = Path(sys.argv[2])
output_dir.mkdir(parents=True, exist_ok=True)

print(f"Loading {model_id} ...", flush=True)
processor = ZoeDepthImageProcessor.from_pretrained(model_id)
model     = ZoeDepthForDepthEstimation.from_pretrained(model_id).eval()

# ZoeDepth.forward returns a DepthEstimatorOutput dataclass; torch.onnx
# can't trace through dataclass attribute access cleanly, so wrap to
# return the bare tensor.
class Wrapper(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m
    def forward(self, pixel_values):
        return self.m(pixel_values=pixel_values).predicted_depth

wrapped = Wrapper(model).eval()

# ZoeDepth requires H and W divisible by 32. 384x512 is the canonical
# trace size for nyu-kitti; dynamic axes below let inference accept any
# 32-aligned shape.
dummy = torch.randn(1, 3, 384, 512)

onnx_path = output_dir / "model.onnx"
print(f"Tracing -> {onnx_path}", flush=True)
torch.onnx.export(
    wrapped,
    (dummy,),
    str(onnx_path),
    input_names=["pixel_values"],
    output_names=["predicted_depth"],
    dynamic_axes={
        "pixel_values":     {0: "batch", 2: "height", 3: "width"},
        "predicted_depth":  {0: "batch", 1: "height", 2: "width"},
    },
    opset_version=17,
    do_constant_folding=True,
)

processor.save_pretrained(output_dir)
model.config.save_pretrained(output_dir)
print("Export complete.", flush=True)
'@

$helperPath = Join-Path $env:TEMP "datum-export-zoedepth-$(Get-Random).py"
Set-Content -Path $helperPath -Encoding utf8 -Value $pythonHelper

Write-Host "Exporting $ModelId -> $OutputDirectory ..." -ForegroundColor Cyan
try {
    python $helperPath $ModelId $OutputDirectory
    $exportExit = $LASTEXITCODE
} finally {
    Remove-Item $helperPath -ErrorAction SilentlyContinue
}

# python is a native exe — $ErrorActionPreference='Stop' doesn't catch
# native exit codes. Explicit check so a failed export doesn't get
# masked by a misleading "Done" message.
if ($exportExit -ne 0) {
    deactivate
    throw "torch.onnx.export failed with exit code $exportExit - no ONNX files were written."
}

# 5. Optional fp16 sibling. keep_io_types=True leaves pixel_values /
#    predicted_depth as fp32 at the wire boundary, so the Heliosoph.DatumV
#    inference layer doesn't need to know about half precision — only
#    the internal DPT-Large weights + metric-bin head activations run
#    in fp16. Same pattern as export-trocr-base-printed-fp16.ps1.
if ($Fp16) {
    $fp32Path = Join-Path $OutputDirectory 'model.onnx'
    $fp16Path = Join-Path $OutputDirectory 'model_fp16.onnx'
    Write-Host "Converting $fp32Path -> $fp16Path (fp16) ..." -ForegroundColor Cyan
    $convertScript = @"
import onnx
from onnxconverter_common import float16

m = onnx.load(r'$fp32Path')
m16 = float16.convert_float_to_float16(m, keep_io_types=True)
onnx.save(m16, r'$fp16Path')
"@
    & $venvPython -c $convertScript
    if ($LASTEXITCODE -ne 0) {
        deactivate
        throw "fp16 conversion failed with exit code $LASTEXITCODE."
    }
}

# 6. Tidy up — leave the parent shell in the same state it started.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem $OutputDirectory" -ForegroundColor DarkGray
