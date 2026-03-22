---
license: apache-2.0
library_name: onnx
tags:
  - vision-language
  - vlm
  - image-captioning
  - vqa
  - moondream
  - onnx
base_model: vikhyatk/moondream2
pipeline_tag: image-to-text
---

# Moondream2 — Compact VLM (ONNX)

ONNX export of [vikhyatk/moondream2](https://huggingface.co/vikhyatk/moondream2) — a 1.86B-param vision-language model from Vikhyat Korrapati. Surprisingly capable for its size on captioning, visual question answering, and basic spatial grounding. Apache-2.0 throughout.

Re-hosted under Heliosoph for distribution stability — `vikhyatk/moondream2` is the authoritative upstream but it doesn't publish its own ONNX exports, and the upstream file layout drifts across revisions. This repo is the canonical ONNX form for the model.

Credit: Vikhyat Korrapati — [Moondream](https://github.com/vikhyat/moondream).

## What this repo contains

Moondream2 is multi-component — Optimum's ONNX exporter splits it into separate files for the vision encoder, text decoder, and token embeddings. **All files must be present in the same directory** for the model to load.

```
vision_encoder.onnx              # SigLIP-based image encoder
decoder_model_merged.onnx        # Phi-1.5-based text decoder (with KV cache merged in)
embed_tokens.onnx                # Token embedding layer (separated for inference efficiency)
config.json                      # HuggingFace model config
generation_config.json           # Decoder generation defaults (max_length, EOS, etc.)
preprocessor_config.json         # Image preprocessing (resize, normalize)
tokenizer.json                   # Tokenizer vocab + merges
tokenizer_config.json
special_tokens_map.json
```

If a component's weights exceed the 2GB protobuf limit, Optimum emits a sibling `.onnx.data` external-data file alongside the `.onnx` — keep them together; the `.onnx` references the `.data` by relative filename.

## Input / output shape

| Stage | Input | Output |
|---|---|---|
| Vision encoder | RGB image, NCHW float32, preprocessor-normalized | Image feature tokens |
| Text decoder | Image features + input token ids + KV cache | Next-token logits + updated KV cache |
| Embed tokens | Token ids | Token embeddings (fed back into decoder) |

Exact tensor shapes and names depend on the Optimum version used at export — verify in Netron before wiring.

## How to use

The runtime pattern is **greedy decoding orchestrated outside the ONNX graph**, similar to the standard encoder-decoder pattern for ONNX-exported LLMs:

```python
import onnxruntime as ort
import numpy as np

vision_enc = ort.InferenceSession("vision_encoder.onnx")
text_dec   = ort.InferenceSession("decoder_model_merged.onnx")
embed      = ort.InferenceSession("embed_tokens.onnx")

# 1. Encode the image
image_features = vision_enc.run(None, {"pixel_values": preprocessed_image})[0]

# 2. Greedy decode loop with KV cache
input_ids = np.array([[BOS_TOKEN]], dtype=np.int64)
generated = []
past_kv = None
for step in range(max_new_tokens):
    embeds = embed.run(None, {"input_ids": input_ids})[0]
    outputs = text_dec.run(None, {
        "inputs_embeds": embeds,
        "image_features": image_features,
        "past_key_values": past_kv,
    })
    next_token = outputs[0][:, -1, :].argmax(-1)
    if next_token.item() == EOS_TOKEN: break
    generated.append(next_token.item())
    input_ids = next_token.reshape(1, 1)
    past_kv = outputs[1:]

text = tokenizer.decode(generated)
```

The `onnxruntime-genai` model builder doesn't currently accept Moondream2's architecture, so raw onnxruntime sessions + a hand-rolled decode loop is the way (same shape as the TrOCR / Florence-2 patterns).

## When to pick Moondream2

- **Compact VLM use cases**: 1.86B params, ~2 GB on disk — runs on CPU at usable latency.
- **Captioning + VQA**: short-form image-to-text. Punches above its size class.
- **Side-by-side VLM comparison**: pairs well with Florence-2 (similar size, different architecture) and Phi-3.5 Vision (~2× larger, different training) for "small VLM" evals.

For larger / higher-quality VLM tasks, reach for [Phi-3.5 Vision](https://huggingface.co/Heliosoph/phi-3.5-vision-instruct-onnx) or upstream Qwen2-VL / Llama-3.2 Vision. For OCR-specific use, [Florence-2](https://huggingface.co/Heliosoph/florence-2-base-ft-fp16-onnx) has dedicated task-tokens that often beat generalist VLMs on document text.

## Provenance + reproducibility caveat

The ONNX export in this repo was done locally via Optimum from the upstream PyTorch weights — one-off, no checked-in reproducible script. If a clean re-export is ever needed (Optimum / transformers / Moondream2 version churn breaks an inference path), the rough recipe is:

```bash
optimum-cli export onnx \
  --model vikhyatk/moondream2 \
  --task image-to-text \
  --trust-remote-code \
  ./moondream2-onnx-staging/
```

Verify the produced file list matches what's shipped here; Optimum's exact output filenames depend on its version. Watch for any new component files (Moondream2 has had architecture tweaks across versions that could add or remove split points).

## License

**Apache-2.0** — same as upstream `vikhyatk/moondream2`. `LICENSE` file included. The ONNX-export step doesn't change licensing — same model, different serialization format.
