# Exports Stability AI's SD-Turbo from PyTorch (HuggingFace) to ONNX.
#
# stabilityai/sd-turbo is a 1-step text-to-image model. The PyTorch
# weights (~2.5 GB safetensors) get converted into the diffusers-format
# ONNX layout (text_encoder/, unet/, vae_decoder/, vae_encoder/,
# tokenizer/, scheduler/) that DatumIngest's StableDiffusionTurboModel
# expects.
#
# Why convert instead of downloading a pre-built ONNX? Most published
# SD-Turbo ONNX builds (e.g. tlwu/sd-turbo-onnxruntime) are optimised
# for the DirectML execution provider — they use Microsoft-specific
# NhwcConv ops that CPU/CUDA EPs don't handle. optimum-cli produces
# a portable build with standard Conv ops that works everywhere ONNX
# Runtime runs.
#
# Requirements:
#   - Python 3.10 venv at .venv\ (the export-vit-gpt-image-captioning.ps1
#     script already creates this; reuse it)
#   - ~3 GB free disk for the download + ~2 GB for the ONNX output
#   - Internet connection
#   - Some patience: the conversion takes 5-10 minutes (UNet is ~860M
#     params and the export traces every operation)
#
# Idempotent: safe to rerun. Reuses an existing .venv.
#
# Usage:
#   ./scripts/export-sd-turbo.ps1
#       — exports to $env:DATUM_MODELS\sd-turbo-onnx
#   ./scripts/export-sd-turbo.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'sd-turbo-onnx'
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputDirectory <path>.'
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

# 3. Install conversion tooling. The diffusers-specific export task
#    requires both the diffusers package and optimum's diffusers extra.
Write-Host 'Installing optimum + transformers + diffusers ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'optimum[onnxruntime,diffusers]' transformers diffusers

# 4. Convert. SD-Turbo is text-to-image, so use that task. Output is
#    the diffusers-standard folder layout — exactly what
#    StableDiffusionTurboModel reads.
Write-Host "Exporting SD-Turbo to $OutputDirectory ..." -ForegroundColor Cyan
Write-Host "(this takes 5-10 minutes; UNet is the bottleneck)" -ForegroundColor DarkGray
optimum-cli export onnx `
    --model stabilityai/sd-turbo `
    --task text-to-image `
    $OutputDirectory

# 5. Tidy up.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem -Recurse $OutputDirectory" -ForegroundColor DarkGray
