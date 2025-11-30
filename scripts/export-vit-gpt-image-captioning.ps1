# Exports the ViT-GPT2 image captioning model from HuggingFace to ONNX.
#
# nlpconnect/vit-gpt2-image-captioning is an encoder-decoder model that
# generates a one-sentence caption for an input image. Apache-2.0 licensed.
# After this script completes the DatumIngest engine can register
# `models.captioner` against the resulting ONNX files.
#
# Requirements:
#   - Python 3.10 installed (`py -3.10 --version` should work).
#     The ML toolchain (transformers, optimum) doesn't reliably support
#     Python 3.14 yet; 3.10 is the closest LTS-grade Python that the
#     ecosystem trusts.
#   - ~1 GB free disk for the download + ~700 MB for the ONNX output.
#   - Internet connection.
#
# Idempotent: safe to rerun. Reuses an existing .venv if present.
#
# Usage:
#   ./scripts/export-vit-gpt-image-captioning.ps1
#       — exports to $env:DATUM_MODELS\vit-gpt2-image-captioning
#   ./scripts/export-vit-gpt-image-captioning.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'vit-gpt2-image-captioning'
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputDirectory <path>.'
        }
    )
)

$ErrorActionPreference = 'Stop'

# 1. Project-local Python 3.10 venv at .venv (gitignored).
#    Reused on subsequent runs.
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

# 2. Activate the venv for the remainder of this script. Activation only
#    affects this script's process; the parent shell stays untouched.
& .\.venv\Scripts\Activate.ps1

# 3. Install conversion tooling. pip is idempotent — fast on reruns.
Write-Host 'Installing optimum + transformers ...' -ForegroundColor Cyan
pip install --quiet --upgrade optimum[onnxruntime] transformers

# 4. Convert the model. Downloads ~990 MB of PyTorch weights, splits into
#    encoder + decoder ONNX files, writes everything (plus tokenizer +
#    preprocessor configs) into $OutputDirectory.
Write-Host "Exporting model to $OutputDirectory ..." -ForegroundColor Cyan
optimum-cli export onnx `
    --model nlpconnect/vit-gpt2-image-captioning `
    --task image-to-text `
    $OutputDirectory

# 5. Tidy up — leave the parent shell in the same state it started.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem $OutputDirectory" -ForegroundColor DarkGray
