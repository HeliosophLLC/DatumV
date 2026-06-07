---
license: apache-2.0
library_name: onnx
tags:
  - image-restoration
  - image-denoising
  - blind-denoising
  - scunet
  - onnx
base_model: cszn/SCUNet
pipeline_tag: image-to-image
---

# SCUNet — Image Denoising (ONNX, full 8-variant bundle)

ONNX exports of [SCUNet](https://github.com/cszn/SCUNet) (Swin-Conv-UNet) — Kai Zhang et al., 2022. Hybrid CNN + Swin Transformer architecture for image denoising. This repo bundles all 8 published checkpoints from the upstream `model_zoo/` so you get the full size / variant ladder in a single download.

Re-exported from upstream PyTorch weights. Provenance trail: Zhang et al. → cszn/SCUNet `model_zoo/*.pth` → `torch.onnx.export` (one pass per checkpoint) → these files.

Toolchain: `torch 2.4.x` (CUDA 12.4), `timm` latest, `einops` latest, `thop` latest, `onnx` latest, `onnxruntime>=1.17`, opset 17, `do_constant_folding=True`. Full conversion script: [`scripts/export-kair.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-kair.ps1) in the Heliosoph repo (runs once per `.pth` checkpoint via `-Model scunet-color` or `-Model scunet-gray`).

Credit: Kai Zhang, Yawei Li, Jingyun Liang, Jiezhang Cao, Yulun Zhang, Hao Tang, Deng-Ping Fan, Radu Timofte, Luc Van Gool. Paper: *"Practical Blind Image Denoising via Swin-Conv-UNet and Data Synthesis"*, 2022.

## What this repo contains

Each variant ships as an `.onnx` (small graph file) + `.onnx.data` (~70 MB of external tensor data) sibling pair — torch's ONNX exporter externalizes weights at the size/opset combination used here. **Both files must be present in the same directory at load time** — the `.onnx` references the `.data` by relative filename.

### Color variants (5)

| File pair | Variant | Training | When to use |
|---|---|---|---|
| `scunet_color_real_psnr.onnx[.data]` | Blind real-world, PSNR | Mixed synthetic degradations (Gaussian + JPEG + downsampling), L1/L2 pixel loss | **Recommended default.** General-purpose photo denoising. Stays faithful to input. |
| `scunet_color_real_gan.onnx[.data]` | Blind real-world, GAN | Same training data, adversarial + perceptual loss | Consumer photo cleanup — sharper output, invents plausible texture. Skip when fidelity matters. |
| `scunet_color_15.onnx[.data]` | Gaussian σ=15 | White Gaussian noise σ=15 (light) | Light noise (ISO grain). Beats blind on matched conditions; over-smooths cleaner inputs. |
| `scunet_color_25.onnx[.data]` | Gaussian σ=25 | σ=25 (moderate) | Standard denoising-benchmark reference — apples-to-apples comparison with other papers at σ=25. |
| `scunet_color_50.onnx[.data]` | Gaussian σ=50 | σ=50 (heavy) | Extreme low-light / heavy-grain photos. Over-smooths anything cleaner. |

### Grayscale variants (3)

| File pair | Variant | When to use |
|---|---|---|
| `scunet_gray_15.onnx[.data]` | Gaussian σ=15 | Grayscale workflows (medical, document, B&W photo) at light noise. |
| `scunet_gray_25.onnx[.data]` | Gaussian σ=25 | Standard grayscale-denoising benchmark level. |
| `scunet_gray_50.onnx[.data]` | Gaussian σ=50 | Heavy-grain grayscale (astrophotography, degraded scans). |

The grayscale variants are ~3× cheaper to run than the color variants on grayscale inputs (they accept 1-channel input directly; the color variants need the gray channel replicated across RGB).

## Input / output (all variants)

| | Color (in_nc=3) | Gray (in_nc=1) |
|---|---|---|
| Input name | `image` | `image` |
| Input shape | `[batch, 3, H, W]` (NCHW) | `[batch, 1, H, W]` |
| Input dtype | float32 | float32 |
| Input range | `[0, 1]` RGB | `[0, 1]` Y |
| Constraint | H and W divisible by 8 | H and W divisible by 8 |
| Output name | `denoised` | `denoised` |
| Output shape | `[batch, 3, H, W]` (same as input) | `[batch, 1, H, W]` |
| Dynamic axes | batch, height, width | batch, height, width |

All variants share the same forward-pass shape; the only differences are the input channel count and the trained weights.

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

# Pick a variant. Both the .onnx and .onnx.data must be present in
# the same directory — ORT resolves the external data automatically.
sess = ort.InferenceSession("scunet_color_real_psnr.onnx")

img = Image.open("noisy.jpg").convert("RGB")
W, H = img.size
W8, H8 = (W // 8) * 8, (H // 8) * 8                          # 8-align
img = img.crop((0, 0, W8, H8))

arr = np.asarray(img, dtype=np.float32) / 255.0              # HWC, [0,1]
arr = arr.transpose(2, 0, 1)[None, ...]                      # 1x3xHxW

result = sess.run(None, {"image": arr.astype(np.float32)})[0][0]
result = np.clip(result, 0.0, 1.0).transpose(1, 2, 0)
Image.fromarray((result * 255).astype(np.uint8)).save("denoised.jpg")
```

## Which one should I use?

- **General-purpose photo denoising**: `scunet_color_real_psnr` — blind, faithful, no guesswork required.
- **Consumer photo cleanup** (subjectively pretty matters more than ground truth): `scunet_color_real_gan`.
- **Matched-σ benchmark or known-noise scenario**: pick the `_15`, `_25`, or `_50` variant that matches your noise level.
- **Grayscale (medical / document / B&W)**: use `scunet_gray_*` directly — ~3× faster than the color variant on gray inputs.
- **Comparison demos**: the σ-specialist variants are great for showing matched-vs-mismatched specialist behavior. Run the same noisy image through `scunet_color_{15,25,50}` and the differences are visually obvious.

For **fixed-σ Gaussian denoising in a research-benchmark context**, [SwinIR's `swinir_denoising_color_25`](https://huggingface.co/Heliosoph/swinir-onnx) is the apples-to-apples transformer counterpart. For **denoising + sharpening as one step**, look at NAFNet ([opencv/deblurring_nafnet](https://huggingface.co/opencv/deblurring_nafnet)) — different task (deblur) but adjacent.

## License

**Apache-2.0** — same as the upstream [`cszn/SCUNet`](https://github.com/cszn/SCUNet) repo. `LICENSE` file included.
