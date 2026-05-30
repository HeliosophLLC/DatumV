# Exports Microsoft BiomedCLIP-PubMedBERT_256-vit_base_patch16_224 to ONNX.
#
# BiomedCLIP is a CLIP-style joint image+text embedding model trained on
# 15M biomedical image-caption pairs from PubMed Central. Architecture:
#   - Vision tower: ViT-B/16 at 224x224
#   - Text tower:   PubMedBERT (BERT-base, biomedical WordPiece vocab)
#   - Projection:   each tower -> 512-d shared embedding space
#
# Unlike OpenAI's CLIP, BiomedCLIP is distributed as an `open_clip` checkpoint
# (open_clip_pytorch_model.bin + open_clip_config.json) and is NOT loadable by
# transformers' CLIPModel. optimum-cli has no exporter for it. We therefore
# load the model via open_clip_torch and drive torch.onnx.export directly on
# wrapper modules around model.encode_image / model.encode_text.
#
# The encoders emit *unnormalised* 512-d Float32 vectors -- L2 normalisation
# happens in SQL via `l2_normalize`, matching how clip-vit-base-patch32 is
# wired in models/sql/clip-vit-base-patch32/2026-05-29.sql.
#
# Output layout (mirrors the OpenAI CLIP bundle so a SQL file can be a near
# clone of the existing clip_image_embed / clip_text_embed setup):
#   <OutputDirectory>\onnx\vision_model.onnx
#   <OutputDirectory>\onnx\vision_model_fp16.onnx
#   <OutputDirectory>\onnx\text_model.onnx
#   <OutputDirectory>\onnx\text_model_fp16.onnx
#   <OutputDirectory>\tokenizer.json
#   <OutputDirectory>\tokenizer_config.json
#   <OutputDirectory>\vocab.txt
#   <OutputDirectory>\special_tokens_map.json
#
# fp16 siblings are produced via onnxconverter-common with
# keep_io_types=True, so wire-boundary tensors stay Float32 -- only
# internal weights/activations run in half precision. Catalog entries
# pick precision via include glob, the same pattern as TrOCR.
#
# License: BiomedCLIP is released under the MIT license by Microsoft.
#
# Requirements:
#   - Python 3.10 installed (`py -3.10 --version` should work).
#   - ~3 GB free disk: torch wheel + model weights + ONNX outputs.
#   - Internet connection (hf.co + pypi).
#
# Idempotent: safe to rerun. Reuses .venv if present.
#
# Usage:
#   ./scripts/export-biomedclip.ps1
#       -- exports to $env:DATUMV_MODELS\biomedclip-vit-base-patch16-224
#   ./scripts/export-biomedclip.ps1 -OutputDirectory C:\foo
#       -- exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'biomedclip-vit-base-patch16-224'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
        }
    )
)

$ErrorActionPreference = 'Stop'

# 1. Project-local Python 3.10 venv at .venv (gitignored, shared with the
#    other export scripts).
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

& .\.venv\Scripts\Activate.ps1

# 2. Install conversion tooling. open_clip_torch pulls in the BiomedCLIP
#    config + the HFTextEncoder wrapper around HuggingFace BertModel.
#    transformers is needed to materialise the PubMedBERT WordPiece
#    tokenizer next to the ONNX files.
Write-Host 'Installing torch 2.4 (CPU build; we never run inference here) ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'torch<2.5' --index-url https://download.pytorch.org/whl/cpu

