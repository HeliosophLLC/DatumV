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

### `sdxl_turbo` — text-to-image generator (high quality)

- **What it does**: Generates 1024×1024 images from a text prompt in
  a single denoising step. Notably better composition and prompt
  adherence than SD-Turbo, at the cost of more disk + VRAM.
- **License**: ⚠️ **Stability AI Community License** — same as SD-Turbo.
  Free under $1M ARR; Enterprise license required above.
- **Source**: [huggingface.co/stabilityai/sdxl-turbo](https://huggingface.co/stabilityai/sdxl-turbo)
- **Folder**: `sdxl-turbo-onnx/` — diffusers-format layout with the
  SDXL addition of a second text encoder
- **Files** (relative to the folder):
  - `text_encoder/model.onnx` (~250 MB) — CLIP-L
  - `text_encoder_2/model.onnx` + `text_encoder_2/model.onnx_data` — OpenCLIP-G (uses external data, ~1.4 GB total)
  - `unet/model.onnx` + `unet/model.onnx_data` — UNet (~5 GB total — ~2.6B params, far bigger than SD's UNet)
  - `vae_decoder/model.onnx` (~200 MB)
  - `vae_encoder/model.onnx` (~140 MB) — only used by img2img
  - `tokenizer/{vocab.json, merges.txt, ...}` — CLIP BPE for encoder 1
  - `tokenizer_2/{vocab.json, merges.txt, ...}` — CLIP BPE for encoder 2 (same vocab as encoder 1's tokenizer in practice; diffusers separates them by convention)
  - `scheduler/scheduler_config.json`
  - `model_index.json`
- **Disk footprint**: ~12 GB FP32, ~6 GB FP16 (use `-Fp16` flag on
  the export script for the smaller build)
- **VRAM**: ~6-8 GB during inference; tight on 12 GB cards alongside
  Llama 8B
- **Setup**: requires conversion from PyTorch — pre-built ONNX repos
  are usually DirectML-optimized and don't work on standard EPs.
  [scripts/export-sdxl-turbo.ps1](../scripts/export-sdxl-turbo.ps1)
  handles the conversion via `optimum-cli`. Reuses the same `.venv`
  the other export scripts created. ~15-25 minutes including download.
- **vs `sd_turbo`**: dramatically better quality, especially for
  complex scenes / multi-subject compositions / fine detail. Slower
  per-image (~3-5s vs SD's ~1-2s). Use SDXL-Turbo for hero outputs;
  SD-Turbo for fast iteration.

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

### `paligemma2_224`, `paligemma2_448` — Google PaliGemma 2 captioners

Google's vision-language model: SigLIP image encoder + Gemma 2B decoder
+ learned linear projector. The "mix" variants are pre-finetuned across
captioning, VQA, and OCR — a single model handles diverse prompts via
the prefix passed to the decoder. We register two resolution variants:

| Catalog name | Input | Image tokens | Use for |
|---|---|---|---|
| `paligemma2_224` | 224×224 | 256 | Cheap iteration; broad scenes |
| `paligemma2_448` | 448×448 | 1024 | Fine-detail / OCR / scene art |

- **License**: Gemma Terms (Google) — broadly permissive, allows
  commercial use; redistribution must pass the terms along.
- **Source**: [huggingface.co/google/paligemma2-3b-mix-448](https://huggingface.co/google/paligemma2-3b-mix-448)
  (or `-224` for the smaller variant)
- **Folders**: one per variant (`paligemma2-3b-mix-224-onnx/`, etc.)
- **Files per folder**:
  - `vision_encoder.onnx` — SigLIP encoder + linear projector
  - `embed_tokens.onnx` — Gemma token-embedding lookup
  - `decoder_model.onnx` — Gemma 2B autoregressive decoder
  - `tokenizer.json`, `vocab.json`, `merges.txt`, `config.json`
- **Default prompt**: `"caption en"` — produces verbose factual
  English captions like *"Two adventurers in armor stand at the cavern
  entrance. The taller one holds a torch. Cobwebs hang from the ceiling."*
- **Output style**: PaliGemma's captions are noticeably more verbose
  and grounded than Florence-2's, with multiple short sentences
  rather than one. Good raw material to feed an LLM rewriter.
- **Other prompts** (set via the registration's `defaultPrompt`):
  - `"caption en"` / `"caption es"` / `"caption fr"` / etc.
  - `"answer en What is the dragon doing?"` — VQA mode
  - `"ocr"` — OCR mode
  - `"detect <object>"` — object detection
- **Setup**: produced by the batch ONNX conversion script:
  ```powershell
  ./scripts/export-batch-onnx.ps1 -Models paligemma2-3b-mix-448
  ```
  Conversion runs `optimum-cli export onnx --model
  google/paligemma2-3b-mix-448`. ~10-15 minutes including download.
- **Demo (vs Florence-2 for the same scene)**:
  ```sql
  SELECT
    art_id,
    models.florence2_more_detailed_caption(art) AS clinical,    -- structured single sentence
    models.paligemma2_448(art)                  AS verbose      -- multi-sentence factual
  FROM scene_art LIMIT 3;
  ```

### LLMs (`llama31_8b`, `phi3_mini`, `tinyllama_1b`, `gemma2_2b`, `qwen25_coder_*`, `granite31_1b`, `falcon3_1b`)

Nine LLMs spanning Meta, Microsoft, TinyLlama community, Google,
Alibaba (three Qwen-Coder sizes), IBM, TII. All quantized to **Q4_K_M**
for clean cross-model comparison (Qwen-Coder 7B is Q5_K_M for the small
quality bump). Each is a single GGUF file loaded via LlamaSharp.

| Catalog name | Display | License | Holder |
|---|---|---|---|
| `llama31_8b` | Llama 3.1 8B Instruct | Llama 3.1 Community | Meta |
| `phi3_mini` | Phi-3-mini-4k Instruct | MIT | Microsoft |
| `tinyllama_1b` | TinyLlama 1.1B Chat v1.0 | Apache-2.0 | TinyLlama community |
| `gemma2_2b` | Gemma 2 2B Instruct | Gemma Terms | Google |
| `qwen25_coder_1_5b` | Qwen 2.5 Coder 1.5B Instruct | Apache-2.0 | Alibaba |
| `qwen25_coder_3b` | Qwen 2.5 Coder 3B Instruct | Apache-2.0 | Alibaba |
| `qwen25_coder_7b` | Qwen 2.5 Coder 7B Instruct | Apache-2.0 | Alibaba |
| `granite31_1b` | IBM Granite 3.1 1B A400M | Apache-2.0 | IBM |
| `falcon3_1b` | Falcon3 1B Instruct | Falcon LLM License 2.0 | TII |

The Qwen2.5-Coder ladder is registered with size-appropriate defaults:
the 1.5B uses a 4K context (fast iteration), while the 3B and 7B use a
16K context with a higher max-tokens budget so single-call generation of
multi-paragraph code or HTML pages doesn't truncate. The 7B drops to
`temperature=0.5` (vs 0.7 default) for more deterministic code output.
Per-call overrides — `models.qwen25_coder_7b(prompt, 0.7, 4096)` — let
you tweak both temperature and max_tokens at the call site.

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

# Qwen 2.5 Coder 3B
huggingface-cli download bartowski/Qwen2.5-Coder-3B-Instruct-GGUF `
  Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf `
  --local-dir $env:DATUM_MODELS

# Qwen 2.5 Coder 7B (note Q5_K_M, not Q4_K_M)
huggingface-cli download bartowski/Qwen2.5-Coder-7B-Instruct-GGUF `
  Qwen2.5-Coder-7B-Instruct-Q5_K_M.gguf `
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

Total LLM disk: ~16 GB with all three Qwen-Coder sizes. Each call holds
one model resident; the residency manager swaps when VRAM is tight.

### Whisper STT zoo (`whisper_tiny`, `whisper_base`, `whisper_small`, `whisper_medium`)

OpenAI Whisper as native ONNX. Four size variants spanning fast/cheap
to slow/accurate. Single SQL surface: `models.whisper_X(audio_bytes)`
returns the transcription as `String`.

| Catalog name | Params | Best for |
|---|---|---|
| `whisper_tiny` | 39M | Round-trip verification, smoke tests |
| `whisper_base` | 74M | Balanced default |
| `whisper_small` | 244M | Better accuracy on accented / noisy speech |
| `whisper_medium` | 769M | Strong STT, slower |

- **License**: MIT (OpenAI)
- **Source**: [huggingface.co/openai/whisper-base](https://huggingface.co/openai/whisper-base)
  (replace `base` with the size you want)
- **Folders**: one per variant (`whisper-tiny-onnx/`, etc.)
- **Files per folder**:
  - `encoder_model.onnx` — audio features → encoder hidden states
  - `decoder_model.onnx` — autoregressive caption decoder (no KV cache;
    `decoder_with_past_model.onnx` shipped alongside but unused)
  - `vocab.json`, `merges.txt`, `tokenizer.json` — multilingual BPE
  - `preprocessor_config.json` — mel spectrogram params
  - `generation_config.json` — special token IDs
  - `special_tokens_map.json`
- **Input**: WAV bytes (any sample rate / bit depth — the C# WAV decoder
  handles 8/16/24-bit PCM and IEEE float32, mono/stereo/multi-channel,
  resampling to 16kHz via linear interpolation).
- **Output**: English transcript as `String`. Multilingual models support
  other languages but the registration uses the English language token
  prefix (`<|en|>`); to transcribe another language, register with a
  different `LanguageToken` (one-line constant in `WhisperOnnxModel`).
- **Setup**: produced by the batch ONNX conversion script:
  ```powershell
  ./scripts/export-batch-onnx.ps1 -Models whisper-base
  ```
  Convert other sizes by name (`whisper-tiny`, `whisper-small`,
  `whisper-medium`). The script reuses `.venv/` and runs
  `optimum-cli export onnx --model openai/whisper-X` per variant.

### Python-bridge models (`bark_small`, `bark`, `kokoro_82m`)

Some models are difficult or impractical to convert from PyTorch to
ONNX — multi-stage pipelines with autoregressive Python control flow
(Bark), or research-grade libraries that don't ship export tooling
(XTTS-v2, StyleTTS2). DatumIngest runs these through a long-lived
Python subprocess: the C# side hands inputs over via NDJSON on
stdio, the Python worker uses the upstream library directly, and the
results come back as bytes.

The bridge has its own status indicator in `system_models`:
**`bridge`** — backend is `python`, the venv exists, and the model is
*probably* runnable. Catalog can't fully verify pip packages without
spawning the worker, so a clean `status=bridge` doesn't guarantee
runnability — but a missing venv reliably reports `status=missing`.

#### `bark_small` — TTS with embedded sound effects

- **What it does**: Generates 24kHz mono speech with optional inline
  non-speech tokens. Write `[laughs]`, `[sighs]`, `[music]` etc. in
  the prompt and Bark renders them inline. Output is WAV bytes
  (carried as `Image` until `DataKind.Audio` lands).
- **License**: MIT (Suno)
- **Source**: [huggingface.co/suno/bark-small](https://huggingface.co/suno/bark-small)
- **Backend**: Python bridge — wraps HuggingFace `transformers`'
  `BarkModel`.
- **Files (catalog tracks)**: `.venv-bark/pyvenv.cfg` — the venv
  marker. Bark's actual weights live in `~/.cache/huggingface/`, not
  in `$DATUM_MODELS`.
- **Setup**:
  ```powershell
  ./scripts/setup-bark-venv.ps1
  ```
  Creates `$DATUM_MODELS/.venv-bark`, pip-installs `transformers`,
  `torch` (CUDA wheel), and `scipy`. The Bark weights download from
  HuggingFace on the first inference call (~1 GB, one-time).
  - Use `-Cpu` to install CPU-only torch (much slower; no NVIDIA
    needed).
  - Use `-CudaWheel cu126` (or `cu124` / `cu121`) to pin a different
    PyTorch CUDA wheel — defaults to `cu128`, which works against
    CUDA Toolkit 12.x system installs.
  - Use `-Force` to nuke and recreate.
- **Per-call overrides**:
  - `[0] voice_preset` (string) — e.g. `'v2/en_speaker_9'`. Worker
    pins `v2/en_speaker_6` by default (neutral male, well-tested).
- **Determinism**: Bark samples internally — same prompt produces
  different audio each call.
- **Tips for good output**:
  - **Use full sentences.** Bark expects multi-second context; bare
    phrases ("Cookie Dadda") produce noisy ~1s clips with weird
    prosody. "Hello there, this is Cookie Dadda speaking." sounds
    far better.
  - **Always specify a voice preset** for repeatable output. Without
    one, Bark picks a random speaker each call — quality varies wildly.
  - Inline cues like `[laughs]`, `[clears throat]`, `[sighs]` work.
  - Bark sometimes adds breath, room tone, or even bird sounds
    spontaneously — that's by design from upstream.
- **Demo**:
  ```sql
  SELECT models.bark_small(
    'Hello there from Datum Ingest. [laughs] This is rather fun, actually.',
    'v2/en_speaker_9'
  );
  ```

#### `bark` — full Bark TTS (higher quality)

Same architecture, voices, and worker as `bark_small` — bigger weights
(~700M params vs ~100M) for noticeably more natural prosody at
~3-4× the inference cost.

- **License / Source**: same as `bark_small` —
  [huggingface.co/suno/bark](https://huggingface.co/suno/bark)
- **Backend**: Python bridge — same `.venv-bark` and worker script
  (`bark_worker.py`) as `bark_small`, only the HuggingFace model ID
  differs (`suno/bark` vs `suno/bark-small`).
- **First-call download**: ~3.5 GB into `~/.cache/huggingface/`.
- **VRAM**: ~3-4 GB during inference (3-4× `bark_small`'s footprint).
- **Latency**: ~15-30s per clip on a consumer GPU vs `bark_small`'s
  ~5-10s. Use `bark` for hero outputs, `bark_small` for fast iteration.
- **Setup**: nothing extra beyond `setup-bark-venv.ps1` — both
  variants share the venv. The full model auto-downloads on first
  inference call.
- **Per-call overrides**: same as `bark_small` —
  `models.bark(text, 'v2/en_speaker_9')`.
- **Demo**:
  ```sql
  -- Compare quality side-by-side: same prompt, both variants.
  SELECT
    models.bark_small(prompt, 'v2/en_speaker_0') AS small,
    models.bark      (prompt, 'v2/en_speaker_0') AS full
  FROM (SELECT 'You enter the cavern. [pause] Distant water drips.' AS prompt);
  ```

#### `kokoro_82m` — fast multi-voice TTS

- **What it does**: 82M-parameter ONNX TTS with 11+ built-in voices.
  Fast enough to keep up with token-streaming LLM output. Apache-2.0,
  cleaner license than Bark for commercial work.
- **License**: Apache-2.0 (hexgrad)
- **Source**: [huggingface.co/hexgrad/Kokoro-82M-ONNX](https://huggingface.co/hexgrad/Kokoro-82M-ONNX)
- **Backend**: Python bridge — wraps the `kokoro-onnx` package, which
  bundles the misaki phonemizer + ONNX inference. The model itself is
  ONNX (we go through Python only for the phonemizer).
- **Files (catalog tracks)**: `kokoro-v1.0.onnx` — the ONNX model file
  in `$DATUM_MODELS`. Voices and venv tracked separately:
  - `voices-v1.0.bin` (~26 MB, bundled all voices), OR
  - `kokoro-voices/<voice>.bin` (per-voice files; the worker bundles
    them into a temp `.npz` at startup)
  - `.venv-kokoro/` for the Python deps
- **Per-call overrides**:
  - `[0] voice` (string) — e.g. `'af_bella'`, `'am_michael'`, `'bm_george'`
  - `[1] speed` (float) — `0.5` ... `2.0`
  - Example: `models.kokoro_82m('hello', 'bm_george', 1.2)`
- **Setup** — venv only:
  ```powershell
  ./scripts/setup-kokoro-venv.ps1
  ```
  Creates `$DATUM_MODELS/.venv-kokoro` and installs `kokoro-onnx`
  (which pulls in `onnxruntime`, the misaki phonemizer, and `scipy`
  as transitive deps). You provide the model + voices files yourself
  (typical for users who already downloaded the per-voice `.bin`
  files from the original hexgrad repo).
- **Setup — fully automated** (venv + model + bundled voices):
  ```powershell
  ./scripts/setup-kokoro-venv.ps1 -DownloadModel -DownloadVoices
  ```
  Downloads `kokoro-v1.0.onnx` (~326 MB) and `voices-v1.0.bin`
  (~26 MB) from the kokoro-onnx GitHub release into `$DATUM_MODELS`.
- **Per-voice .bin layout**: if you have separate per-voice files
  (e.g. `af_bella.bin`, `bm_george.bin`, ...), drop them into
  `$DATUM_MODELS/kokoro-voices/`. The default registration points at
  this path; the worker bundles the per-voice arrays into a temp
  `.npz` at startup and passes that to `kokoro-onnx`.
- **Determinism**: deterministic for a given (text, voice, speed)
  tuple. Planner CSE folds duplicate call sites.
- **Demo**:
  ```sql
  SELECT models.kokoro_82m('hello there from datum ingest', 'af_bella');
  ```

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

-- Which Python-bridge models are set up but unverified?
-- (status = 'bridge' means files present, but the catalog can't see
-- whether the venv's pip packages are intact — first invocation will
-- fail loudly if they aren't.)
SELECT name, file_names
FROM system_models
WHERE status = 'bridge';

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

1. **Pick a backend.** Three options:
   - **ONNX** — vision, embeddings, captioners, detectors, image gen
     pipelines. Inherits from `OnnxModel`. The fast, native path.
   - **GGUF + LlamaSharp** — LLMs. Use `LlamaModel`.
   - **Python bridge** — for libraries that don't ship ONNX export
     tooling (research-grade TTS, multi-stage pipelines with dynamic
     control flow, anything in the HuggingFace transformers ecosystem
     that fights `optimum-cli`). Inherits from `PythonBackedModel`,
     ships a worker `.py` in `src/DatumIngest/Models/Python/scripts/`,
     gets a `setup-X-venv.ps1` script under `scripts/`, and reports
     `status=bridge` in `system_models`.
2. **Add a model class** to `src/DatumIngest/Models/Onnx/` or
   `src/DatumIngest/Models/Llama/`. Inherit from `OnnxModel` for ONNX
   Runtime models; for multi-session pipelines like ViT-GPT2,
   Florence-2, or Whisper, override `InferBatchAsync` directly.
   For Python-bridge models, the model class is just a
   `PythonBackedModel` instantiation in the registration helper —
   the worker script does the per-model logic.
3. **Add a register helper** to
   [BuiltinModels.cs](../src/DatumIngest/Models/BuiltinModels.cs).
   Populate the full metadata: `DisplayName`, `Parameters`, `License`,
   `LicenseHolder`, `SourceUrl`, `Category`, `Modalities`, `Files`.
4. **Wire into `AttachStandardModels`** so it ships with the default
   catalog.
5. **Add a smoke test** under
   `tests/DatumIngest.Tests/Models/`. Self-skip when the file isn't
   available so CI machines don't fail.
6. **Add a setup script** if Python-backed: `scripts/setup-X-venv.ps1`
   following the Bark / Kokoro template.
7. **Update this doc** with the model entry.
