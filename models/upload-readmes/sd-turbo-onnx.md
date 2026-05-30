---
license: other
license_name: stability-ai-community
license_link: https://stability.ai/community-license-agreement
library_name: diffusers
tags:
  - stable-diffusion
  - sd-2.1
  - turbo
  - add
  - onnx
  - text-to-image
  - 1-step
  - 4-step
base_model: stabilityai/sd-turbo
pipeline_tag: text-to-image
---

# SD Turbo — ONNX

ONNX export of [stabilityai/sd-turbo](https://huggingface.co/stabilityai/sd-turbo) — Stability AI's Adversarial Diffusion Distillation (ADD) of Stable Diffusion 2.1. 512×512 native, designed to run at CFG = 1 in **1-4 inference steps** (1 = design point for max speed, 4 = quality sweet spot).

This is a converted artifact, not a new model. All training credit belongs to Stability AI (Sauer, Lorenz, Blattmann, Rombach — ADD method, 2023).

## What this repo contains

A standard ONNX diffusers pipeline layout:

```
model_index.json
feature_extractor/
scheduler/
text_encoder/           # OpenCLIP ViT-H/14 (1024-dim embeddings — NOT 768 like SD 1.5)
tokenizer/
unet/                   # SD 2.1 UNet, encoder_hidden_states dim 1024
vae_decoder/
vae_encoder/
LICENSE.md              # Stability AI Community License
```

`unet/model.onnx` is paired with `unet/model.onnx_data` (external-weights file). Both must be downloaded.

## How it was produced

`optimum-cli export onnx --model stabilityai/sd-turbo <output>` against a pinned toolchain. No LoRA fuse step — SD Turbo is the distilled base, not a fine-tune.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Full conversion script: [`scripts/export-sd-turbo.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-sd-turbo.ps1) in the Heliosoph repo.

## Inference notes

| Setting | Value |
|---|---|
| Architecture | Stable Diffusion 2.1 (single CLIP-H text encoder, 1024-dim) |
| Scheduler | Euler (or EulerAncestral) — ADD-distilled for short schedules |
| Steps | 1 (design point) to 4 (quality sweet spot); beyond 4 returns diminishing gains |
| CFG / guidance scale | 1.0 (no classifier-free guidance — Turbo was distilled without it) |
| Negative prompt | Skip — CFG = 1 ignores it |
| Resolution | 512×512 native (other resolutions degrade fast) |
| VAE scale | 0.18215 (SD 2.x family) |
| Latent shape | `[1, 4, 64, 64]` |

## SD Turbo vs SDXL Turbo vs the SD 1.5 Hyper family

| Need | Pick |
|---|---|
| Fastest 512×512 from a clean Stability baseline | **SD Turbo (this)** |
| Better prompt adherence + composition at 512×512 | [SDXL Turbo](https://huggingface.co/Heliosoph/sdxl-turbo-onnx) (heavier — ~5× the disk and ~2× the VRAM) |
| 512×512 with a specific photoreal / fantasy / illustrative fine-tune | SD 1.5 Hyper family (AbsoluteReality / DreamShaper / epiCRealism / RealisticVision / etc.) |
| 1024×1024 SDXL output | [JuggernautXL Lightning](https://huggingface.co/Heliosoph/juggernaut-xl-lightning-onnx) |

SD Turbo is the right "small baseline" pick when you want Stability's canonical fast model rather than a community fine-tune, and when 512×512 is enough.

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
