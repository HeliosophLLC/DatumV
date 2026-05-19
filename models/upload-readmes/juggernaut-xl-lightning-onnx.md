---
license: openrail++
library_name: diffusers
tags:
  - stable-diffusion-xl
  - sdxl
  - lightning
  - onnx
  - text-to-image
  - 4-step
  - photorealistic
  - fantasy
base_model: RunDiffusion/Juggernaut-XL-Lightning
pipeline_tag: text-to-image
---

# JuggernautXL Lightning — ONNX (fp32)

ONNX export of [RunDiffusion/Juggernaut-XL-Lightning](https://huggingface.co/RunDiffusion/Juggernaut-XL-Lightning) — an SDXL fine-tune + Lightning distillation. SDXL architecture (1024×1024 native, dual text encoders), runs in **4 inference steps** at CFG = 1.

JuggernautXL is one of the most-downloaded SDXL fine-tunes for photorealistic and fantasy-character art. Lightning distillation brings it down to 4 steps (vs SDXL's native 30+) at minimal quality cost. Pick this when SDXL-Turbo's 1-step output isn't quality-stable enough but you still want fast inference.

Converted artifact. Training credit: RunDiffusion (Juggernaut-XL-Lightning).

## What this repo contains

A standard SDXL ONNX diffusers pipeline layout:

```
model_index.json
scheduler/
text_encoder/           # CLIP-L (OpenAI)
text_encoder_2/         # OpenCLIP-G
tokenizer/
tokenizer_2/
unet/                   # JuggernautXL-Lightning UNet (~2.6B params)
vae_decoder/
vae_encoder/
```

`unet/model.onnx` has external weights in `unet/model.onnx_data`. Same for `text_encoder_2/` if present.

## How it was produced

`optimum-cli export onnx --model RunDiffusion/Juggernaut-XL-Lightning --task text-to-image --library diffusers --device cuda --no-post-process <output>`

via [`scripts/export-batch-onnx.ps1 -Models juggernaut-xl-lightning`](https://github.com/HeliosophLLC/Heliosoph/blob/main/scripts/export-batch-onnx.ps1).

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4).

## Why fp32, not fp16

The `optimum 1.24` + `torch 2.4` + opset-14 fp16 export path produces a numerically broken UNet for SDXL-class models (all-NaN noise predictions from valid conditioning). The fp32 export works correctly. Revisit fp16 if/when a confirmed working toolchain ships.

Practical impact: ~13 GB on disk vs ~6.5 GB at fp16. VRAM budget at inference: ~7–8 GB with the standard scheduler.

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler (epsilon prediction — Lightning 4-step is **not** sample-prediction; the 2-step variant is, but this is the 4-step) |
| Steps | 4 |
| CFG / guidance scale | 1.0 |
| Negative prompt | Skip |
| Resolution | 1024×1024 native |

## License

**CreativeML OpenRAIL++-M** inherited from `stabilityai/stable-diffusion-xl-base-1.0` + JuggernautXL's terms. The included `LICENSE-creativeml-openrail-pp-m.md` travels with redistribution. By using this model you accept those terms — review the use-based restrictions in section II of the license.
