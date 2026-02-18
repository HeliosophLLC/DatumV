---
license: mit
library_name: onnx
tags:
  - depth-estimation
  - dpt
  - midas
  - onnx
base_model: Intel/dpt-large
pipeline_tag: depth-estimation
---

# DPT-Large — Monocular Depth Estimation (ONNX)

ONNX export of [Intel/dpt-large](https://huggingface.co/Intel/dpt-large) — the Dense Prediction Transformer for monocular depth. ~330M params, originally published as part of the [MiDaS](https://github.com/isl-org/MiDaS) project at Intel Intelligent Systems Lab.

Re-hosted under Heliosoph for distribution stability — Intel's published checkpoint is the authoritative source.

Credit: Intel ISL (DPT / MiDaS team — Ranftl et al.).

## What this repo contains

```
dpt_large_384.onnx     # ~1.3 GB
```

A single ONNX file. No tokenizer, no preprocessor config — preprocessing is fixed by convention.

## Input/output shape

| | Spec |
|---|---|
| Input name | `pixel_values` (or `image` — verify in Netron) |
| Input shape | `[1, 3, 384, 384]` |
| Input dtype | float32 |
| Preprocessing | RGB, divide by 255, normalize by `mean=[0.5, 0.5, 0.5]` / `std=[0.5, 0.5, 0.5]` |
| Output shape | `[1, 384, 384]` |
| Output meaning | Relative depth — **not** metric. Lower values = farther; higher values = closer. Linearly map to your visualization range. |

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

sess = ort.InferenceSession("dpt_large_384.onnx")

# Resize input image to 384×384, normalize, NCHW
img = Image.open("photo.jpg").convert("RGB").resize((384, 384))
arr = (np.asarray(img, dtype=np.float32) / 255.0 - 0.5) / 0.5  # HWC, [-1,1]
arr = arr.transpose(2, 0, 1)[None, ...]                         # 1x3x384x384

depth = sess.run(None, {sess.get_inputs()[0].name: arr})[0][0]  # 384x384
```

For metric depth, pair with a calibration scheme — DPT-Large is trained for relative depth and will not give you "this object is 1.7m away" without further work.

## When to pick DPT-Large

- **Quality matters more than speed**: ~330M params, slowest variant in the MiDaS family.
- **Single static image, not video**: no temporal smoothing built in.
- **GPU available**: CPU inference is workable but slow (~1–2 sec on consumer CPU).

For real-time or edge use, prefer `dpt-hybrid` or `midas-small` — not in this repo, but available as separate uploads upstream.

## License

**MIT** — same as upstream. `LICENSE` file included.
