# Exports the ViT-GPT2 image captioning model from HuggingFace to ONNX.
#
# nlpconnect/vit-gpt2-image-captioning is an encoder-decoder model that
# generates a one-sentence caption for an input image. Apache-2.0 licensed.
# After this script completes the Heliosoph.DatumV engine can register
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
#       — exports to $env:DATUMV_MODELS\vit-gpt2-image-captioning
#   ./scripts/export-vit-gpt-image-captioning.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'vit-gpt2-image-captioning'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
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

# 3. Install conversion tooling, pinned to the same stack as
#    export-batch-onnx.ps1 so the shared .venv stays internally consistent.
#
#    Why the pins matter here specifically:
#      - transformers >= 4.50 enforces torch >= 2.6 due to CVE-2025-32434
#        (a torch.load vulnerability). If the venv already has torch 2.4
#        (from a previous batch-onnx run that pinned torch<2.5), an
#        unpinned `pip install --upgrade transformers` pulls a version
#        that refuses to load PyTorch .bin checkpoints — silent failure
#        mid-conversion. Pinning transformers to 4.45.2 keeps it
#        compatible with the torch in the venv.
#      - optimum 2.x changed APIs in ways the conversion script relies on;
#        1.24.0 is the last 1.x release and the one the batch script uses.
# Uninstall any conflicting versions left over from previous runs. The
# 2.x optimum stack splits its ONNX exporter into a separate optimum-onnx
# package; that package requires optimum~=2.1 and is incompatible with
# optimum 1.24. pip's dependency resolver does NOT remove the orphan when
# downgrading optimum, so optimum-onnx 0.1.0 lingers and corrupts the CLI
# subcommand registration with a misleading AttributeError. Wipe the
# whole ML toolchain first, then reinstall at the pinned versions.
#
# The try/catch swallows pip's stderr complaint when one of these
# packages isn't installed (which is fine — it's just being defensive).
Write-Host 'Cleaning stale optimum / transformers packages ...' -ForegroundColor Cyan
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 (pinned) ...' -ForegroundColor Cyan
pip install --quiet `
    'optimum[onnxruntime]==1.24.0' `
    'transformers==4.45.2' `
    sentencepiece `
    accelerate

# Ensure torch is present and within the version range optimum 1.24
# supports. cu124 wheel matches the batch script's pin; on machines
# without CUDA the resolver falls back to the CPU build automatically.
Write-Host 'Installing torch 2.4 (CUDA 12.4) ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'torch<2.5' --index-url https://download.pytorch.org/whl/cu124

# 4. Convert the model. Downloads ~990 MB of PyTorch weights, splits into
#    encoder + decoder ONNX files, writes everything (plus tokenizer +
#    preprocessor configs) into $OutputDirectory.
Write-Host "Exporting model to $OutputDirectory ..." -ForegroundColor Cyan
optimum-cli export onnx `
    --model nlpconnect/vit-gpt2-image-captioning `
    --task image-to-text `
    $OutputDirectory

# optimum-cli is a native .exe — PowerShell's $ErrorActionPreference='Stop'
# only catches PowerShell-level errors, not native exit codes. Without
# this check the script "succeeds" even when conversion failed, leaving
# no ONNX files on disk and a misleading "Done" message at the end.
if ($LASTEXITCODE -ne 0) {
    deactivate
    throw "optimum-cli failed with exit code $LASTEXITCODE - no ONNX files were written."
}

# 5. Tidy up — leave the parent shell in the same state it started.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem $OutputDirectory" -ForegroundColor DarkGray
