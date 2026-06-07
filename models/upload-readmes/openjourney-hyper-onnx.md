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
  - midjourney-style
base_model: prompthero/openjourney
pipeline_tag: text-to-image
---

# Openjourney + Hyper-SD (4-step) — ONNX

ONNX export of [prompthero/openjourney](https://huggingface.co/prompthero/openjourney) with the [ByteDance/Hyper-SD](https://huggingface.co/ByteDance/Hyper-SD) 4-step LoRA fused into the UNet. SD 1.5 architecture, 512×512 native, Euler scheduler, CFG = 1, **4 steps**.

Openjourney is prompthero's Midjourney-v4-style fine-tune. Activator phrase: `mdjrny-v4 style`. Pick this one when you want the painterly / illustrative "MJ look."

Converted artifact. Training credit: prompthero (Openjourney), ByteDance (Hyper-SD).

## What this repo contains

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # Openjourney UNet + Hyper-SD-15 4-step LoRA fused in
vae_decoder/
vae_encoder/
```

## How it was produced

1. Load `prompthero/openjourney` via `diffusers`.
2. Fuse `ByteDance/Hyper-SD/Hyper-SD15-4steps-lora.safetensors`.
3. `optimum-cli export onnx`.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Conversion script: [`scripts/export-openjourney-hyper.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-openjourney-hyper.ps1).

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler |
| Steps | 4 |
| CFG / guidance scale | 1.0 |
| Negative prompt | Skip |
| Resolution | 512×512 native |
| Activator | Include `mdjrny-v4 style` in prompts |

## License

CreativeML OpenRAIL-M (SD 1.5 + Openjourney + Hyper-SD). License files included. By using this model you accept those terms.
