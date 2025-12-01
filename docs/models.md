---
title: Models
---

# Model Zoo Reference

DatumIngest invokes machine-learning models through SQL functions in the
`models.X` namespace — `models.llama31_8b(prompt)`,
`models.yolov8n(image)`, `models.florence2_caption(image)`. This page
documents what's registered out of the box, where each weights file
comes from, and how to set up the model directory on a fresh machine.

For an introspectable view of the same information, query the
`system_models` virtual table:

```sql
SELECT name, category, parameters, license, status
FROM system_models
ORDER BY category, name;
```

## Setup

### Models directory

DatumIngest reads model files from a directory configured in this
order:

1. `--models <path>` flag on `datum-shell`
2. `DATUM_MODELS` environment variable
3. Per-user fallback (`%LOCALAPPDATA%\DatumIngest\models` on Windows,
   `~/.local/share/DatumIngest/models` on Linux/macOS)

The recommended setup is to pick a directory with sufficient free
space and set the env var once:

```powershell
[Environment]::SetEnvironmentVariable('DATUM_MODELS', 'E:\models', 'User')
```

Reopen your terminal so the new variable propagates.

### Directory layout

Single-file models live as a flat `.onnx` or `.gguf` file directly
inside the models directory:

```
$DATUM_MODELS\
  yolov8n.onnx
  mobilenetv2-12.onnx
  Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf
  ...
```

Multi-file models live in a subfolder (the catalog entry's
`RelativePath` points at one anchor file inside the folder; the model
loader derives the rest from the parent directory):

```
$DATUM_MODELS\
  vit-gpt2-image-captioning\
    encoder_model.onnx
    decoder_model.onnx
    tokenizer.json
    ...
  florence-2-base-ft-fp16\
    vision_encoder_fp16.onnx
    embed_tokens_fp16.onnx
    encoder_model_fp16.onnx
    decoder_model_fp16.onnx
    tokenizer.json
    ...
```

## Models reference

### `mobilenetv2` — image classifier

- **What it does**: Top-1 ImageNet classification. Returns
  `Struct{label: String, score: Float32}`.
