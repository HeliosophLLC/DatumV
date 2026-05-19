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
base_model: Lykon/DreamShaper
pipeline_tag: text-to-image
---

# DreamShaper + Hyper-SD (4-step) — ONNX

ONNX export of [Lykon/DreamShaper](https://huggingface.co/Lykon/DreamShaper) with the [ByteDance/Hyper-SD](https://huggingface.co/ByteDance/Hyper-SD) 4-step LoRA fused into the UNet. SD 1.5 architecture, 512×512 native, designed to run with the Euler scheduler at CFG = 1 in **4 inference steps**.

DreamShaper is Lykon's stylized SFW fine-tune — leans more illustrative / fantasy than AbsoluteReality, which is more photorealistic. Pick this one for D&D-style art, character portraits with painterly textures, and concept-art-leaning prompts.

This is a converted artifact, not a new model. All training credit belongs to Lykon (DreamShaper) and ByteDance (Hyper-SD).

## What this repo contains

```
model_index.json
feature_extractor/
scheduler/
text_encoder/
tokenizer/
unet/                   # DreamShaper UNet + Hyper-SD-15 4-step LoRA fused in
vae_decoder/
vae_encoder/
```

`unet/model.onnx` is paired with `unet/model.onnx_data` (external-weights file).

## How it was produced

1. Load `Lykon/DreamShaper` via `diffusers` (bundled VAE).
2. Load `ByteDance/Hyper-SD/Hyper-SD15-4steps-lora.safetensors` via `peft`, `fuse_lora()` it into the UNet.
3. Save the fused pipeline to a temp directory.
4. `optimum-cli export onnx --model <temp> <output>`.

Toolchain: `optimum 1.24.0`, `diffusers 0.31.0`, `transformers 4.45.2`, `torch 2.4.x` (CUDA 12.4). Full conversion script: [`scripts/export-dreamshaper-hyper.ps1`](https://github.com/HeliosophLLC/Heliosoph/blob/main/scripts/export-dreamshaper-hyper.ps1).

## Inference notes

| Setting | Value |
|---|---|
| Scheduler | Euler |
| Steps | 4 |
| CFG / guidance scale | 1.0 |
| Negative prompt | Skip |
| Resolution | 512×512 native |

## License

CreativeML OpenRAIL-M (inherited from SD 1.5 + DreamShaper) + the Hyper-SD LoRA's OpenRAIL-M. Both license files are included in this repo. By using this model you accept those terms.

## Citation

```bibtex
@misc{lykon-dreamshaper,
  author = {Lykon},
  title  = {DreamShaper},
  howpublished = {\url{https://huggingface.co/Lykon/DreamShaper}}
}
@article{ren2024hypersd,
  title   = {Hyper-SD: Trajectory Segmented Consistency Model for Efficient Image Synthesis},
  author  = {Ren, Yuxi and others},
  journal = {arXiv preprint arXiv:2404.13686},
  year    = {2024}
}
```
