---
license: creativeml-openrail-m
library_name: diffusers
tags:
  - stable-diffusion
  - sd-1.5
  - onnx
  - text-to-image
  - cfg
  - photorealistic
base_model: SG161222/Realistic_Vision_V6.0_B1_noVAE
pipeline_tag: text-to-image
---

# Realistic Vision V6 (CFG, full quality) — ONNX

ONNX export of [SG161222/Realistic_Vision_V6.0_B1_noVAE](https://huggingface.co/SG161222/Realistic_Vision_V6.0_B1_noVAE) paired with [stabilityai/sd-vae-ft-mse](https://huggingface.co/stabilityai/sd-vae-ft-mse). **No distillation LoRA** — this is the full, non-distilled UNet driven with classifier-free guidance. SD 1.5 architecture, 512×512 native, Euler scheduler, CFG ≈ 7.5, **~25 steps**.

This is the quality counterpart to the [4-step Hyper export](https://huggingface.co/Heliosoph/realistic-vision-hyper-onnx). The Hyper variant is distilled for fast, CFG-free, 1–4 step sampling; it's great for previews and batch work but caps fidelity and prompt adherence. This non-distilled export, run with classifier-free guidance, a negative prompt, and a normal step budget, recovers the sharp, prompt-faithful, clean-tone output people expect from Realistic Vision — at a higher per-image cost (the UNet runs twice per step, over ~25 steps).

Realistic Vision V6 is the photorealistic-portrait flagship of the SD 1.5 ecosystem. Trained on a narrow distribution (people, portraits, photography aesthetics), which is exactly *why* it's more stable across seeds than base SD 1.5 for those subjects.

> **Heads-up:** Realistic Vision is more NSFW-permissive than the other variants in this collection. Pair with content filters if that matters for your application.

Converted artifact. Training credit: SG161222 (Realistic Vision), Stability AI (sd-vae-ft-mse).

## What this repo contains

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # RV6 UNet, non-distilled (no LoRA)
vae_decoder/            # sd-vae-ft-mse (RV6 ships without VAE — paired here)
vae_encoder/
```

## How it was produced

1. Load `SG161222/Realistic_Vision_V6.0_B1_noVAE` via `diffusers`.
2. Replace the (missing) VAE with `stabilityai/sd-vae-ft-mse` — the SD 1.5 community-standard fine-tuned VAE.
3. `optimum-cli export onnx` (no LoRA fusion step).

Exported at FP32 — the SD 1.5 VAE is fp16-fragile (it overflows and posterizes), so the quality export stays full precision.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Conversion script: [`scripts/export-realistic-vision-cfg.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-realistic-vision-cfg.ps1).

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler |
| Steps | 25 (20–30 sweet spot) |
| CFG / guidance scale | 7.5 (6–9 usual range) |
| Negative prompt | Supported — use it |
| Resolution | 512×512 native (best results); 768×768 OK |

Classifier-free guidance runs the UNet twice per step (conditional + unconditional) and combines them as `uncond + guidance · (cond − uncond)`. The negative prompt only takes effect when `guidance > 1`.

## License

CreativeML OpenRAIL-M (SD 1.5 + Realistic Vision). License files included. By using this model you accept those terms.
