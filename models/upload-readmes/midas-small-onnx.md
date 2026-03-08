---
license: mit
library_name: onnx
tags:
  - depth-estimation
  - midas
  - mobile
  - edge
  - onnx
base_model: isl-org/MiDaS
pipeline_tag: depth-estimation
---

# MiDaS v2.1 Small — Monocular Depth Estimation (ONNX)

ONNX checkpoint of [Intel ISL's MiDaS v2.1 small](https://github.com/isl-org/MiDaS) — an EfficientNet-Lite3 encoder paired with a lightweight depth decoder. ~21M params, 256×256 input, CPU-friendly. Sibling to DPT-Large but ~16× smaller and ~20× faster on CPU.

Not converted locally — this is the ONNX file isl-org publishes directly in the [v2_1 GitHub release](https://github.com/isl-org/MiDaS/releases/tag/v2_1).

Credit: Intel Intelligent Systems Lab (MiDaS team — Ranftl, Lasinger, Hafner, Schindler, Koltun).

## What this repo contains

```
midas_v21_small_256.onnx   # ~80 MB — fp32, EfficientNet-Lite3 backbone, 256×256 input
```

A single ONNX file. No tokenizer, no preprocessor config — preprocessing is fixed by the architecture convention.

## Input / output

| | Spec |
|---|---|
| Input name | `input.1` (verify in Netron) |
| Input shape | `[1, 3, 256, 256]` (NCHW) |
| Input dtype | float32 |
| Input color order | **BGR** — note this differs from DPT-Large (which expects RGB) |
| Preprocessing | Resize to 256×256, scale to `[0,1]`, normalize with ImageNet stats: `mean=[0.485, 0.456, 0.406]`, `std=[0.229, 0.224, 0.225]` |
| Output shape | `[1, 256, 256]` |
| Output meaning | Single-channel **relative** depth (higher = closer, lower = farther). **Not metric.** Linearly map to your visualization range. |

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

sess = ort.InferenceSession("midas_v21_small_256.onnx")

# Resize, BGR (note: PIL is RGB by default — swap channels for MiDaS-small)
img = Image.open("photo.jpg").convert("RGB").resize((256, 256))
arr = np.asarray(img, dtype=np.float32) / 255.0
arr = arr[..., ::-1]                                                   # RGB -> BGR
arr = (arr - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225]            # ImageNet normalize
arr = arr.transpose(2, 0, 1)[None, ...].copy().astype(np.float32)      # NCHW

depth = sess.run(None, {sess.get_inputs()[0].name: arr})[0][0]         # 256x256
```

For metric depth, pair with a calibration scheme — MiDaS is trained for relative depth and will not give you "this object is 1.7 m away" without further work.

## When to pick MiDaS-small

- **Real-time, edge, CPU, or mobile**: ~50 ms / image on consumer CPU, ~80 MB on disk.
- **Coarse depth is enough**: relative ordering of "what's close vs far" matters more than fine boundary precision.
- **Pair with DPT-Large**: a common pattern is to run MiDaS-small first for a quick estimate, then fall back to DPT-Large only when high-quality depth is needed for a specific frame.

For sharper boundaries and higher absolute quality (at ~16× the disk + GPU latency), reach for `dpt-large` instead — same model family, same upstream lab.

## License

**MIT** — same as the upstream [isl-org/MiDaS](https://github.com/isl-org/MiDaS) repo. `LICENSE` file included.

Note: a separate Intel-published variant of DPT-Large lives on HuggingFace at `Intel/dpt-large` under **Apache-2.0**. Same model family, different distribution channel, different licenses. The checkpoint in *this* repo (v2_1 GitHub release) inherits MIT from the upstream GitHub repo.
