# Exports DreamShaper 8 (SD 1.5 finetune) to ONNX — NO distillation LoRA.
#
# This is the QUALITY counterpart to export-dreamshaper-hyper.ps1. It is the
# same model and the same diffusers folder layout, with one difference: the
# Hyper-SD 4-step LoRA is NOT fused in. The result is the plain, non-distilled
# UNet, which is driven by the dreamshaper_cfg SQL body with classifier-free
# guidance over ~25 steps (see models/sql/dreamshaper-cfg/2026-06-30.sql).
#
# Why a separate export:
#   - The Hyper LoRA distills the model for 1-4 step, CFG-free sampling. That
#     is fast but caps fidelity and prompt adherence.
#   - The non-distilled model with CFG (guidance ~7.5) + a negative prompt +
#     20-30 steps is what matches the polished gallery output people compare
#     against. Slower per image, much higher quality.
#
# DreamShaper 8 (Lykon) ships with its own VAE baked in, so there is no
# separate VAE pairing step (unlike Realistic Vision, which is noVAE).
#
# Pipeline:
#   1. Load DreamShaper 8 via diffusers (uses its bundled VAE, no LoRA)
#   2. Save the pipeline to a temp directory
#   3. Run optimum-cli export onnx on it
#
# Requirements:
#   - Python 3.10 venv at .venv\ (the existing one works)
#   - ~3 GB free disk for the download + ~2 GB for the ONNX output
#   - Internet connection
#   - 5-10 minutes (UNet export is the bottleneck)
#
# Idempotent: safe to rerun. Reuses an existing .venv.
#
# Usage:
#   ./scripts/export-dreamshaper-cfg.ps1
#       — exports to $env:DATUMV_MODELS\dreamshaper-cfg-onnx (FP32)
#   ./scripts/export-dreamshaper-cfg.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory
#
# Note: FP32 only. This is the quality tier; the SD 1.5 VAE is fp16-fragile
# (it overflows and produces posterized output), so the quality export stays
# fp32 deliberately. Use the Hyper variant if you need the smaller footprint.

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'dreamshaper-cfg-onnx'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
        }
    )
)

$ErrorActionPreference = 'Stop'

# 1. Project-local Python 3.10 venv. Reuse if present.
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

# 2. Activate.
& .\.venv\Scripts\Activate.ps1

# 3. Install conversion tooling + CUDA torch.
#
# Same pinned stack as the other diffusion export scripts:
#   - optimum 1.24.0: last release with the [diffusers] extra; 2.x broke API
#   - transformers 4.45.2 + diffusers 0.31.0: known-good with optimum 1.24
#   - onnxscript: torch >=2.5 needs it for ONNX export
#   - accelerate: faster pipeline loads, silences low-cpu-mem warning
#
# No peft here — this export does not fuse a LoRA.
#
# torch is installed explicitly from the PyTorch CUDA index so pip doesn't
# silently substitute the CPU-only wheel.
Write-Host 'Cleaning stale packages from prior export attempts ...' -ForegroundColor Cyan
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers diffusers *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 + diffusers 0.31 + onnxscript + accelerate ...' -ForegroundColor Cyan
pip install --quiet `
    'optimum[onnxruntime-gpu,diffusers]==1.24.0' `
    'transformers==4.45.2' `
    'diffusers==0.31.0' `
    onnxscript `
    accelerate

Write-Host 'Installing CUDA torch 2.4 (legacy exporter) from pytorch cu124 index ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'torch<2.5' --index-url https://download.pytorch.org/whl/cu124

# 3a. Verify BOTH torch CUDA AND ORT CUDAExecutionProvider are usable.
$cudaCheck = & .\.venv\Scripts\python.exe -c @"
import torch, onnxruntime
torch_ok = torch.cuda.is_available()
ort_ok = 'CUDAExecutionProvider' in onnxruntime.get_available_providers()
print(f'{torch_ok},{ort_ok},{onnxruntime.get_available_providers()}')
"@ 2>&1
if ($LASTEXITCODE -ne 0 -or -not $cudaCheck.Trim().StartsWith('True,True,')) {
    Write-Host ''
    Write-Host 'ERROR: GPU runtime not available after install.' -ForegroundColor Red
    Write-Host "Detected: $cudaCheck" -ForegroundColor Red
    Write-Host 'Possible causes: no NVIDIA GPU, outdated driver (need 525+), CUDA toolkit mismatch,' -ForegroundColor Red
    Write-Host 'or onnxruntime CPU package still resident (try: pip uninstall onnxruntime onnxruntime-gpu, then rerun).' -ForegroundColor Red
    deactivate
    exit 1
}

# 4. Load DreamShaper 8 (with its bundled VAE) and save a non-distilled
#    pipeline that optimum-cli can export. No LoRA fusion here — that is the
#    whole point of this variant.
$mergedDir = Join-Path $env:TEMP "dreamshaper-cfg-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Write-Host "Preparing DreamShaper 8 (bundled VAE, no LoRA) at $mergedDir ..." -ForegroundColor Cyan

$prepScript = @"
import sys, torch
from diffusers import StableDiffusionPipeline

merged_dir = sys.argv[1]

print('Loading DreamShaper 8 base (with its bundled VAE) ...')
pipe = StableDiffusionPipeline.from_pretrained(
    'Lykon/dreamshaper-8',
    torch_dtype=torch.float32,
    safety_checker=None,
    requires_safety_checker=False,
)

# No LoRA fusion: this is the full, non-distilled UNet. CFG sampling is done
# at inference time by the dreamshaper_cfg SQL body.
print(f'Saving pipeline to {merged_dir} ...')
pipe.save_pretrained(merged_dir)
print('Done preparing.')
"@

$tmpPrep = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.py'
Set-Content -Path $tmpPrep -Value $prepScript -Encoding utf8
& .\.venv\Scripts\python.exe $tmpPrep $mergedDir
$prepExit = $LASTEXITCODE
Remove-Item $tmpPrep -Force
if ($prepExit -ne 0) {
    Write-Host ''
    Write-Host "ERROR: prepare step failed (exit $prepExit). The pipeline was not produced." -ForegroundColor Red
    if (Test-Path $mergedDir) { Remove-Item -Recurse -Force $mergedDir }
    deactivate
    exit 1
}

# 5. Convert the pipeline to ONNX (FP32).
#
# --device cuda     forces export + validation to GPU.
# --no-post-process skips ONNX graph constant-folding (NOT numerical validation).
# Opset is left at optimum's default (14).
Write-Host "Exporting pipeline to $OutputDirectory (FP32, ~3.4 GB UNet) ..." -ForegroundColor Cyan
Write-Host "(this takes 5-10 minutes; UNet is the bottleneck)" -ForegroundColor DarkGray

optimum-cli export onnx `
    --model $mergedDir `
    --task text-to-image `
    --device cuda `
    --no-post-process `
    $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "ERROR: optimum-cli exited with code $LASTEXITCODE. The export failed." -ForegroundColor Red
    Write-Host "The output directory may contain a partial dump. Delete it before retrying:" -ForegroundColor Red
    Write-Host "  Remove-Item -Recurse -Force '$OutputDirectory'" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $mergedDir
    deactivate
    exit 1
}

# 6. Tidy up the temp pipeline directory.
Remove-Item -Recurse -Force $mergedDir

# 7. Tidy up the venv.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem -Recurse $OutputDirectory" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Next: place the export at <models root>\dreamshaper-cfg\2026-06-30\ so the" -ForegroundColor DarkGray
Write-Host "      USING paths in sql/dreamshaper-cfg/2026-06-30.sql resolve, then run that SQL." -ForegroundColor DarkGray
