---
license: mit
library_name: transformers
tags:
  - florence-2
  - vision-language
  - image-captioning
  - ocr
  - object-detection
  - onnx
  - fp16
base_model: microsoft/Florence-2-base-ft
pipeline_tag: image-to-text
---

# Florence-2 base-ft — ONNX (fp16)

ONNX export of [microsoft/Florence-2-base-ft](https://huggingface.co/microsoft/Florence-2-base-ft) at fp16 precision. Florence-2 is Microsoft's unified vision-language model — a single checkpoint that handles captioning, OCR, object detection, region description, and grounded VQA via task-prompted decoding.

Converted artifact. Training credit: Microsoft Research.

## What this repo contains

Florence-2 ships as **four** ONNX files (one per sub-model). All four are required at inference:

```
config.json
generation_config.json
preprocessor_config.json
tokenizer.json
tokenizer_config.json
vocab.json
merges.txt
special_tokens_map.json

vision_encoder_fp16.onnx     # DaViT image encoder
encoder_model_fp16.onnx      # text encoder (T5-style)
decoder_model_fp16.onnx      # autoregressive decoder
embed_tokens_fp16.onnx       # token embedding lookup
```

Total: ~520 MB. Use with `optimum.onnxruntime.ORTModelForVision2Seq` or load the four sessions manually.

## How it was produced

```
optimum-cli export onnx \
    --model microsoft/Florence-2-base-ft \
    --task image-to-text \
    --dtype fp16 \
    --trust-remote-code \
    <output>
```

Toolchain: `optimum 1.24.0`, `transformers 4.45.2`, `torch 2.4.x`. `--trust-remote-code` is required — Florence-2 ships custom modeling code (`modeling_florence2.py`) in the source repo.

## Task prompts (selected)

| Task | Prompt |
|---|---|
| Caption | `<CAPTION>` |
| Detailed caption | `<DETAILED_CAPTION>` |
| More detailed caption | `<MORE_DETAILED_CAPTION>` |
| OCR | `<OCR>` |
| OCR with regions | `<OCR_WITH_REGION>` |
| Object detection | `<OD>` |
| Dense region caption | `<DENSE_REGION_CAPTION>` |
| Region proposal | `<REGION_PROPOSAL>` |
| Caption to phrase grounding | `<CAPTION_TO_PHRASE_GROUNDING>` |
| Referring expression segmentation | `<REFERRING_EXPRESSION_SEGMENTATION>` |

Full task-prompt list: see the [upstream model card](https://huggingface.co/microsoft/Florence-2-base-ft).

## When to pick fp16 vs quantized

This repo (**fp16**): GPU inference, maximum quality. ~520 MB.
[`Heliosoph/florence-2-base-ft-quantized-onnx`](https://huggingface.co/Heliosoph/florence-2-base-ft-quantized-onnx): CPU / NPU / mobile, INT8 dynamic. ~270 MB, modestly degraded on text-heavy OCR.

## License

**MIT** — same as upstream. `LICENSE` file included.
