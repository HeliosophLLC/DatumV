---
license: apache-2.0
library_name: onnx
tags:
  - depth-estimation
  - pose-estimation
  - multi-view-geometry
  - depth-anything-3
  - dinov2
  - onnx
base_model: depth-anything/DA3-BASE
pipeline_tag: depth-estimation
---

# Depth Anything 3 Base, 4-View — Depth + Camera Pose Recovery (ONNX)

Multi-view ONNX export of [depth-anything/DA3-BASE](https://huggingface.co/depth-anything/DA3-BASE): the same Apache-2.0 any-view checkpoint as [Heliosoph/da3-base-onnx](https://huggingface.co/Heliosoph/da3-base-onnx), traced with a **4-frame window** so the cross-view attention — and therefore the pose head — actually functions. Feed 4 views of a scene in one call; get per-view depth, confidence, camera intrinsics, and **relative camera poses** (first view is the reference).

**Why a fixed view count.** ONNX tracing bakes the view count into the cross-view attention reshapes. This is not cosmetic: a graph traced at V=2 *runs* at V=4 but produces silently wrong numbers (~5e-1 relative error, measured). So the views axis is pinned at 4 and onnxruntime rejects any other count at the input — for a different window size, re-run the conversion script with `-Views N`. Pose is only defined within a window; stitch longer sequences with overlapping windows.

**Scale.** Depth and pose translations share one **unknown global scale** (the standard any-view ambiguity). Shapes and relative geometry are right; absolute size isn't. To land in real meters, anchor against a metric estimator on the same frames — e.g. [Heliosoph/da3metric-large-onnx](https://huggingface.co/Heliosoph/da3metric-large-onnx): `s = median(d_metric / d_base)` over confidence-gated pixels, then scale the translations by `s` (rotations and K unchanged).

Provenance, toolchain, and exporter workarounds are identical to the single-view repo (see its card): official `depth-anything-3 0.1.1` package, torch 2.4.x, opset 17, fp32 trace, `cartesian_prod` and scripted-`affine_inverse` shims. Conversion script: [`scripts/export-da3metric.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-da3metric.ps1) (this export: `-Views 4`). Validation: fp32 ONNX matches PyTorch to 6.0e-06 max relative error across all four heads at V=4; batch=2 verified item-wise (windows in a batch don't leak into each other's poses); fp16 matches fp32 to ≤1.1e-03.

Credit: Haotong Lin, Sili Chen, Jun Hao Liew, Donny Y. Chen, Zhenyu Li, Guang Shi, Jiashi Feng, Bingyi Kang (ByteDance Seed). Paper: *"Depth Anything 3: Recovering the Visual Space from Any Views"*, 2025.

## What this repo contains

| File | Variant | Size | Use |
|---|---|---|---|
| `model.onnx` | fp32 | ~394 MB | Default — matches the PyTorch upstream to ~1e-5. |
| `model_fp16.onnx` | fp16 | ~198 MB | Half precision, **I/O stays fp32** (`keep_io_types`) — drop-in swap. |
| `config.json` | — | <1 KB | Upstream DA3 model config (provenance / re-instantiation). |

## Input / output

| | Spec |
|---|---|
| Input name | `images` |
| Input shape | `[batch, 4, 3, 504, 504]` — **exactly 4 views**, each preprocessed like a single image |
| Input dtype | float32 (both variants) |
| Preprocessing | per view: RGB, scale to [0,1], ImageNet mean/std |
| Output `depth` | `[batch, 4, 504, 504]` — up-to-scale depth per view, bigger = farther |
| Output `depth_conf` | `[batch, 4, 504, 504]` — per-pixel confidence per view |
| Output `extrinsics` | `[batch, 4, 3, 4]` — per-view `[R \| t]` world→camera, poses relative within the window (view 0 ≈ reference) |
| Output `intrinsics` | `[batch, 4, 3, 3]` — per-view K **at the 504×504 grid** (rescale via `diag(W/504, H/504, 1) · K`) |
| Dynamic axes | batch only (views and resolution are pinned in the trace) |

## How to use

```python
import numpy as np
import onnxruntime as ort
from PIL import Image

MEAN, STD = [0.485, 0.456, 0.406], [0.229, 0.224, 0.225]

def prep(path):
    im = Image.open(path).convert("RGB").resize((504, 504), Image.BILINEAR)
    x = np.asarray(im, dtype=np.float32) / 255.0
    return ((x - MEAN) / STD).transpose(2, 0, 1)

frames = np.stack([prep(p) for p in ["f0.jpg", "f1.jpg", "f2.jpg", "f3.jpg"]])[None]
frames = frames.astype(np.float32)                 # [1, 4, 3, 504, 504]

sess = ort.InferenceSession("model.onnx")
depth, conf, ext, K = sess.run(
    ["depth", "depth_conf", "extrinsics", "intrinsics"], {"images": frames})

# ext[0, v] is the [R|t] of view v relative to the window; unproject each
# view's depth with K[0, v] and transform by the inverse pose to fuse a
# single up-to-scale point cloud. Anchor scale with a metric depth model
# (see the scale note above) to land in meters.
```

## License

**Apache-2.0** — same as upstream [depth-anything/DA3-BASE](https://huggingface.co/depth-anything/DA3-BASE). (The DA3 any-view Large/Giant checkpoints are CC-BY-NC 4.0 and are **not** part of this export.) The ONNX-export step doesn't change licensing — same model, different serialization.
