---
license: mit
library_name: onnx
tags:
  - embeddings
  - vision
  - cross-modal
  - biomedical
  - medical
  - clip
  - onnx
base_model: microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224
pipeline_tag: zero-shot-image-classification
---

# BiomedCLIP ViT-B/16 PubMedBERT-256 (ONNX, fp32 + fp16 bundle)

ONNX exports of [microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224](https://huggingface.co/microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224) — Microsoft's CLIP-style joint image+text embedder tuned for biomedical figures. Trained on **PMC-15M**, a corpus of 15 million biomedical image-caption pairs scraped from PubMed Central papers (radiology, histology, microscopy, charts, gross pathology, illustrations). The biomedical counterpart to OpenAI CLIP.

This repo bundles **both fp32 and fp16 precisions** in one download — distribution symmetry, shared tokenizer files. Pick a precision via the `.onnx` filename in the `onnx/` subdir.

Re-exported from upstream because the upstream repo only ships the `open_clip` checkpoint (`open_clip_pytorch_model.bin` + `open_clip_config.json`) — no ONNX, and `optimum-cli` has no exporter for `open_clip`-style architectures.

Credit: Sheng Zhang, Yanbo Xu, Naoto Usuyama, Hanwen Xu, Jaspreet Bagga, Robert Tinn, Sam Preston, Rajesh Rao, Mu Wei, Naveen Valluri, Cliff Wong, Andrea Tupini, Yu Gu, Mei-Hwa Chen, Hoifung Poon (Microsoft Research). Paper: [*"BiomedCLIP: a multimodal biomedical foundation model pretrained from fifteen million scientific image-text pairs"*](https://arxiv.org/abs/2303.00915), 2023.

## How it was produced

1. Load `microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224` via `open_clip.create_model_and_transforms("hf-hub:microsoft/BiomedCLIP-...")` (the upstream is an `open_clip` checkpoint, not a `transformers.CLIPModel`).
2. Wrap the vision and text branches in thin `nn.Module` adapters that call `model.encode_image(..., normalize=False)` / `model.encode_text(..., normalize=False)` — we want the raw 512-d projections; L2-normalisation happens at the call site.
3. `torch.onnx.export` each wrapper at opset 17 with `do_constant_folding=True`. Vision input shape `[batch, 3, 224, 224]` Float32; text input shape `[batch, ≤256]` Int64 (the `_256` in the upstream repo name is the model's hard position-embedding limit).
4. `onnxconverter_common.float16.convert_float_to_float16(..., keep_io_types=True)` casts each fp32 graph in place to produce the `_fp16.onnx` sibling. `keep_io_types=True` is the load-bearing flag: wire-boundary tensors stay Float32 so the runtime feeds the same input tensors regardless of variant — only internal weights and activations run in half precision.
5. Materialise the PubMedBERT tokenizer assets via `AutoTokenizer.from_pretrained("microsoft/BiomedNLP-PubMedBERT-base-uncased-abstract").save_pretrained(...)`. BiomedCLIP itself ships no tokenizer files; `open_clip` resolves them at runtime from that PubMedBERT repo. Bundling them here lets the engine load by path without hitting the hub.

Toolchain: `torch 2.4.x` (CPU wheel — we never run inference at export time), `open_clip_torch >= 2.24`, `transformers >= 4.40`, `onnx >= 1.16`, `onnxconverter-common >= 1.14`. Full conversion script: [`scripts/export-biomedclip.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-biomedclip.ps1) in the Heliosoph repo.

## What this repo contains

BiomedCLIP is two-tower contrastive, so the export splits into two ONNX files per precision plus the shared tokenizer at the root.

### `onnx/` subdir — precision-specific files

| File | Variant | Size | Role |
|---|---|---|---|
| `vision_model.onnx` | fp32 | ~340 MB | ViT-B/16 vision encoder + 512-d projection head |
| `vision_model_fp16.onnx` | fp16 | ~170 MB | Half-precision ViT-B/16 vision encoder |
| `text_model.onnx` | fp32 | ~430 MB | PubMedBERT text encoder + 512-d projection head |
| `text_model_fp16.onnx` | fp16 | ~220 MB | Half-precision PubMedBERT text encoder |

### Root — shared tokenizer files

| File | Role |
|---|---|
| `vocab.txt` | PubMedBERT WordPiece vocabulary (biomedical, one wordpiece per line) |
| `tokenizer.json` | Fast-tokenizer JSON form (unified tokenizer with pre-tokenizer + model) |
| `tokenizer_config.json` | Tokenizer metadata (lowercase, max length, BERT special tokens) |
| `special_tokens_map.json` | `[CLS]`, `[SEP]`, `[PAD]`, `[UNK]`, `[MASK]` mapping |

The tokenizer files are mirrored from [microsoft/BiomedNLP-PubMedBERT-base-uncased-abstract](https://huggingface.co/microsoft/BiomedNLP-PubMedBERT-base-uncased-abstract) — BiomedCLIP itself ships no tokenizer assets, and `open_clip` resolves them at runtime from that PubMedBERT repo. Bundling them here lets the engine load by path without hitting the hub.

## Input / output (both variants)

| Tower | Input | Output |
|---|---|---|
| Vision | `pixel_values` — NCHW **Float32** (yes, even for the fp16 variant — IO types are kept fp32), preprocessed RGB image at 224×224 | `image_embeds` — `[batch, 512]` Float32 (unnormalised; project to the unit sphere yourself) |
| Text | `input_ids` — `[batch, ≤256]` **Int64** WordPiece token IDs with `[CLS]` / `[SEP]` wrapping | `text_embeds` — `[batch, 512]` Float32 (unnormalised) |

The text tower was exported with a **single ONNX input** (`input_ids` only — no `attention_mask` / `token_type_ids`). The position-embedding table has exactly 256 rows; sequences longer than 256 will index out of bounds inside the model and must be truncated by the caller. No padding is needed at the wire boundary; the runtime sends the actual token count per row and lets the model attend across the whole real sequence.

Image preprocessing inherits OpenAI CLIP's stats unchanged:
- Resize + center crop to 224×224
- RGB normalize with `mean=[0.48145466, 0.4578275, 0.40821073]`, `std=[0.26862954, 0.26130258, 0.27577711]`

## How to use

Independent vision and text sessions; L2-normalise the outputs, then dot product equals cosine similarity:

```python
import onnxruntime as ort
import numpy as np
from PIL import Image
from torchvision import transforms
from transformers import AutoTokenizer

# Pick a precision — same runtime code either way thanks to keep_io_types.
PRECISION_SUFFIX = ""          # "" for fp32, "_fp16" for fp16

tok        = AutoTokenizer.from_pretrained(".")
vision_sess = ort.InferenceSession(f"onnx/vision_model{PRECISION_SUFFIX}.onnx")
text_sess   = ort.InferenceSession(f"onnx/text_model{PRECISION_SUFFIX}.onnx")

# --- Image embedding ---
preprocess = transforms.Compose([
    transforms.Resize(224, interpolation=transforms.InterpolationMode.BICUBIC),
    transforms.CenterCrop(224),
    transforms.ToTensor(),
    transforms.Normalize(
        mean=[0.48145466, 0.4578275, 0.40821073],
        std =[0.26862954, 0.26130258, 0.27577711]),
])
img = preprocess(Image.open("chest_xray.png").convert("RGB")).unsqueeze(0).numpy()
image_embeds = vision_sess.run(None, {"pixel_values": img.astype(np.float32)})[0]
image_embeds /= np.linalg.norm(image_embeds, axis=-1, keepdims=True)

# --- Text embedding ---
labels = [
    "a chest X-ray showing pneumonia",
    "a chest X-ray showing pneumothorax",
    "a chest X-ray of a healthy patient",
]
text_embeds = []
for label in labels:
    ids = tok(label, return_tensors="np")["input_ids"].astype(np.int64)
    # Truncate to the model's 256-token context if needed.
    ids = ids[:, :256]
    emb = text_sess.run(None, {"input_ids": ids})[0]
    text_embeds.append(emb[0])
text_embeds = np.stack(text_embeds)
text_embeds /= np.linalg.norm(text_embeds, axis=-1, keepdims=True)

# --- Zero-shot classification ---
scores = (image_embeds @ text_embeds.T).flatten()  # cosine similarity
print(dict(zip(labels, scores)))
```

## Which precision should I use?

- **fp32** — full precision, identical numerics to the upstream `open_clip` PyTorch reference. Default for retrieval-quality benchmarks and accuracy-sensitive medical-AI evaluation.
- **fp16** — ~half the disk footprint (~390 MB vs ~770 MB total) and half the model-load memory. On GPU / NPU with native fp16: modest speedup (typically 1.5-2× over fp32 on consumer GPUs). On CPU runtimes that upcast fp16 → fp32 internally, runtime speed is identical to fp32 but you save the memory.

The `keep_io_types=True` setting means switching between them is a single file-path change — no code changes needed.

## What it's good for

Zero-shot biomedical image classification (give a list of candidate diagnostic descriptions, rank them by similarity to the image), figure retrieval across PMC-scale corpora, cross-modal search ("show me histology slides described like X"), de-duplication of biomedical figures, and as a frozen feature extractor for downstream task-specific heads on small labeled datasets.

**Not a diagnostic tool.** Zero-shot scores reflect caption-vs-image similarity in the training distribution; clinical decisions need validated, regulated systems. The authors call this out explicitly in the upstream model card.

## Related variants (not in this repo)

Microsoft has not published a small / large variant of BiomedCLIP at time of writing — this is the only public BiomedCLIP cut. For OpenAI's general-domain counterpart see [openai/clip-vit-base-patch32](https://huggingface.co/openai/clip-vit-base-patch32) (the Heliosoph catalog ships an ONNX clone of it as `clip-vit-base-patch32`).

## License

**MIT** — same as upstream [microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224](https://huggingface.co/microsoft/BiomedCLIP-PubMedBERT_256-vit_base_patch16_224). `LICENSE` file included. `open_clip` + `torch.onnx` export + fp16 conversion are numerical transformations only — no relicensing implication. The bundled PubMedBERT tokenizer files are also MIT-licensed by Microsoft Research.
