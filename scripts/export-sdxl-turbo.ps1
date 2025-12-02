# Exports Stability AI's SDXL-Turbo from PyTorch (HuggingFace) to ONNX.
#
# stabilityai/sdxl-turbo is a 1-4 step text-to-image model. Compared to
# SD-Turbo: dramatically better quality, 1024×1024 output (vs 512×512),
# dual text encoders (CLIP-L + OpenCLIP-G), and a much larger UNet
# (~2.6B params vs SD-Turbo's ~860M). Same diffusers folder layout
# convention.
#
# Why convert instead of downloading a pre-built ONNX? Same reason as
# SD-Turbo: most published SDXL-Turbo ONNX builds are optimised for
# DirectML and use NhwcConv operators that the standard CPU/CUDA
# execution providers don't handle. optimum-cli produces a portable
# build with standard Conv ops that works everywhere ONNX Runtime runs.
#
# Requirements:
#   - Python 3.10 venv at .venv\ (the existing one from
#     export-vit-gpt-image-captioning.ps1 / export-sd-turbo.ps1 works)
#   - ~12 GB free disk for the FP32 ONNX output (~6 GB for FP16)
#   - ~7 GB free disk for the PyTorch download (cached temporarily)
#   - Internet connection
#   - 15-25 minutes of patience: the UNet is ~2.6B params and ONNX
#     export traces every operation
#
# Idempotent: safe to rerun.
#
# Usage:
#   ./scripts/export-sdxl-turbo.ps1
#       — exports to $env:DATUM_MODELS\sdxl-turbo-onnx (FP32)
#   ./scripts/export-sdxl-turbo.ps1 -Fp16
#       — exports to FP16 (~half the disk + VRAM)
#   ./scripts/export-sdxl-turbo.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'sdxl-turbo-onnx'
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputDirectory <path>.'
        }
    ),

    [Parameter()]
    [switch]$Fp16
)

$ErrorActionPreference = 'Stop'

# 1. Reuse the project-local Python 3.10 venv. Create if missing.
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

# 2. Activate.
& .\.venv\Scripts\Activate.ps1

# 3. Install conversion tooling.
Write-Host 'Installing optimum + transformers + diffusers ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'optimum[onnxruntime,diffusers]' transformers diffusers

# 4. Convert.
$dtypeFlag = if ($Fp16) { '--dtype fp16' } else { '' }
$dtypeLabel = if ($Fp16) { 'FP16 (~6 GB)' } else { 'FP32 (~12 GB)' }
Write-Host "Exporting SDXL-Turbo to $OutputDirectory ($dtypeLabel) ..." -ForegroundColor Cyan
Write-Host "(this takes 15-25 minutes; UNet is the bottleneck)" -ForegroundColor DarkGray

if ($Fp16) {
    optimum-cli export onnx `
        --model stabilityai/sdxl-turbo `
        --task text-to-image `
        --dtype fp16 `
        $OutputDirectory
} else {
    optimum-cli export onnx `
        --model stabilityai/sdxl-turbo `
        --task text-to-image `
        $OutputDirectory
}

# 5. Tidy up.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem -Recurse $OutputDirectory" -ForegroundColor DarkGray