- **License**: Apache-2.0 (ONNX Model Zoo)
- **Source**: [github.com/onnx/models](https://github.com/onnx/models/tree/main/validated/vision/classification/mobilenet)
- **Files**:
  - `mobilenetv2-12.onnx` (~13 MB) — the ONNX weights
  - `imagenet-classes.json` (optional, ~30 KB) — label vocabulary;
    download
    [imagenet-simple-labels.json](https://raw.githubusercontent.com/anishathalye/imagenet-simple-labels/master/imagenet-simple-labels.json)
    and rename
- **Setup**:
  ```powershell
  Invoke-WebRequest "https://github.com/onnx/models/raw/main/validated/vision/classification/mobilenet/model/mobilenetv2-12.onnx" `
    -OutFile $env:DATUM_MODELS\mobilenetv2-12.onnx
  Invoke-WebRequest "https://raw.githubusercontent.com/anishathalye/imagenet-simple-labels/master/imagenet-simple-labels.json" `
    -OutFile $env:DATUM_MODELS\imagenet-classes.json
  ```

### `yolov8n` — object detector

- **What it does**: Bounding-box detection across 80 COCO classes.
  Returns `Array<Struct{label, score, x, y, w, h}>`.
- **License**: ⚠️ **AGPL-3.0** (Ultralytics) — strong copyleft.
  Personal / research / open-source-with-AGPL-compatible-license use is
  fine; commercial SaaS that exposes detection requires either
  re-licensing the dependent codebase under AGPL or buying Ultralytics'
  separate commercial license. See
  [Ultralytics licensing](https://www.ultralytics.com/license).
- **Source**: [github.com/ultralytics/ultralytics](https://github.com/ultralytics/ultralytics)
- **Files**:
  - `yolov8n.onnx` (~12 MB)
- **Setup**: Export from PyTorch (or download a pre-built ONNX from
  the Ultralytics releases page). Variants `yolov8s`, `yolov8m`,
  `yolov8l`, `yolov8x` drop in by registering with a different
  filename — they trade accuracy for speed/size.

### YOLOX — license-clean object detector ladder

Megvii's YOLOX detector family registered as seven sibling entries
spanning the full speed/accuracy spectrum. Same architecture, same
COCO-80 vocabulary, different parameter counts. The license-clean
default detector for `tasks.detect` once that namespace lands.

- **License**: Apache-2.0 (Megvii)
- **Source**: [github.com/Megvii-BaseDetection/YOLOX](https://github.com/Megvii-BaseDetection/YOLOX)
- **Setup**: Pre-built ONNX files attached to the GitHub releases
  page — no Python conversion needed. Direct download:
  ```powershell
  $base = "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0"
  foreach ($file in @("yolox_nano.onnx","yolox_tiny.onnx","yolox_s.onnx","yolox_m.onnx","yolox_l.onnx","yolox_x.onnx","yolox_darknet.onnx")) {
    Invoke-WebRequest "$base/$file" -OutFile "$env:DATUM_MODELS\$file"
  }
  ```

| Catalog name | File | Params | Input size | Disk |
|---|---|---|---|---|
| `yolox_n` | `yolox_nano.onnx` | 0.91M | 416×416 | ~3 MB |
| `yolox_t` | `yolox_tiny.onnx` | 5.06M | 416×416 | ~20 MB |
| `yolox_s` | `yolox_s.onnx` | 9.0M | 640×640 | ~36 MB |
| `yolox_m` | `yolox_m.onnx` | 25.3M | 640×640 | ~98 MB |
| `yolox_l` | `yolox_l.onnx` | 54.2M | 640×640 | ~200 MB |
| `yolox_x` | `yolox_x.onnx` | 99.1M | 640×640 | ~378 MB |
| `yolox_darknet` | `yolox_darknet.onnx` | 63.7M | 640×640 | ~250 MB |

Note that nano and tiny use 416×416 input (smaller, faster); the
others use 640×640. The `YoloXModel` class auto-detects input size
from the ONNX metadata so a single class handles both. Output format
is identical across all sizes.

### `vit_gpt2_caption` — image captioner

- **What it does**: Generates a single-sentence COCO-style caption for
  an image. Returns `String`.
- **License**: Apache-2.0 (nlpconnect)
- **Source**: [huggingface.co/nlpconnect/vit-gpt2-image-captioning](https://huggingface.co/nlpconnect/vit-gpt2-image-captioning)
- **Folder**: `vit-gpt2-image-captioning/`
- **Files** (all relative to the folder):
  - `encoder_model.onnx` (~330 MB) — ViT-base image encoder
  - `decoder_model.onnx` (~480 MB) — GPT-2 autoregressive decoder
  - `tokenizer.json`, `vocab.json`, `merges.txt`, `config.json`,
    `generation_config.json`, `tokenizer_config.json`,
    `special_tokens_map.json`
- **Setup**: requires Python and the `optimum` library to convert
  PyTorch → ONNX. The repo
  [scripts/export-vit-gpt-image-captioning.ps1](../scripts/export-vit-gpt-image-captioning.ps1)
  handles the full conversion in one command:
  ```powershell
  ./scripts/export-vit-gpt-image-captioning.ps1
  ```
  The script creates a Python 3.10 venv at `.venv/`, installs
  `optimum[onnxruntime] transformers`, and runs `optimum-cli export
  onnx`. ~5 minutes including download.

### `sd_turbo` — text-to-image generator

- **What it does**: Generates 512×512 images from a text prompt in a
  single denoising step. Returns `Image` (PNG bytes).
- **License**: ⚠️ **Stability AI Community License** — free for personal
  use and commercial use under $1M ARR. Above that threshold an
  Enterprise license from Stability AI is required.
- **Source**: [huggingface.co/stabilityai/sd-turbo](https://huggingface.co/stabilityai/sd-turbo)
- **Folder**: `sd-turbo-onnx/` — diffusers-format layout
- **Files** (relative to the folder):
  - `text_encoder/model.onnx` (~1.4 GB) — CLIP ViT-H/14 text encoder
  - `unet/model.onnx` + `unet/model.onnx_data` (~3.5 GB) — UNet weights (split
    via ONNX external-data because total exceeds the 2 GB ONNX limit)
  - `vae_decoder/model.onnx` (~200 MB) — latent → RGB
  - `vae_encoder/model.onnx` (~140 MB) — only used by img2img; not used by
    DatumIngest's text-to-image path
  - `tokenizer/{vocab.json, merges.txt, special_tokens_map.json, tokenizer_config.json}`
  - `scheduler/scheduler_config.json`
  - `model_index.json`
- **Disk footprint**: ~5 GB total (FP32). FP16 builds (~half size) exist
  in some community repos; the optimum-cli conversion produces FP32 by
  default.
- **Setup**: requires conversion from PyTorch — pre-built ONNX repos
  (e.g. `tlwu/sd-turbo-onnxruntime`) are typically optimized for the
  DirectML execution provider and use Microsoft-specific NhwcConv
  operators that the standard CPU/CUDA EPs don't handle. The
  [scripts/export-sd-turbo.ps1](../scripts/export-sd-turbo.ps1) script
  handles the full conversion via `optimum-cli`:
  ```powershell
  ./scripts/export-sd-turbo.ps1
  ```
  Reuses the same `.venv` the ViT-GPT2 export created. ~5–10 minutes
  including download.

### Florence-2 captioners (`florence2_*`)

Microsoft's prompt-driven vision-language model. One model handles
multiple caption styles plus future detection / OCR / segmentation
tasks. Registered as four separate catalog entries that share the
same backbone:

| Catalog name | Task prompt | Output style |
|---|---|---|
| `florence2_caption` | `<CAPTION>` | Short COCO-style caption |
| `florence2_detailed_caption` | `<DETAILED_CAPTION>` | Full sentence with context |
| `florence2_more_detailed_caption` | `<MORE_DETAILED_CAPTION>` | Paragraph-level description |
| `florence2_caption_q8` | `<CAPTION>` (int8 quant) | Short caption, ¼ size |

- **License**: MIT (Microsoft)
- **Source**: [huggingface.co/onnx-community/Florence-2-base-ft](https://huggingface.co/onnx-community/Florence-2-base-ft)
- **Folders**:
  - `florence-2-base-ft-fp16/` — fp16 build (~480 MB ONNX) used by the
    first three entries
  - `florence-2-base-ft-quantized/` — int8 build (~120 MB ONNX) used
    by the `_q8` entry
- **Files per folder** (suffix matches folder: `_fp16` or `_quantized`):
  - `vision_encoder{suffix}.onnx`
  - `embed_tokens{suffix}.onnx`
  - `encoder_model{suffix}.onnx`
  - `decoder_model{suffix}.onnx`
  - Plus shared tokenizer/config files: `tokenizer.json`, `vocab.json`,
    `merges.txt`, `config.json`, `generation_config.json`,
    `preprocessor_config.json`, `special_tokens_map.json`
- **Setup**:
  ```powershell
  # fp16 variant — used by the three caption-style entries
  huggingface-cli download onnx-community/Florence-2-base-ft `
    --include "*_fp16.onnx" "*.json" "*.txt" `
    --local-dir $env:DATUM_MODELS\florence-2-base-ft-fp16

  # int8-quantized variant — used by florence2_caption_q8
  huggingface-cli download onnx-community/Florence-2-base-ft `
    --include "*_quantized.onnx" "*.json" "*.txt" `
    --local-dir $env:DATUM_MODELS\florence-2-base-ft-quantized
  ```

### LLMs (`llama31_8b`, `phi3_mini`, `tinyllama_1b`, `gemma2_2b`, `qwen25_coder_1_5b`, `granite31_1b`, `falcon3_1b`)

Seven LLMs spanning Meta, Microsoft, TinyLlama community, Google,
Alibaba, IBM, TII. All quantized to **Q4_K_M** for clean cross-model
comparison. Each is a single GGUF file loaded via LlamaSharp.

| Catalog name | Display | License | Holder |
|---|---|---|---|
| `llama31_8b` | Llama 3.1 8B Instruct | Llama 3.1 Community | Meta |
| `phi3_mini` | Phi-3-mini-4k Instruct | MIT | Microsoft |
| `tinyllama_1b` | TinyLlama 1.1B Chat v1.0 | Apache-2.0 | TinyLlama community |
| `gemma2_2b` | Gemma 2 2B Instruct | Gemma Terms | Google |
| `qwen25_coder_1_5b` | Qwen 2.5 Coder 1.5B Instruct | Apache-2.0 | Alibaba |
| `granite31_1b` | IBM Granite 3.1 1B A400M | Apache-2.0 | IBM |
| `falcon3_1b` | Falcon3 1B Instruct | Falcon LLM License 2.0 | TII |

**Setup**: each is a single `*.gguf` file dropped into the models
directory. Filenames must match the catalog defaults
(see [BuiltinModels.cs](../src/DatumIngest/Models/BuiltinModels.cs))
or be passed explicitly via the registration helper's `modelFilename`
parameter.

```powershell
# Llama 3.1 8B
huggingface-cli download bartowski/Meta-Llama-3.1-8B-Instruct-GGUF `
  Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Phi-3 mini
huggingface-cli download bartowski/Phi-3-mini-4k-instruct-GGUF `
  Phi-3-mini-4k-instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# TinyLlama 1.1B Chat
huggingface-cli download TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF `
  tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Gemma 2 2B
huggingface-cli download bartowski/gemma-2-2b-it-GGUF `
  gemma-2-2b-it-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Qwen 2.5 Coder 1.5B
huggingface-cli download bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF `
  Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Granite 3.1 1B (MoE)
huggingface-cli download bartowski/granite-3.1-1b-a400m-instruct-GGUF `
  granite-3.1-1b-a400m-instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Falcon3 1B
huggingface-cli download tiiuae/Falcon3-1B-Instruct-GGUF `
  Falcon3-1B-Instruct-q4_k_m.gguf `
  --local-dir $env:DATUM_MODELS
```

Total LLM disk: ~12 GB. Each call holds one model resident; the
residency manager swaps when VRAM is tight.

## Quantization conventions

GGUF LLMs use the K-quant family. Common suffixes:

| Suffix | Bits | Quality | Size (vs FP16) |
|---|---|---|---|
| `Q3_K_M` | 3-bit | Noticeable drop | ~25% |
| `Q4_K_M` | 4-bit | **Standard** | ~30% |
| `Q5_K_M` | 5-bit | Slightly better than Q4 | ~37% |
| `Q5_K_L` | 5-bit + fp16 critical layers | High | ~40% |
| `Q6_K` | 6-bit | Near-FP16 | ~45% |
| `Q8_0` | 8-bit | Indistinguishable | ~57% |

The default zoo uses Q4_K_M throughout for consistent
quality-comparison conditions. If you re-quantize, prefer matching
quants across all entries you plan to compare side-by-side — mixing
quants confounds tone/quality differences with quantization noise.

ONNX models use ONNX-Runtime quantization formats (fp32, fp16, int8).
The Florence-2 entries explicitly cover both fp16 and int8 to support
quality / size A/B testing.

## Querying the catalog

```sql
-- The whole zoo
SELECT * FROM system_models;

-- Just the LLM zoo, smallest first
SELECT name, parameters, file_size_bytes, license
FROM system_models
WHERE category = 'llm'
ORDER BY file_size_bytes;

-- What's missing?
SELECT name, file_names, source_url
FROM system_models
WHERE status = 'missing';

-- License audit
SELECT category, license, COUNT(*) AS n
FROM system_models
GROUP BY category, license;

-- Which models are commercially clean (Apache / MIT / BSD)?
SELECT name, display_name, license
FROM system_models
WHERE license IN ('Apache-2.0', 'MIT', 'BSD-3-Clause');
```

## Adding a new model

1. **Pick a backend.** ONNX (vision, embeddings, captioners,
   detectors) or GGUF + LlamaSharp (LLMs).
2. **Add a model class** to `src/DatumIngest/Models/Onnx/` or
   `src/DatumIngest/Models/Llama/`. Inherit from `OnnxModel` for ONNX
   Runtime models; for multi-session pipelines like ViT-GPT2 or
   Florence-2, override `InferBatchAsync` directly.
3. **Add a register helper** to
   [BuiltinModels.cs](../src/DatumIngest/Models/BuiltinModels.cs).
   Populate the full metadata: `DisplayName`, `Parameters`, `License`,
   `LicenseHolder`, `SourceUrl`, `Category`, `Modalities`, `Files`.
4. **Wire into `AttachStandardModels`** so it ships with the default
   catalog.
5. **Add a smoke test** under
   `tests/DatumIngest.Tests/Models/`. Self-skip when the file isn't
   available so CI machines don't fail.
6. **Update this doc** with the model entry.
