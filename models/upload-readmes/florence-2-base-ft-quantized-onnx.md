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
  - int8
  - quantized
base_model: microsoft/Florence-2-base-ft
pipeline_tag: image-to-text
---

# Florence-2 base-ft — ONNX (INT8 dynamic-quantized)

ONNX export of [microsoft/Florence-2-base-ft](https://huggingface.co/microsoft/Florence-2-base-ft) with post-export INT8 dynamic quantization applied to all four sub-models. Roughly half the disk and inference RAM of the fp16 variant.

Converted artifact. Training credit: Microsoft Research.

## What this repo contains

```
config.json
generation_config.json
preprocessor_config.json
tokenizer.json
tokenizer_config.json
vocab.json
merges.txt
special_tokens_map.json

vision_encoder_quantized.onnx
encoder_model_quantized.onnx
decoder_model_quantized.onnx
embed_tokens_quantized.onnx
```

Total: ~270 MB. All four ONNX files are required at inference.

## How it was produced

1. Export to fp32 ONNX:
   ```
   optimum-cli export onnx \
       --model microsoft/Florence-2-base-ft \
       --task image-to-text \
       --trust-remote-code \
       <fp32-output>
   ```
2. Apply dynamic INT8 quantization to each `.onnx` file using `onnxruntime.quantization.quantize_dynamic` (weight-only, per-channel).

Toolchain: `optimum 1.24.0`, `transformers 4.45.2`, `onnxruntime 1.19.x`.

## When to pick quantized vs fp16

This repo (**INT8**): CPU, NPU (OpenVINO EP), mobile, browser (transformers.js). ~270 MB.
[`Heliosoph/florence-2-base-ft-fp16-onnx`](https://huggingface.co/Heliosoph/florence-2-base-ft-fp16-onnx): GPU, maximum quality. ~520 MB.

**Known degradation:** Dense OCR over small text loses noticeable accuracy at INT8. Captioning and object detection are largely unaffected. Test on your workload before committing.

## Task prompts

Identical to the fp16 variant — see [`Heliosoph/florence-2-base-ft-fp16-onnx`](https://huggingface.co/Heliosoph/florence-2-base-ft-fp16-onnx) for the full list.

## License

**MIT** — same as upstream. `LICENSE` file included.
