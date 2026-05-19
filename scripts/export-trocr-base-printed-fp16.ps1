# Exports the Microsoft TrOCR (base, printed) model to ONNX and produces
# the fp16 sibling expected by models/sql/trocr-base-printed-fp16.sql.
#
# microsoft/trocr-base-printed is a ViT-base encoder + RoBERTa-style
# decoder for printed-text OCR. MIT licensed. After this script
# completes the Heliosoph.DatumV engine can register `models.trocr_printed_fp16`
# against the resulting fp16 ONNX files.
#
# We export fp32 first via optimum-cli and convert the encoder + merged
# decoder to fp16 with onnxconverter-common, rather than passing
# `--dtype fp16 --device cuda` to optimum-cli directly. Two reasons:
#   - The CUDA path requires a CUDA-enabled torch build in the venv,
#     which the rest of the export scripts don't install.
#   - optimum-cli's fp16 merged-decoder export ships with an If-subgraph
#     wiring bug (see scripts/convert_decoder_model_merged_fp16.py).
#     onnxconverter-common.float16 casts the fp32 graph in place and
#     doesn't hit that bug.
#
# Output layout — both precisions land in one folder so a single
# Heliosoph.DatumV repo (`Heliosoph.DatumV/trocr-base-printed-onnx`) can serve both
# variants. Catalog entries pick their precision via include glob:
#   <OutputDirectory>\onnx\encoder_model.onnx
#   <OutputDirectory>\onnx\encoder_model_fp16.onnx
#   <OutputDirectory>\onnx\decoder_model_merged.onnx
#   <OutputDirectory>\onnx\decoder_model_merged_fp16.onnx
#   <OutputDirectory>\vocab.json
#   <OutputDirectory>\merges.txt
#   <OutputDirectory>\... (rest of tokenizer + preprocessor configs)
#
# Requirements:
#   - Python 3.10 installed (`py -3.10 --version` should work).
#   - ~1.5 GB free disk for the fp32 staging dir + ~700 MB for the fp16 output.
#   - Internet connection.
#
# Idempotent: safe to rerun. Reuses an existing .venv if present.
#
# Usage:
#   ./scripts/export-trocr-base-printed-fp16.ps1
#       — exports to $env:DATUM_MODELS\trocr-base-printed-fp16
#   ./scripts/export-trocr-base-printed-fp16.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'trocr-base-printed-fp16'
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

& .\.venv\Scripts\Activate.ps1

# 2. Install conversion tooling. Same pins as export-vit-gpt-image-captioning.ps1
#    so the shared .venv stays internally consistent. See that script for the
#    rationale on the transformers / optimum / torch pins.
Write-Host 'Cleaning stale optimum / transformers packages ...' -ForegroundColor Cyan
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 + onnxconverter-common (pinned) ...' -ForegroundColor Cyan
pip install --quiet `
    'optimum[onnxruntime]==1.24.0' `
    'transformers==4.45.2' `
    'onnxconverter-common>=1.14' `
    sentencepiece `
    accelerate

Write-Host 'Installing torch 2.4 (CPU build is fine; we never run inference here) ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'torch<2.5' --index-url https://download.pytorch.org/whl/cu124

# 3. Export the fp32 model into a staging dir alongside the final output.
#    `image-to-text-with-past` is the supported task name for
#    vision-encoder-decoder models with KV-cache decoders. The merged
#    decoder file is produced by optimum's post-processing step.
$stagingDir = "$OutputDirectory.fp32-staging"
Write-Host "Exporting fp32 model to staging dir $stagingDir ..." -ForegroundColor Cyan
optimum-cli export onnx `
    --model microsoft/trocr-base-printed `
    --task image-to-text-with-past `
    $stagingDir

if ($LASTEXITCODE -ne 0) {
    deactivate
    throw "optimum-cli failed with exit code $LASTEXITCODE - no ONNX files were written."
}

# 4. Confirm the files we need to convert actually landed. Older Optimum
#    releases skip the merge step for some vision-encoder-decoder configs;
#    catch that here rather than at SQL load time.
$fp32Encoder = Join-Path $stagingDir 'encoder_model.onnx'
$fp32Decoder = Join-Path $stagingDir 'decoder_model_merged.onnx'
foreach ($f in @($fp32Encoder, $fp32Decoder)) {
    if (-not (Test-Path $f)) {
        deactivate
        throw "Expected file $f not produced by optimum-cli. Inspect $stagingDir."
    }
}

# 5. Cast to fp16. keep_io_types=True keeps inputs/outputs fp32 so the
#    Heliosoph.DatumV pipeline (image_to_tensor_chw on the host, decode_seq2seq
#    over the encoder hidden states) doesn't have to know about fp16 at
#    the wire boundary — only the internal weights and activations run in
#    half precision.
$onnxDir = Join-Path $OutputDirectory 'onnx'
New-Item -ItemType Directory -Force -Path $onnxDir | Out-Null

$fp16Encoder = Join-Path $onnxDir 'encoder_model_fp16.onnx'
$fp16Decoder = Join-Path $onnxDir 'decoder_model_merged_fp16.onnx'

Write-Host 'Converting encoder + merged decoder to fp16 ...' -ForegroundColor Cyan
$convertScript = @"
import onnx
from onnxconverter_common import float16

for src, dst in [
    (r'$fp32Encoder', r'$fp16Encoder'),
    (r'$fp32Decoder', r'$fp16Decoder'),
]:
    print(f'  {src} -> {dst}')
    m = onnx.load(src)
    m16 = float16.convert_float_to_float16(m, keep_io_types=True)
    onnx.save(m16, dst)
"@
& .\.venv\Scripts\python.exe -c $convertScript
if ($LASTEXITCODE -ne 0) {
    deactivate
    throw "fp16 conversion failed with exit code $LASTEXITCODE."
}

# 6. Copy the fp32 ONNX files into the onnx/ subdir alongside the fp16
#    siblings. Heliosoph.DatumV bundles both precisions in one repo for
#    distribution symmetry — separate catalog entries pick which they
#    want via include glob. The non-merged decoder_model.onnx is dropped
#    (the merged form supersedes it for runtime use; keeping both would
#    double the repo size for no benefit).
Write-Host "Copying fp32 ONNX files into $onnxDir ..." -ForegroundColor Cyan
Copy-Item -Path $fp32Encoder -Destination (Join-Path $onnxDir 'encoder_model.onnx')           -Force
Copy-Item -Path $fp32Decoder -Destination (Join-Path $onnxDir 'decoder_model_merged.onnx')    -Force

# 7. Copy the tokenizer + preprocessor sidecars next to the onnx/ subdir
#    so the SQL `../vocab.json` / `../merges.txt` references resolve.
Write-Host "Copying tokenizer + config files into $OutputDirectory ..." -ForegroundColor Cyan
Get-ChildItem -Path $stagingDir -File `
    | Where-Object { $_.Extension -ne '.onnx' -and $_.Extension -ne '.onnx_data' } `
    | Copy-Item -Destination $OutputDirectory -Force

# 8. Remove the staging dir now that both precisions + sidecars are in place.
Write-Host 'Removing fp32 staging dir ...' -ForegroundColor DarkGray
Remove-Item -Recurse -Force $stagingDir

deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem $OutputDirectory -Recurse" -ForegroundColor DarkGray
