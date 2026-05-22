# Re-exports SwinIR from upstream PyTorch weights to ONNX.
#
# Upstream: github.com/JingyunLiang/SwinIR (Apache-2.0, Liang et al., 2021).
# The pre-existing wuminghao/swinir HF repo is a personal account — we
# re-export from authoritative upstream so the provenance trail and the
# .onnx files we ship are entirely under our control.
#
# What this exports (matches the wuminghao two-file shape):
#   swinir_realsr_x4.onnx          ← real-world image super-resolution (4x)
#                                    SwinIR-L config, 64x64 input -> 256x256 output
#   swinir_denoising_color_25.onnx ← color denoising at noise sigma=25
#                                    SwinIR-M config, 128x128 input
#
# Requirements:
#   - Python 3.10 (`py -3.10 --version`).
#   - git on PATH (we clone JingyunLiang/SwinIR for its model class).
#   - ~500 MB free disk: ~300 MB for weights + ~150 MB for ONNX outputs +
#     ~50 MB for the cloned source.
#   - Internet for the initial fetch (.pth weights from upstream releases
#     and the git clone). Subsequent runs reuse the cache.
#
# Notes on ONNX export shape:
#   - SwinIR's windowed attention requires spatial dims be multiples of
#     window_size=8. Dynamic H/W axes work in principle but are brittle
#     across ORT versions for the window-shift op, so we pin spatial dims
#     to the training img_size (64 for SR, 128 for denoising). The model
#     class supports any H/W >= window_size at runtime; for arbitrary
#     image sizes, your consumer should tile the input.
#   - Batch size is dynamic (axis 0) — runtime can batch as needed.
#
# Idempotent: safe to rerun. Reuses .venv, the cloned SwinIR source, and
# the cached checkpoints.
#
# Usage:
#   ./scripts/export-swinir.ps1
#       — exports both files to $env:DATUMV_MODELS\swinir-staging\
#   ./scripts/export-swinir.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'swinir-staging'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
        }
    )
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Resolve-Path "$PSScriptRoot\.."
$venvPython = Join-Path $repoRoot '.venv\Scripts\python.exe'
$sourceDir  = Join-Path $repoRoot '.cache\swinir-source'
$weightsDir = Join-Path $repoRoot '.cache\swinir-weights'

# 1. Shared project venv (reused across export scripts). Created on first
#    use, kept gitignored.
if (-not (Test-Path $venvPython)) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv (Join-Path $repoRoot '.venv')
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

& (Join-Path $repoRoot '.venv\Scripts\Activate.ps1')

# 2. Dependencies. SwinIR needs torch + timm (for the SwinTransformerBlock
#    base class) + numpy + onnx. We're additive — no uninstall of
#    transformers/optimum because the other export scripts pin those and
#    SwinIR doesn't conflict. Skip install if already present.
Write-Host 'Ensuring torch / timm / onnx are installed ...' -ForegroundColor Cyan
pip install --quiet `
    'torch<2.5' `
    --index-url https://download.pytorch.org/whl/cu124
pip install --quiet timm numpy onnx 'onnxruntime>=1.17'

# 3. Clone the upstream SwinIR source for its model class. Pin to a known
#    commit so the model architecture matches the checkpoints we'll
#    download. Shallow clone — we only need the network definition file.
if (-not (Test-Path (Join-Path $sourceDir 'models\network_swinir.py'))) {
    Write-Host "Cloning JingyunLiang/SwinIR to $sourceDir ..." -ForegroundColor Cyan
    git clone --depth 1 https://github.com/JingyunLiang/SwinIR.git $sourceDir
} else {
    Write-Host 'SwinIR source already cached.' -ForegroundColor DarkGray
}

# 4. Download checkpoints if not already cached. The URLs are pinned to
#    the v0.0 release on the upstream repo — that release contains all
#    the published checkpoints and hasn't moved since 2021.
New-Item -ItemType Directory -Force -Path $weightsDir | Out-Null

$checkpoints = @(
    @{
        Name = '003_realSR_BSRGAN_DFOWMFC_s64w8_SwinIR-L_x4_GAN.pth'
        Url  = 'https://github.com/JingyunLiang/SwinIR/releases/download/v0.0/003_realSR_BSRGAN_DFOWMFC_s64w8_SwinIR-L_x4_GAN.pth'
    },
    @{
        Name = '005_colorDN_DFWB_s128w8_SwinIR-M_noise25.pth'
        Url  = 'https://github.com/JingyunLiang/SwinIR/releases/download/v0.0/005_colorDN_DFWB_s128w8_SwinIR-M_noise25.pth'
    }
)

foreach ($ckpt in $checkpoints) {
    $dest = Join-Path $weightsDir $ckpt.Name
    if (-not (Test-Path $dest)) {
        Write-Host "Downloading $($ckpt.Name) ..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $ckpt.Url -OutFile $dest
    } else {
        Write-Host "$($ckpt.Name) already cached." -ForegroundColor DarkGray
    }
}

# 5. Stage the output directory.
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# 6. Run the Python export. The here-string captures the script verbatim;
#    single-quoted '@ ... '@ means no PowerShell variable interpolation,
#    so the Python code stays literal. We pass paths via environment
#    variables since that's the cleanest cross-platform channel.
$env:SWINIR_SOURCE  = $sourceDir
$env:SWINIR_WEIGHTS = $weightsDir
$env:SWINIR_OUTPUT  = $OutputDirectory

