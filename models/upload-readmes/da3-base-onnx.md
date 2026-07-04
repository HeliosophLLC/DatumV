---
license: apache-2.0
library_name: onnx
tags:
  - depth-estimation
  - depth-anything-3
  - dinov2
  - camera-intrinsics
  - onnx
base_model: depth-anything/DA3-BASE
pipeline_tag: depth-estimation
---

# Depth Anything 3 Base — Monocular Depth + Camera Intrinsics (ONNX)

Single-view ONNX export of [depth-anything/DA3-BASE](https://huggingface.co/depth-anything/DA3-BASE) — the Apache-2.0 any-view model of ByteDance's Depth Anything 3 family (DINOv2 ViT-B backbone, dual-DPT head, camera heads). This cut takes **one image per row** and emits depth, per-pixel confidence, and a per-image camera-intrinsics estimate.

Two things to know up front:

- **Depth is scale-ambiguous** (up-to-scale, not meters). For metric depth use [Heliosoph/da3metric-large-onnx](https://huggingface.co/Heliosoph/da3metric-large-onnx), or anchor this model's scale against it.
- **The pose output is not useful here.** DA3 predicts camera pose *relative to the other views in the same forward pass*; with a single view the `extrinsics` output is near-identity by construction. For real pose recovery use the multi-view sibling: [Heliosoph/da3-base-4view-onnx](https://huggingface.co/Heliosoph/da3-base-4view-onnx) (same weights, 4-frame window). The `intrinsics` output **is** meaningful for single images — a per-image focal-length estimate.

Re-exported from upstream safetensors via the official `depth-anything-3` package. Provenance trail: Lin et al. → depth-anything/DA3-BASE safetensors → `depth_anything_3.api.DepthAnything3` + thin wrapper → `torch.onnx.export` → these files. The any-view checkpoints need two exporter workarounds (baked into the script): `torch.cartesian_prod` (RoPE position grid, no ONNX symbolic) replaced with meshgrid+stack, and the TorchScript-compiled `affine_inverse` (whose `aten::mT` is unexportable) rebound to a `transpose(-2, -1)` equivalent. fp16 sibling via onnxconverter-common with a Cast-node type realignment.

Toolchain: `torch 2.4.x` (CUDA 12.4), `depth-anything-3 0.1.1`, opset 17, legacy TorchScript exporter, fp32 trace (upstream bf16 autocast disabled). Conversion script: [`scripts/export-da3metric.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-da3metric.ps1) in the Heliosoph repo. Export validation: fp32 ONNX matches PyTorch to 4.1e-07 max relative error across all four heads; fp16 matches fp32 to ≤4.9e-04; batch>1 verified item-wise against batch=1.

Credit: Haotong Lin, Sili Chen, Jun Hao Liew, Donny Y. Chen, Zhenyu Li, Guang Shi, Jiashi Feng, Bingyi Kang (ByteDance Seed). Paper: *"Depth Anything 3: Recovering the Visual Space from Any Views"*, 2025.

## What this repo contains

| File | Variant | Size | Use |
|---|---|---|---|
| `model.onnx` | fp32 | ~394 MB | Default — matches the PyTorch upstream to ~1e-6. |
| `model_fp16.onnx` | fp16 | ~198 MB | Half precision, **I/O stays fp32** (`keep_io_types`) — drop-in swap. |
| `config.json` | — | <1 KB | Upstream DA3 model config (provenance / re-instantiation). |

## Input / output

| | Spec |
|---|---|
| Input name | `image` |
| Input shape | `[batch, 3, 504, 504]` (NCHW) |
| Input dtype | float32 (both variants) |
| Preprocessing | RGB, scale to [0,1], ImageNet mean/std (`[0.485, 0.456, 0.406]` / `[0.229, 0.224, 0.225]`) |
| Output `depth` | `[batch, 1, 504, 504]` — up-to-scale depth, bigger = farther |
| Output `depth_conf` | `[batch, 1, 504, 504]` — per-pixel confidence |
| Output `extrinsics` | `[batch, 1, 3, 4]` — `[R \| t]`; **near-identity for single view** (see above) |
| Output `intrinsics` | `[batch, 1, 3, 3]` — estimated K **at the 504×504 input grid** (principal point at 252, 252); rescale to source dims via `K' = diag(W/504, H/504, 1) · K` |
| Dynamic axes | batch only |

**Resolution is fixed at 504×504** — the ViT position-embedding interpolation bakes the patch count into the trace (inherent to DA3 ONNX exports, not a choice). Resize inputs to match; re-run the conversion script with `-Height`/`-Width` for a different fixed resolution (multiples of 14).

## How to use

```python
import numpy as np
import onnxruntime as ort
from PIL import Image

sess = ort.InferenceSession("model.onnx")

img = Image.open("photo.jpg").convert("RGB")
x = np.asarray(img.resize((504, 504), Image.BILINEAR), dtype=np.float32) / 255.0
x = ((x - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225]).transpose(2, 0, 1)[None].astype(np.float32)

depth, conf, _ext, K = sess.run(["depth", "depth_conf", "extrinsics", "intrinsics"], {"image": x})
depth, conf, K = depth[0, 0], conf[0, 0], K[0, 0]

# K is at the 504x504 grid; rescale to the original image if needed:
w, h = img.size
K_src = np.diag([w / 504, h / 504, 1.0]) @ K
```

## License

**Apache-2.0** — same as upstream [depth-anything/DA3-BASE](https://huggingface.co/depth-anything/DA3-BASE). (The DA3 any-view Large/Giant checkpoints are CC-BY-NC 4.0 and are **not** part of this export; Base is the largest permissively-licensed any-view variant.) The ONNX-export step doesn't change licensing — same model, different serialization.
