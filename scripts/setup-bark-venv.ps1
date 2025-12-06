# Sets up the Python venv for the Bark Small Python-bridge model.
#
# Creates {DATUM_MODELS}/.venv-bark and pip-installs:
#   - torch  (CUDA wheel by default; CPU fallback via -Cpu)
#   - transformers
#   - scipy
#
# The Bark worker script ships with the engine in bin/<config>/net10.0/python/
# and is auto-resolved by RegisterBarkSmall, so this script doesn't drop a
# worker file. Bark's model weights download from HuggingFace into the user's
# HF cache (~/.cache/huggingface/) on the first inference call.
#
# Idempotent: skips if the venv already exists. Use -Force to recreate.
#
# Usage:
#   .\scripts\setup-bark-venv.ps1
#       -- creates the venv with the cu128 PyTorch wheel
#   .\scripts\setup-bark-venv.ps1 -CudaWheel cu126
#       -- pin to a different CUDA wheel (cu118 / cu121 / cu124 / cu126 / cu128)
#   .\scripts\setup-bark-venv.ps1 -Cpu
#       -- skip CUDA wheel; use CPU-only torch (much slower, no NVIDIA needed)
#   .\scripts\setup-bark-venv.ps1 -Force
#       -- nuke and recreate the venv from scratch
#   .\scripts\setup-bark-venv.ps1 -ModelsDirectory C:\my-models
#       -- override the venv parent directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ModelsDirectory = $(
        if ($env:DATUM_MODELS) {
            $env:DATUM_MODELS
        } else {
            throw 'Set $env:DATUM_MODELS or pass -ModelsDirectory <path>.'
        }
    ),

    # CUDA wheel suffix matching your installed CUDA Toolkit major.minor.
    # cu128 works against CUDA 12.x system installs; pick a smaller suffix
    # if you have an older driver and the cu128 wheel can't load.
    [Parameter()]
    [string]$CudaWheel = 'cu128',

    # Skip the CUDA wheel and install the default (CPU-only) torch.
    # Use this on machines without an NVIDIA GPU.
    [Parameter()]
    [switch]$Cpu,

    # Re-create the venv even if one exists.
    [Parameter()]
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$venvPath = Join-Path $ModelsDirectory '.venv-bark'

# Step 1: ensure venv exists.
if (Test-Path $venvPath) {
    if ($Force) {
        Write-Host "Removing existing venv at $venvPath ..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $venvPath
    } else {
        Write-Host ".venv-bark exists at $venvPath, reusing." -ForegroundColor DarkGray
        Write-Host "(use -Force to recreate from scratch)" -ForegroundColor DarkGray
    }
}

if (-not (Test-Path $venvPath)) {
    Write-Host "Creating Python 3.10 venv at $venvPath ..." -ForegroundColor Cyan
    py -3.10 -m venv $venvPath
    if ($LASTEXITCODE -ne 0) { throw "venv creation failed (exit code $LASTEXITCODE)" }
}

# Step 2: pip install.
$pip = Join-Path $venvPath 'Scripts\pip.exe'
if (-not (Test-Path $pip)) {
    throw "pip not found at $pip after venv creation; something's wrong with the venv."
}

Write-Host 'Upgrading pip ...' -ForegroundColor Cyan
& $pip install --quiet --upgrade pip

if ($Cpu) {
    Write-Host 'Installing torch (CPU-only build) ...' -ForegroundColor Cyan
    & $pip install --quiet torch
} else {
    Write-Host "Installing torch ($CudaWheel CUDA wheel) ..." -ForegroundColor Cyan
    & $pip install --quiet torch --index-url "https://download.pytorch.org/whl/$CudaWheel"
}
if ($LASTEXITCODE -ne 0) { throw 'torch install failed' }

Write-Host 'Installing transformers + scipy ...' -ForegroundColor Cyan
& $pip install --quiet transformers scipy
if ($LASTEXITCODE -ne 0) { throw 'transformers/scipy install failed' }

# Step 3: verify CUDA is reachable (informational; not fatal).
$python = Join-Path $venvPath 'Scripts\python.exe'
$cudaCheck = & $python -c "import torch; print('CUDA:' + str(torch.cuda.is_available()) + ' / version:' + str(torch.version.cuda))"
Write-Host ''
Write-Host "torch reports: $cudaCheck" -ForegroundColor Cyan

Write-Host ''
Write-Host 'Bark venv setup complete.' -ForegroundColor Green
Write-Host ''
Write-Host 'Try it:' -ForegroundColor Cyan
Write-Host "  SELECT models.bark_small('hello there [laughs] from datum');"
Write-Host ''
Write-Host 'First call downloads ~1 GB of Bark weights from HuggingFace into'
Write-Host '~/.cache/huggingface/ — expect a 1-2 minute pause on the first query.'
