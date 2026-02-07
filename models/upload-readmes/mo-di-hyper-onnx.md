---
license: creativeml-openrail-m
library_name: diffusers
tags:
  - stable-diffusion
  - sd-1.5
  - hyper-sd
  - onnx
  - text-to-image
  - 4-step
  - disney-style
base_model: nitrosocke/mo-di-diffusion
pipeline_tag: text-to-image
---

# Mo-Di Diffusion + Hyper-SD (4-step) — ONNX

ONNX export of [nitrosocke/mo-di-diffusion](https://huggingface.co/nitrosocke/mo-di-diffusion) with the [ByteDance/Hyper-SD](https://huggingface.co/ByteDance/Hyper-SD) 4-step LoRA fused into the UNet. SD 1.5 architecture, 512×512 native, Euler scheduler, CFG = 1, **4 steps**.

Mo-Di is nitrosocke's "modern Disney style" fine-tune — produces character art with the late-2010s Disney/Pixar look. Activator phrase commonly used upstream: `modern disney style`. Worth including in prompts for stronger style adherence.

Converted artifact. Training credit: nitrosocke (Mo-Di), ByteDance (Hyper-SD).

## What this repo contains

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # Mo-Di UNet + Hyper-SD-15 4-step LoRA fused in
vae_decoder/
vae_encoder/
```

## How it was produced

1. Load `nitrosocke/mo-di-diffusion` via `diffusers`.
2. Fuse `ByteDance/Hyper-SD/Hyper-SD15-4steps-lora.safetensors`.
3. `optimum-cli export onnx`.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Conversion script: [`scripts/export-mo-di-hyper.ps1`](https://github.com/HeliosophLLC/DatumIngest/blob/main/scripts/export-mo-di-hyper.ps1).

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler |
| Steps | 4 |
| CFG / guidance scale | 1.0 |
| Negative prompt | Skip |
| Resolution | 512×512 native |
| Activator | Include `modern disney style` in prompts for stronger adherence |

## License

CreativeML OpenRAIL-M (SD 1.5 + Mo-Di + Hyper-SD). License files included. By using this model you accept those terms.
