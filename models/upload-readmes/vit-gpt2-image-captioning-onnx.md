---
license: apache-2.0
library_name: transformers
tags:
  - image-captioning
  - vit
  - gpt2
  - onnx
base_model: nlpconnect/vit-gpt2-image-captioning
pipeline_tag: image-to-text
---

# ViT-GPT2 Image Captioning — ONNX

ONNX export of [nlpconnect/vit-gpt2-image-captioning](https://huggingface.co/nlpconnect/vit-gpt2-image-captioning) — a classic ViT encoder + GPT-2 decoder image captioner. ~240M parameters, trained on COCO captions.

Lightweight baseline captioner. Florence-2 is the better default for new projects (smaller, more capable, multi-task), but this one is useful when you need a vanilla "describe this image in one sentence" with minimal dependencies.

Converted artifact. Training credit: nlpconnect.

## What this repo contains

```
config.json
generation_config.json
tokenizer.json
tokenizer_config.json
vocab.json
merges.txt
special_tokens_map.json

encoder_model.onnx          # ViT image encoder
decoder_model.onnx          # GPT-2 autoregressive decoder
```

Total: ~1.1 GB at fp32. Load with `optimum.onnxruntime.ORTModelForVision2Seq`.

## How it was produced

```
optimum-cli export onnx \
    --model nlpconnect/vit-gpt2-image-captioning \
    --task image-to-text \
    <output>
```

Conversion script: [`scripts/export-vit-gpt-image-captioning.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-vit-gpt-image-captioning.ps1) in the Heliosoph repo.

Toolchain: `optimum 1.24.0`, `transformers 4.45.2`, `torch 2.4.x`.

## Inference notes

| Setting | Value |
|---|---|
| Input resolution | 224×224 (resized + center-cropped by `preprocessor_config.json`) |
| Output | English caption, ~16-token median length |
| Max tokens | 16 (default in `generation_config.json`) |
| Domain | COCO-style natural scenes |

## License

**Apache-2.0** — same as upstream. `LICENSE` file included.