Write-Host 'Installing open_clip_torch + transformers + onnx + onnxconverter-common ...' -ForegroundColor Cyan
pip install --quiet `
    'open_clip_torch>=2.24' `
    'transformers>=4.40,<5' `
    'onnx>=1.16' `
    'onnxconverter-common>=1.14' `
    huggingface_hub

# 3. Run the export. We write the Python to a temp file rather than passing
#    it via `python -c` because the multi-line wrappers + open_clip imports
#    are awkward to escape inside a PowerShell here-string.
$onnxDir = Join-Path $OutputDirectory 'onnx'
New-Item -ItemType Directory -Force -Path $onnxDir | Out-Null

$exportPy = Join-Path ([System.IO.Path]::GetTempPath()) "biomedclip_export_$([guid]::NewGuid().ToString('N')).py"

$exportScript = @'
import sys, pathlib, torch, torch.nn as nn
import open_clip
import onnx
from onnxconverter_common import float16
from transformers import AutoTokenizer

output_root = pathlib.Path(sys.argv[1])
onnx_dir    = output_root / "onnx"
hub_id      = "hf-hub:microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224"
tok_id      = "microsoft/BiomedNLP-PubMedBERT-base-uncased-abstract"

print(f"Loading {hub_id} via open_clip ...", flush=True)
model, _, _ = open_clip.create_model_and_transforms(hub_id)
model.eval()

# Sanity-check dims so a silently mis-loaded checkpoint blows up here
# rather than at SQL register time.
with torch.no_grad():
    img_probe = model.encode_image(torch.zeros(1, 3, 224, 224))
    txt_probe = model.encode_text(torch.zeros(1, 256, dtype=torch.long))
assert img_probe.shape == (1, 512), f"unexpected image_embed shape {img_probe.shape}"
assert txt_probe.shape == (1, 512), f"unexpected text_embed shape {txt_probe.shape}"

class VisionEncoder(nn.Module):
    def __init__(self, m): super().__init__(); self.m = m
    def forward(self, pixel_values):
        return self.m.encode_image(pixel_values, normalize=False)

class TextEncoder(nn.Module):
    def __init__(self, m): super().__init__(); self.m = m
    def forward(self, input_ids):
        return self.m.encode_text(input_ids, normalize=False)

vision_path = onnx_dir / "vision_model.onnx"
text_path   = onnx_dir / "text_model.onnx"

print(f"Exporting vision encoder -> {vision_path} ...", flush=True)
torch.onnx.export(
    VisionEncoder(model),
    torch.randn(1, 3, 224, 224),
    str(vision_path),
    input_names=["pixel_values"],
    output_names=["image_embeds"],
    dynamic_axes={"pixel_values": {0: "batch"}, "image_embeds": {0: "batch"}},
    opset_version=17,
    do_constant_folding=True,
)

# BiomedCLIP text tower is trained at context length 256 (the "_256" in the
# repo name). The position-embedding table has exactly 256 rows; passing a
# longer sequence would index out of bounds inside the model.
print(f"Exporting text encoder -> {text_path} ...", flush=True)
torch.onnx.export(
    TextEncoder(model),
    torch.zeros(1, 256, dtype=torch.long),
    str(text_path),
    input_names=["input_ids"],
    output_names=["text_embeds"],
    dynamic_axes={"input_ids": {0: "batch", 1: "sequence"}, "text_embeds": {0: "batch"}},
    opset_version=17,
    do_constant_folding=True,
)

# fp16 siblings. keep_io_types=True keeps inputs/outputs Float32 so the
# SQL wire boundary (image_to_tensor_chw / tokenizer.encode_bert) does not
# have to know about fp16 -- only internal weights and activations run in
# half precision. This matches the TrOCR fp16 export pattern.
for src, dst in [
    (vision_path, onnx_dir / "vision_model_fp16.onnx"),
    (text_path,   onnx_dir / "text_model_fp16.onnx"),
]:
    print(f"Converting {src.name} -> {dst.name} (fp16) ...", flush=True)
    m = onnx.load(str(src))
    m16 = float16.convert_float_to_float16(m, keep_io_types=True)
    onnx.save(m16, str(dst))

# Tokenizer files. BiomedCLIP itself ships no tokenizer assets -- open_clip
# resolves them at runtime from PubMedBERT-abstract. Materialise them so
# the engine's tokenizer functions can load by path without hitting the hub.
print(f"Saving PubMedBERT tokenizer to {output_root} ...", flush=True)
tok = AutoTokenizer.from_pretrained(tok_id)
tok.save_pretrained(str(output_root))

print("Done.", flush=True)
'@

Set-Content -Path $exportPy -Value $exportScript -Encoding UTF8

try {
    & .\.venv\Scripts\python.exe $exportPy $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Export script failed with exit code $LASTEXITCODE."
    }
} finally {
    Remove-Item -Force $exportPy -ErrorAction SilentlyContinue
}

deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem $OutputDirectory -Recurse" -ForegroundColor DarkGray