$exportScript = @'
import inspect
import os
import sys
import torch

# Add SwinIR's models/ to sys.path so we can import its network class.
sys.path.insert(0, os.environ['SWINIR_SOURCE'])
from models.network_swinir import SwinIR

weights_dir = os.environ['SWINIR_WEIGHTS']
output_dir  = os.environ['SWINIR_OUTPUT']

# Force the legacy TorchScript-based ONNX exporter in torch >=2.5 where
# dynamo became the default. The dynamo path can't reconcile SwinIR's
# `.type_as()` calls (used for dtype coercion of normalization buffers)
# — it loses track of constant names and raises:
#   ValueError: Key 'c_mean' does not match the name of the value 'type_as'.
# The legacy exporter handles this fine. On older torch (2.4 and below)
# the `dynamo` kwarg doesn't exist, so we feature-detect.
_extra_export_kwargs = {}
if 'dynamo' in inspect.signature(torch.onnx.export).parameters:
    _extra_export_kwargs['dynamo'] = False

# ---------- Variant 1: Real-world Image Super-Resolution (x4) ----------
# Architecture per the upstream main_test_swinir.py for task='real_sr',
# scale=4, large_model=True. 64x64 input -> 256x256 output.
print('Exporting swinir_realsr_x4.onnx ...')
model_sr = SwinIR(
    upscale=4, in_chans=3, img_size=64, window_size=8,
    img_range=1.0,
    depths=[6, 6, 6, 6, 6, 6, 6, 6, 6],
    embed_dim=240,
    num_heads=[8, 8, 8, 8, 8, 8, 8, 8, 8],
    mlp_ratio=2,
    upsampler='nearest+conv',
    resi_connection='3conv',
)
state_sr = torch.load(
    os.path.join(weights_dir, '003_realSR_BSRGAN_DFOWMFC_s64w8_SwinIR-L_x4_GAN.pth'),
    map_location='cpu',
)
# SwinIR checkpoints wrap weights under 'params_ema' (or 'params') —
# unwrap if present so load_state_dict sees a flat dict.
if 'params_ema' in state_sr:
    state_sr = state_sr['params_ema']
elif 'params' in state_sr:
    state_sr = state_sr['params']
model_sr.load_state_dict(state_sr, strict=True)
model_sr.eval()

dummy_sr = torch.randn(1, 3, 64, 64)
torch.onnx.export(
    model_sr,
    dummy_sr,
    os.path.join(output_dir, 'swinir_realsr_x4.onnx'),
    input_names=['image'],
    output_names=['upscaled'],
    # Batch is dynamic; spatial dims are pinned to 64 (window_size constraint).
    # Consumers tile larger inputs.
    dynamic_axes={'image': {0: 'batch'}, 'upscaled': {0: 'batch'}},
    opset_version=17,
    do_constant_folding=True,
    **_extra_export_kwargs,
)

# ---------- Variant 2: Color Denoising (noise sigma = 25) ----------
# SwinIR-M config from main_test_swinir.py task='color_dn'.
# 128x128 input -> 128x128 output (no upscaling).
print('Exporting swinir_denoising_color_25.onnx ...')
model_dn = SwinIR(
    upscale=1, in_chans=3, img_size=128, window_size=8,
    img_range=1.0,
    depths=[6, 6, 6, 6, 6, 6],
    embed_dim=180,
    num_heads=[6, 6, 6, 6, 6, 6],
    mlp_ratio=2,
    upsampler='',
    resi_connection='1conv',
)
state_dn = torch.load(
    os.path.join(weights_dir, '005_colorDN_DFWB_s128w8_SwinIR-M_noise25.pth'),
    map_location='cpu',
)
if 'params_ema' in state_dn:
    state_dn = state_dn['params_ema']
elif 'params' in state_dn:
    state_dn = state_dn['params']
model_dn.load_state_dict(state_dn, strict=True)
model_dn.eval()

dummy_dn = torch.randn(1, 3, 128, 128)
torch.onnx.export(
    model_dn,
    dummy_dn,
    os.path.join(output_dir, 'swinir_denoising_color_25.onnx'),
    input_names=['image'],
    output_names=['denoised'],
    dynamic_axes={'image': {0: 'batch'}, 'denoised': {0: 'batch'}},
    opset_version=17,
    do_constant_folding=True,
    **_extra_export_kwargs,
)

print('Done.')
'@

$tmpPy = Join-Path $env:TEMP "swinir-export-$(Get-Random).py"
Set-Content -Path $tmpPy -Value $exportScript -Encoding UTF8

try {
    Write-Host "Running ONNX export to $OutputDirectory ..." -ForegroundColor Cyan
    & $venvPython $tmpPy
    if ($LASTEXITCODE -ne 0) {
        throw "ONNX export failed with exit code $LASTEXITCODE - check output above for traceback."
    }
} finally {
    Remove-Item -Path $tmpPy -ErrorAction SilentlyContinue
    deactivate
}

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem $OutputDirectory" -ForegroundColor DarkGray
