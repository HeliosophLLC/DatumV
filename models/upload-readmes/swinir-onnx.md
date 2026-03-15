---
license: apache-2.0
library_name: onnx
tags:
  - image-restoration
  - super-resolution
  - image-denoising
  - swinir
  - transformer
  - onnx
base_model: JingyunLiang/SwinIR
pipeline_tag: image-to-image
---

# SwinIR — Image Restoration (ONNX)

ONNX exports of [SwinIR](https://github.com/JingyunLiang/SwinIR) — Swin Transformer for Image Restoration. Two variants covering the two most common SwinIR use cases: real-world super-resolution and color denoising.

Re-exported from upstream PyTorch weights. Provenance trail: Liang et al. → JingyunLiang/SwinIR (cloned source) + pinned `.pth` checkpoints from the v0.0 GitHub release → `torch.onnx.export` (one pass per variant) → these files.

Toolchain: `torch 2.4.x` (CUDA 12.4), `timm` latest, `onnx` latest, `onnxruntime>=1.17`, opset 17, `do_constant_folding=True`, `dynamo=False` (forces the legacy TorchScript-based exporter; SwinIR's `.type_as()` buffer coercions trip the dynamo path's name-lineage tracking on torch >=2.5). Full conversion script: [`scripts/export-swinir.ps1`](https://github.com/HeliosophLLC/DatumIngest/blob/main/scripts/export-swinir.ps1) in the DatumIngest repo.

Credit: Jingyun Liang, Jiezhang Cao, Guolei Sun, Kai Zhang, Luc Van Gool, Radu Timofte (ETH Zurich and collaborators). Paper: *"SwinIR: Image Restoration Using Swin Transformer"*, ICCV 2021.

## What this repo contains

| File | Variant | Input → Output | Use |
|---|---|---|---|
| `swinir_realsr_x4.onnx` | SwinIR-L real-SR (4×) | 64×64 RGB → 256×256 RGB | Real-world image super-resolution (handles compression artifacts, sensor noise, mild blur as a side effect). ~110 MB. |
| `swinir_denoising_color_25.onnx` | SwinIR-M color DN | 128×128 RGB → 128×128 RGB | Color denoising at Gaussian noise σ=25 — the standard denoising-benchmark reference. ~45 MB. |

Both files share the same general I/O signature (NCHW float32 RGB in `[0, 1]`) — only the spatial dims differ.

## Input / output

| | `swinir_realsr_x4.onnx` | `swinir_denoising_color_25.onnx` |
|---|---|---|
| Input name | `image` | `image` |
| Input shape | `[batch, 3, 64, 64]` | `[batch, 3, 128, 128]` |
| Input dtype | float32 | float32 |
| Input range | `[0, 1]` RGB | `[0, 1]` RGB |
| Output name | `upscaled` | `denoised` |
| Output shape | `[batch, 3, 256, 256]` | `[batch, 3, 128, 128]` |
| Dynamic axes | batch only | batch only |

Spatial dims are **fixed** by design — SwinIR's windowed attention is brittle under dynamic H/W in ONNX Runtime's window-shift op. To process larger images, tile the input into 64×64 (SR) or 128×128 (DN) patches with some overlap, run inference per tile, and stitch the outputs.

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

# Pick the variant
sess = ort.InferenceSession("swinir_denoising_color_25.onnx")
# or:
# sess = ort.InferenceSession("swinir_realsr_x4.onnx")

img = Image.open("noisy.jpg").convert("RGB").resize((128, 128))
arr = np.asarray(img, dtype=np.float32) / 255.0          # HWC, [0,1]
arr = arr.transpose(2, 0, 1)[None, ...]                  # 1x3xHxW

result = sess.run(None, {"image": arr.astype(np.float32)})[0][0]
result = np.clip(result, 0.0, 1.0).transpose(1, 2, 0)    # back to HWC
result_img = Image.fromarray((result * 255).astype(np.uint8))
```

For larger images, see the upstream [`main_test_swinir.py`](https://github.com/JingyunLiang/SwinIR/blob/main/main_test_swinir.py) for a reference tiling implementation.

## Which one should I use?

- **`swinir_denoising_color_25.onnx`** — when you specifically want the Gaussian σ=25 reference denoiser (research papers, benchmark reproduction, comparing against other denoisers).
- **`swinir_realsr_x4.onnx`** — when you want 4× super-resolution on real-world photos and don't mind that it'll also clean up some noise / compression artifacts in the process.

For **blind real-world denoising** (unknown noise level), [SCUNet](https://huggingface.co/Heliosoph/scunet-onnx) is the better fit — SwinIR's `noise25` variant is trained for a specific noise level and degrades when the input noise pattern differs.

## License

**Apache-2.0** — same as the upstream [`JingyunLiang/SwinIR`](https://github.com/JingyunLiang/SwinIR) repo. `LICENSE` file included.
