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
base_model: emilianJR/epiCRealism
pipeline_tag: text-to-image
---

# epiCRealism (CFG, full quality) — ONNX

ONNX export of [emilianJR/epiCRealism](https://huggingface.co/emilianJR/epiCRealism) (which ships its own VAE). **No distillation LoRA** — this is the full, non-distilled UNet driven with classifier-free guidance. SD 1.5 architecture, 512×512 native, Euler scheduler, CFG ≈ 7.5, **~25 steps**.

This is the quality counterpart to the [4-step Hyper export](https://huggingface.co/Heliosoph/epicrealism-hyper-onnx). The Hyper variant is distilled for fast, CFG-free, 1–4 step sampling; it's great for previews and batch work but caps fidelity and prompt adherence. This non-distilled export, run with classifier-free guidance, a negative prompt, and a normal step budget, recovers the sharp, prompt-faithful output epiCRealism is known for — at a higher per-image cost (the UNet runs twice per step, over ~25 steps).

epiCRealism is a photoreal SD 1.5 fine-tune with broad subject coverage — strongest on environments, landscapes, architecture, interiors, and natural lighting.

Converted artifact. Training credit: emilianJR (epiCRealism).

## What this repo contains

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # epiCRealism UNet, non-distilled (no LoRA)
vae_decoder/            # epiCRealism bundled VAE
vae_encoder/
```

## How it was produced

1. Load `emilianJR/epiCRealism` via `diffusers` (uses its bundled VAE).
2. `optimum-cli export onnx` (no LoRA fusion step).

Exported at FP32 — the SD 1.5 VAE is fp16-fragile (it overflows and posterizes), so the quality export stays full precision.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Conversion script: [`scripts/export-epicrealism-cfg.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-epicrealism-cfg.ps1).

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

CreativeML OpenRAIL-M (SD 1.5 + epiCRealism). License files included. By using this model you accept those terms.
