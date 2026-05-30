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
  - photorealistic
base_model: SG161222/Realistic_Vision_V6.0_B1_noVAE
pipeline_tag: text-to-image
---

# Realistic Vision V6 + Hyper-SD (4-step) — ONNX

ONNX export of [SG161222/Realistic_Vision_V6.0_B1_noVAE](https://huggingface.co/SG161222/Realistic_Vision_V6.0_B1_noVAE) paired with [stabilityai/sd-vae-ft-mse](https://huggingface.co/stabilityai/sd-vae-ft-mse) and the [ByteDance/Hyper-SD](https://huggingface.co/ByteDance/Hyper-SD) 4-step LoRA fused into the UNet. SD 1.5 architecture, 512×512 native, Euler scheduler, CFG = 1, **4 steps**.

Realistic Vision V6 is the photorealistic-portrait flagship of the SD 1.5 ecosystem. Trained on a narrow distribution (people, portraits, photography aesthetics), which is exactly *why* it's more stable across seeds than base SD 1.5 for those subjects.

> **Heads-up:** Realistic Vision is more NSFW-permissive than the other Hyper variants in this collection. Pair with content filters if that matters for your application.

Converted artifact. Training credit: SG161222 (Realistic Vision), Stability AI (sd-vae-ft-mse), ByteDance (Hyper-SD).

## What this repo contains

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # RV6 UNet + Hyper-SD-15 4-step LoRA fused in
vae_decoder/            # sd-vae-ft-mse (RV6 ships without VAE — paired here)
vae_encoder/
```

## How it was produced

1. Load `SG161222/Realistic_Vision_V6.0_B1_noVAE` via `diffusers`.
2. Replace the (missing) VAE with `stabilityai/sd-vae-ft-mse` — the SD 1.5 community-standard fine-tuned VAE.
3. Load `ByteDance/Hyper-SD/Hyper-SD15-4steps-lora.safetensors` via `peft`, `fuse_lora()` into UNet.
4. `optimum-cli export onnx`.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Conversion script: [`scripts/export-realistic-vision-hyper.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-realistic-vision-hyper.ps1).

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler |
| Steps | 4 |
| CFG / guidance scale | 1.0 |
| Negative prompt | Skip |
| Resolution | 512×512 native (best results); 768×768 OK |

## License

CreativeML OpenRAIL-M (SD 1.5 + Realistic Vision + Hyper-SD). License files included. By using this model you accept those terms.
