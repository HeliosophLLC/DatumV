---
license: bsd-3-clause
library_name: onnx
tags:
  - super-resolution
  - real-esrgan
  - image-upscaling
  - onnx
base_model: xinntao/Real-ESRGAN
pipeline_tag: image-to-image
---

# Real-ESRGAN-x4v3 — 4× Image Super-Resolution (ONNX)

ONNX export of [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)'s general-purpose 4× upscaler (`realesr-general-x4v3` variant). At only ~5 MB it punches well above its weight on real photographs.

Re-hosted under Heliosoph for distribution stability — xinntao's GitHub releases are the authoritative source.

Credit: Wang Xintao et al. (Tencent ARC Lab).

## What this repo contains

```
realesr-general-x4v3.onnx    # ~5 MB
```

A single ONNX file. The model upscales any input image to 4× its width and height with learned restoration of fine detail.

## Input/output

| | Spec |
|---|---|
| Input name | `input` |
| Input shape | dynamic — `[1, 3, H, W]` |
| Input dtype | float32, range `[0, 1]` |
| Input color order | **RGB** |
| Preprocessing | Divide by 255 (no mean/std subtraction). Tile large images to avoid OOM. |
| Output | `[1, 3, H*4, W*4]` in `[0, 1]` |

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

sess = ort.InferenceSession("realesr-general-x4v3.onnx")

img = Image.open("low_res.jpg").convert("RGB")
arr = np.asarray(img, dtype=np.float32) / 255.0
arr = arr.transpose(2, 0, 1)[None, ...]  # 1x3xHxW

upscaled = sess.run(None, {"input": arr})[0][0]  # 3 x (H*4) x (W*4)
upscaled = (upscaled.transpose(1, 2, 0) * 255).clip(0, 255).astype(np.uint8)
Image.fromarray(upscaled).save("upscaled_4x.png")
```

For images larger than ~512×512, tile the input into overlapping patches and stitch the outputs — otherwise inference memory grows quadratically. Real-ESRGAN's reference implementation includes a tiler.

## When to use which Real-ESRGAN variant

This repo ships `realesr-general-x4v3` only. Other Real-ESRGAN variants exist upstream:

| Variant | Best for |
|---|---|
| **`realesr-general-x4v3`** (this repo) | General-purpose photos, illustrations, screenshots |
| `RealESRGAN_x4plus_anime_6B` | Anime / cartoon-style images |
| `RealESRGAN_x4plus` | Photographs, more aggressive enhancement |

The `general-x4v3` is the safest default — it doesn't over-sharpen real photos or hallucinate on noisy input.

## License

**BSD-3-Clause** — same as upstream. `LICENSE` file included.
