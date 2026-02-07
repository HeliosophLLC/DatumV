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
base_model: Lykon/AbsoluteReality
pipeline_tag: text-to-image
---

# AbsoluteReality + Hyper-SD (4-step) — ONNX

ONNX export of [Lykon/AbsoluteReality](https://huggingface.co/Lykon/AbsoluteReality) with the [ByteDance/Hyper-SD](https://huggingface.co/ByteDance/Hyper-SD) 4-step LoRA fused into the UNet. SD 1.5 architecture, 512×512 native, designed to run with the Euler scheduler at CFG = 1 in **4 inference steps**.

This is a converted artifact, not a new model. All training credit belongs to Lykon (AbsoluteReality) and ByteDance (Hyper-SD).

## What this repo contains

A standard ONNX diffusers pipeline layout:

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # AbsoluteReality UNet + Hyper-SD-15 4-step LoRA fused in
vae_decoder/
vae_encoder/
```

`unet/model.onnx` is paired with `unet/model.onnx_data` (external-weights file). Both must be downloaded.

## How it was produced

1. Load `Lykon/AbsoluteReality` via `diffusers` (uses its bundled VAE — no separate VAE pairing needed).
2. Load `ByteDance/Hyper-SD/Hyper-SD15-4steps-lora.safetensors` via `peft` and call `fuse_lora()` on the UNet.
3. Save the fused pipeline to a temp directory.
4. `optimum-cli export onnx --model <temp> <output>`.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4), `peft` latest. Full conversion script: [`scripts/export-absolute-reality-hyper.ps1`](https://github.com/HeliosophLLC/DatumIngest/blob/main/scripts/export-absolute-reality-hyper.ps1) in the DatumIngest repo.

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler (sample/x0 prediction is **not** required — 4-step Hyper is epsilon) |
| Steps | 4 |
| CFG / guidance scale | 1.0 (no classifier-free guidance) |
| Negative prompt | Skip — CFG = 1 ignores it |
| Resolution | 512×512 native (768×768 works, smaller resolutions degrade fast) |

## License

This export inherits **CreativeML OpenRAIL-M** from the base SD 1.5 lineage and AbsoluteReality. The Hyper-SD LoRA also ships under OpenRAIL-M (ByteDance). Both `LICENSE-*.md` files are included in this repo and travel with redistribution. By using this model you accept those terms — see the included license files for acceptable-use clauses.

## Citation

If you use this in academic work, cite both the base model and the distillation method:

```bibtex
@misc{lykon-absolutereality,
  author = {Lykon},
  title  = {AbsoluteReality},
  howpublished = {\url{https://huggingface.co/Lykon/AbsoluteReality}}
}
@article{ren2024hypersd,
  title   = {Hyper-SD: Trajectory Segmented Consistency Model for Efficient Image Synthesis},
  author  = {Ren, Yuxi and others},
  journal = {arXiv preprint arXiv:2404.13686},
  year    = {2024}
}
```
