---
license: other
license_name: stability-ai-community
license_link: https://stability.ai/community-license-agreement
library_name: diffusers
tags:
  - stable-diffusion
  - sdxl
  - turbo
  - add
  - onnx
  - text-to-image
  - 1-step
  - 4-step
base_model: stabilityai/sdxl-turbo
pipeline_tag: text-to-image
---

# SDXL Turbo — ONNX

ONNX export of [stabilityai/sdxl-turbo](https://huggingface.co/stabilityai/sdxl-turbo) — Stability AI's Adversarial Diffusion Distillation (ADD) of Stable Diffusion XL base. 512×512 native (NOT the 1024 of full SDXL), designed to run at CFG = 1 in **1-4 inference steps**.

This is a converted artifact, not a new model. All training credit belongs to Stability AI (Sauer, Lorenz, Blattmann, Rombach — ADD method, 2023).

## What this repo contains

A standard ONNX SDXL diffusers pipeline layout:

```
model_index.json
scheduler/
text_encoder/           # CLIP-L (768-dim)
text_encoder_2/         # OpenCLIP-G (1280-dim, also emits text_embeds pooled output)
tokenizer/
tokenizer_2/
unet/                   # SDXL UNet — encoder_hidden_states dim 2048 (concatenated)
vae_decoder/
vae_encoder/
LICENSE.md              # Stability AI Community License
```

`unet/model.onnx` is paired with `unet/model.onnx_data` (external-weights file); both must travel together.

## How it was produced

`optimum-cli export onnx --model stabilityai/sdxl-turbo <output>` against a pinned toolchain. No LoRA fuse step — SDXL Turbo is the distilled base, not a fine-tune.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Full conversion script: [`scripts/export-sdxl-turbo.ps1`](https://github.com/HeliosophLLC/DatumIngest/blob/main/scripts/export-sdxl-turbo.ps1) in the DatumIngest repo.

**Precision note:** fp32 only. The `optimum 1.24` / `torch 2.4` / opset-14 fp16 path produces a numerically broken UNet for SDXL-class models (NaN/Inf cascades through attention softmax + group norm). Same situation noted for [JuggernautXL Lightning](https://huggingface.co/Heliosoph/juggernaut-xl-lightning-onnx). Revisit fp16 when a confirmed working toolchain exists.

## Inference notes

| Setting | Value |
|---|---|
| Architecture | SDXL base — dual text encoders (CLIP-L 768 + OpenCLIP-G 1280, concatenated to 2048), pooled text embeds, time_ids micro-conditioning |
| Scheduler | Euler — ADD-distilled for short schedules |
| Steps | 1 (design point) to 4 (quality sweet spot) |
| CFG / guidance scale | 1.0 (no classifier-free guidance — Turbo was distilled without it) |
| Negative prompt | Skip — CFG = 1 ignores it |
| Resolution | **512×512** native (the distinguishing feature vs full SDXL's 1024×1024) |
| `time_ids` | `[512, 512, 0, 0, 512, 512]` — 512 target, NOT 1024 |
| VAE scale | 0.13025 (SDXL family) |
| Latent shape | `[1, 4, 64, 64]` |

The 512 `time_ids` is the load-bearing difference from full SDXL bodies — feeding 1024 produces correctly-typed tensors but visibly off-aesthetic outputs (SDXL Turbo was distilled with the 512 conditioning baked in).

## SDXL Turbo vs SD Turbo vs JuggernautXL Lightning

| Need | Pick |
|---|---|
| Fastest 512×512 from a clean Stability baseline | [SD Turbo](https://huggingface.co/Heliosoph/sd-turbo-onnx) (~5× smaller, ~2× less VRAM than SDXL Turbo) |
| Better prompt adherence + composition at 512×512 | **SDXL Turbo (this)** |
| 1024×1024 SDXL output with a photoreal fine-tune | [JuggernautXL Lightning](https://huggingface.co/Heliosoph/juggernaut-xl-lightning-onnx) |
| 1024×1024 SDXL output, clean Stability baseline | `stabilityai/stable-diffusion-xl-base-1.0` (not in catalog — full multi-step pipeline) |

SDXL Turbo is the right pick when 512 is enough output size and you want SDXL's prompt adherence at Turbo speed.

## License

**Stability AI Community License** — `LICENSE.md` included in this repo and travels with redistribution.

> ⚠️ **Commercial revenue threshold:** This license is free for research, individuals, and commercial use **below $1M annual revenue**. Above the threshold, commercial use requires a separate Stability AI Enterprise License. By downloading you agree to these terms and to Stability's [Acceptable Use Policy](https://stability.ai/use-policy), which prohibits CSAM, non-consensual deepfakes, harassment, malware generation, and similar misuse. The AUP propagates with the model — your derivatives and downstream redistributions must impose the same policy.

## Citation

```bibtex
@article{sauer2023adversarial,
  title   = {Adversarial Diffusion Distillation},
  author  = {Sauer, Axel and Lorenz, Dominik and Blattmann, Andreas and Rombach, Robin},
  journal = {arXiv preprint arXiv:2311.17042},
  year    = {2023}
}
```
