# Sets up the Python venv for the Kokoro-82M Python-bridge TTS model.
#
# Creates {DATUMV_MODELS}/.venv-kokoro and pip-installs:
#   - kokoro-onnx  (handles phonemizer + ONNX inference)
#   - numpy
#
# Optionally downloads the model files from the kokoro-onnx GitHub release
# (-DownloadModel for the .onnx, -DownloadVoices for the bundled voices.bin).
# By default it sets up the venv only; you provide the model files yourself
# (e.g. you already have per-voice .bin files in {DATUMV_MODELS}/kokoro-voices/).
#
# The Kokoro worker script ships with the engine in bin/<config>/net10.0/python/
# and is auto-resolved by RegisterKokoro82M, so this script doesn't drop a
# worker file.
#
# Idempotent: skips operations whose outputs already exist. Use -Force to redo.
#
# Usage:
#   .\scripts\setup-kokoro-venv.ps1
#       -- creates the venv only (skip model+voices download)
#   .\scripts\setup-kokoro-venv.ps1 -DownloadModel -DownloadVoices
#       -- venv + model ONNX + bundled voices file (~352 MB total)
#   .\scripts\setup-kokoro-venv.ps1 -DownloadModel
#       -- venv + model ONNX only (you supply voices via per-voice .bin dir)
#   .\scripts\setup-kokoro-venv.ps1 -Force
#       -- nuke and recreate the venv from scratch
#   .\scripts\setup-kokoro-venv.ps1 -ModelsDirectory C:\my-models
#       -- override the venv parent directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ModelsDirectory = $(
        if ($env:DATUMV_MODELS) {
            $env:DATUMV_MODELS
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -ModelsDirectory <path>.'
        }
    ),

    # Download kokoro-v1.0.onnx (~326 MB unquantized) into the model directory.
    [Parameter()]
    [switch]$DownloadModel,

    # Download voices-v1.0.bin (~26 MB, bundle of all voices) into the model
    # directory. Skip this if you have per-voice .bin files in a directory
    # like kokoro-voices/ instead.
    [Parameter()]
    [switch]$DownloadVoices,

    # Re-create the venv even if one exists.
    [Parameter()]
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$venvPath = Join-Path $ModelsDirectory '.venv-kokoro'
$onnxPath = Join-Path $ModelsDirectory 'kokoro-v1.0.onnx'
$voicesPath = Join-Path $ModelsDirectory 'voices-v1.0.bin'

# Step 1: ensure venv exists.
if (Test-Path $venvPath) {
    if ($Force) {
        Write-Host "Removing existing venv at $venvPath ..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $venvPath
    } else {
        Write-Host ".venv-kokoro exists at $venvPath, reusing." -ForegroundColor DarkGray
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

Write-Host 'Installing kokoro-onnx + numpy ...' -ForegroundColor Cyan
# kokoro-onnx pulls in onnxruntime + misaki phonemizer + scipy as transitive deps.
& $pip install --quiet kokoro-onnx numpy
if ($LASTEXITCODE -ne 0) { throw 'kokoro-onnx install failed' }

# Step 3: optional model file download.
if ($DownloadModel) {
    if ((Test-Path $onnxPath) -and -not $Force) {
        Write-Host "kokoro-v1.0.onnx exists at $onnxPath, skipping." -ForegroundColor DarkGray
    } else {
        Write-Host "Downloading kokoro-v1.0.onnx (~326 MB) to $onnxPath ..." -ForegroundColor Cyan
        $url = 'https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx'
        Invoke-WebRequest -Uri $url -OutFile $onnxPath
    }
}

# Step 4: optional voices file download.
if ($DownloadVoices) {
    if ((Test-Path $voicesPath) -and -not $Force) {
        Write-Host "voices-v1.0.bin exists at $voicesPath, skipping." -ForegroundColor DarkGray
    } else {
        Write-Host "Downloading voices-v1.0.bin (~26 MB) to $voicesPath ..." -ForegroundColor Cyan
        $url = 'https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin'
        Invoke-WebRequest -Uri $url -OutFile $voicesPath
    }
}

Write-Host ''
Write-Host 'Kokoro venv setup complete.' -ForegroundColor Green
Write-Host ''

# Status summary so the user knows what's still needed.
$onnxOk = Test-Path $onnxPath
$voicesOk = Test-Path $voicesPath
$voicesDirOk = Test-Path (Join-Path $ModelsDirectory 'kokoro-voices')

Write-Host 'Required model files:' -ForegroundColor Cyan
Write-Host ('  kokoro-v1.0.onnx          ' + $(if ($onnxOk) { 'present' } else { 'MISSING -- rerun with -DownloadModel' }))
Write-Host ('  voices-v1.0.bin           ' + $(if ($voicesOk) { 'present' } else { 'absent' }))
Write-Host ('  kokoro-voices/  (per-voice .bin) ' + $(if ($voicesDirOk) { 'present' } else { 'absent' }))
if (-not ($voicesOk -or $voicesDirOk)) {
    Write-Host '  >>> No voices found. Either rerun with -DownloadVoices, or' -ForegroundColor Yellow
    Write-Host '      drop per-voice .bin files into kokoro-voices/.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Try it:' -ForegroundColor Cyan
Write-Host "  SELECT models.kokoro_82m('hello there from datum ingest', 'af_bella');"
