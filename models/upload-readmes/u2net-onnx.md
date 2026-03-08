---
license: apache-2.0
library_name: onnx
tags:
  - image-segmentation
  - salient-object-detection
  - background-removal
  - u2net
  - onnx
base_model: xuebinqin/U-2-Net
pipeline_tag: image-segmentation
---

# U²-Net — Salient Object Segmentation (ONNX)

ONNX checkpoints of [xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net) — a nested U-structure network for salient-object detection. Trained to separate the "main subject" of an image from the background. Pair the output mask with `image_cutout()` for background removal, or with `apply_colormap()` to visualize saliency.

Not converted locally — these are the official ONNX checkpoints, republished by [danielgatis/rembg](https://github.com/danielgatis/rembg) in a convenient release.

Credit: Xuebin Qin, Zichen Zhang, Chenyang Huang, Masood Dehghan, Osmar R. Zaiane, Martin Jagersand — University of Alberta (*"U²-Net: Going Deeper with Nested U-Structure for Salient Object Detection"*, Pattern Recognition 2020).

## What this repo contains

| File | Params | Size | Use |
|---|---|---|---|
| `u2netp.onnx` | 4.7M | ~4.7 MB | **Recommended default.** Distilled lite variant — CPU/mobile/edge-friendly |
| `u2net.onnx` | 176M | ~170 MB | Full network — sharper edges on hair, fur, lace, thin structures |

Both files share the same input/output tensor signature, so inference code is identical — you can swap variants without rewriting anything.

## Input / output

| | Spec |
|---|---|
| Input name | `input.1` (verify in Netron) |
| Input shape | `[1, 3, 320, 320]` (NCHW) |
| Input dtype | float32 |
| Input color order | **RGB** |
| Preprocessing | Resize to 320×320, scale to `[0,1]`, normalize with ImageNet stats: `mean=[0.485, 0.456, 0.406]`, `std=[0.229, 0.224, 0.225]` |
| Outputs | 7 tensors: `d0`..`d6`, saliency maps at decreasing resolution. **`d0` is the final fused mask** — the other six are intermediate supervisions used during training; ignore them at inference. |
| Output shape (per map) | `[1, 1, 320, 320]` |
| Output meaning | Per-pixel saliency in `[0, 1]` — higher = more likely to be the subject. Threshold (typically ~0.5) for a binary mask, or use raw values as a soft alpha. |

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

sess = ort.InferenceSession("u2netp.onnx")  # or "u2net.onnx" — same signature

# Remember the original size so we can resize the mask back at the end
orig = Image.open("photo.jpg").convert("RGB")
W, H = orig.size

# Preprocess
img = orig.resize((320, 320), Image.BILINEAR)
arr = np.asarray(img, dtype=np.float32) / 255.0
arr = (arr - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225]
arr = arr.transpose(2, 0, 1)[None, ...].astype(np.float32)

# Inference — outputs is a list of 7 tensors; d0 is index 0
outputs = sess.run(None, {sess.get_inputs()[0].name: arr})
d0 = outputs[0][0, 0]  # 320x320 saliency

# Normalize (U²-Net outputs aren't strictly in [0,1] before squashing)
d0 = (d0 - d0.min()) / (d0.max() - d0.min() + 1e-8)

# Resize mask back to original image dimensions
mask = Image.fromarray((d0 * 255).astype(np.uint8)).resize((W, H), Image.BILINEAR)
```

For background removal: apply `mask` as the alpha channel to the original RGB image (RGBA cutout).

## Which one should I use?

- **`u2netp`** is the right default. 4.7 MB on disk, ~30 ms / image on CPU, mask quality good enough for >90% of background-removal and saliency-mapping use cases. Loads instantly.
- **`u2net`** earns its disk + latency cost on **fine-edge** subjects: hair, fur, lace, complex foliage, transparent objects. If the lite variant's edges look "blocky" on your inputs, the full model is the upgrade.

For interactive segmentation (clicks / boxes / prompts), pair with [MobileSAM](https://huggingface.co/Heliosoph/sam-onnx) instead — U²-Net is automatic / non-interactive.

## Excluded variant

The original [xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net) repo also ships a third checkpoint called `u2net_portrait` (line-drawing portrait sketches). It's **deliberately not bundled here** — it was trained on the APDrawing dataset, which carries non-commercial restrictions that would taint the otherwise-clean Apache-2.0 status of this bundle. If you need it, grab it directly from the upstream repo and read the dataset terms first.

## License

**Apache-2.0** — same as the upstream [xuebinqin/U-2-Net](https://github.com/xuebinqin/U-2-Net) repo. `LICENSE` file included. The danielgatis/rembg release just bundles the original weights; no relicensing occurred.
