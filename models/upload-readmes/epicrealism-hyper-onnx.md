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
base_model: emilianJR/epiCRealism
pipeline_tag: text-to-image
---

# epiCRealism + Hyper-SD (4-step) — ONNX

ONNX export of [emilianJR/epiCRealism](https://huggingface.co/emilianJR/epiCRealism) with the [ByteDance/Hyper-SD](https://huggingface.co/ByteDance/Hyper-SD) 4-step LoRA fused into the UNet. SD 1.5 architecture, 512×512 native, Euler scheduler, CFG = 1, **4 steps**.

epiCRealism leans heavily toward photorealistic environments and natural lighting. Pick this one over Realistic Vision when the subject is "a place" rather than "a person."

Converted artifact, not a new model. Training credit: emilianJR (epiCRealism), ByteDance (Hyper-SD).

## What this repo contains

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # epiCRealism UNet + Hyper-SD-15 4-step LoRA fused in
vae_decoder/
vae_encoder/
```

## How it was produced

1. Load `emilianJR/epiCRealism` via `diffusers`.
2. Load `ByteDance/Hyper-SD/Hyper-SD15-4steps-lora.safetensors` via `peft`, `fuse_lora()` into UNet.
3. Save fused pipeline; run `optimum-cli export onnx`.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Conversion script: [`scripts/export-epicrealism-hyper.ps1`](https://github.com/HeliosophLLC/Heliosoph/blob/main/scripts/export-epicrealism-hyper.ps1).

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler |
| Steps | 4 |
| CFG / guidance scale | 1.0 |
| Negative prompt | Skip |
| Resolution | 512×512 native |

## License

CreativeML OpenRAIL-M, inherited from SD 1.5 + epiCRealism + Hyper-SD. License files included. By using this model you accept those terms.
