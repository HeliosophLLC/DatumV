---
license: apache-2.0
library_name: onnx
tags:
  - depth-estimation
  - metric-depth
  - depth-anything-3
  - dinov2
  - onnx
base_model: depth-anything/DA3METRIC-LARGE
pipeline_tag: depth-estimation
---

# Depth Anything 3 Metric Large — Metric Monocular Depth (ONNX)

ONNX export of [depth-anything/DA3METRIC-LARGE](https://huggingface.co/depth-anything/DA3METRIC-LARGE) — the metric-depth monocular variant of ByteDance's Depth Anything 3 family. DINOv2 ViT-L backbone with a single-channel DPT head, plus a sky-segmentation head. Outputs **canonical depth** that converts to real-world meters with one multiply by your camera's focal length (details below).

This is the largest **Apache-2.0** model in the DA3 family — the any-view DA3-LARGE / DA3-GIANT models are CC-BY-NC 4.0, but ByteDance licenses the monocular metric Large variant permissively.

Re-exported from upstream safetensors — the source repo ships PyTorch weights only, loaded through the official `depth-anything-3` package (not transformers). Provenance trail: Lin et al. → depth-anything/DA3METRIC-LARGE safetensors → `depth_anything_3.api.DepthAnything3` + thin wrapper → `torch.onnx.export` → these files. fp16 sibling produced from the fp32 trace via onnxconverter-common (with a Cast-node type realignment the converter misses on this graph).

Toolchain: `torch 2.4.x` (CUDA 12.4), `depth-anything-3 0.1.1`, opset 17, legacy TorchScript exporter, `do_constant_folding=True`, upstream's bf16 autocast disabled for a clean fp32 trace. Full conversion script: [`scripts/export-da3metric.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-da3metric.ps1) in the Heliosoph repo (run once for fp32, again with `-Fp16` for the half-precision sibling). Export validation: fp32 ONNX matches PyTorch to 4.3e-05 max relative error; fp16 matches fp32 to 1.5e-03 (depth) / 5.1e-03 (sky); batch>1 verified item-wise against batch=1.

Credit: Haotong Lin, Sili Chen, Jun Hao Liew, Donny Y. Chen, Zhenyu Li, Guang Shi, Jiashi Feng, Bingyi Kang (ByteDance Seed). Paper: *"Depth Anything 3: Recovering the Visual Space from Any Views"*, 2025.

## What this repo contains

| File | Variant | Size | Use |
|---|---|---|---|
| `model.onnx` | fp32 | ~1.34 GB | Default — full precision, matches the PyTorch upstream to ~1e-5. |
| `model_fp16.onnx` | fp16 | ~670 MB | Half precision — same architecture, ~½ the disk footprint. **I/O stays fp32** (`keep_io_types`), so it's a drop-in swap: same input dtype, same output dtype. |
| `config.json` | — | <1 KB | Upstream DA3 model config (preserved for provenance / re-instantiation via the `depth-anything-3` package). |

Both files are **self-contained** — the fp32 trace came in at 1.34 GB, under the 2 GB protobuf limit, so no external-data `.onnx_data` sidecar.

## Canonical depth → meters

The network predicts **canonical depth**: depth as it would appear through a reference camera with a 300-pixel focal length. Converting to meters is one multiply (per the upstream DA3 FAQ):

```
metric_depth_m = depth_output * focal_px / 300
```

where `focal_px` is the focal length **in pixels of the image as fed to the network** (i.e. after resizing to the model's 504×504 input). If you know the horizontal field of view instead:

```
focal_px = 0.5 * 504 / tan(hfov / 2)
```

If you don't know the focal length, the raw output is still a high-quality depth map — you just can't claim real-world units for it.

## What "metric depth" means (vs the other depth models on Heliosoph)

| Repo | Output | When to use |
|---|---|---|
| **Heliosoph/da3metric-large-onnx** (this repo) | **Metric** depth (meters, given focal length) + sky mask | Best-quality metric depth: 3D reconstruction at real-world scale, distance measurement, point-cloud fusion — when you know (or can estimate) the camera's focal length |
| [Heliosoph/zoedepth-nyu-kitti-onnx](https://huggingface.co/Heliosoph/zoedepth-nyu-kitti-onnx) | **Metric** depth (meters, no focal needed) | Metric depth when the focal length is unknown — ZoeDepth bakes calibration in, at older-generation quality |
| [onnx-community/depth-anything-v2-small](https://huggingface.co/onnx-community/depth-anything-v2-small) | Relative depth | Fast modern default for relative depth |
| [Heliosoph/dpt-large-onnx](https://huggingface.co/Heliosoph/dpt-large-onnx) | Relative depth | Visualization, "what's closer than what" without real units |

## Input / output

| | Spec |
|---|---|
| Input name | `image` |
| Input shape | `[batch, 3, 504, 504]` (NCHW) |
| Input dtype | float32 (both variants — fp16 model keeps fp32 I/O) |
| Preprocessing | RGB, scale to [0,1], normalize with ImageNet stats (mean `[0.485, 0.456, 0.406]`, std `[0.229, 0.224, 0.225]`) |
| Output `depth` | `[batch, 1, 504, 504]` — canonical depth (× `focal_px / 300` for meters) |
| Output `sky` | `[batch, 1, 504, 504]` — sky score; `sky >= 0.5` is the upstream sky-mask threshold (depth is unreliable on sky pixels — mask them before reconstruction) |
| Dynamic axes | **batch only** |

**Resolution is fixed at 504×504.** The ViT position-embedding interpolation bakes the patch-token count into the traced graph, so a DA3 ONNX export is only valid at its trace resolution — this is inherent to the export, not a choice. Resize inputs to 504×504 (and resize the outputs back if you need the source resolution). For a different fixed resolution (any multiple of 14), re-run the conversion script with `-Height`/`-Width`.

## How to use

```python
import numpy as np
import onnxruntime as ort
from PIL import Image

sess = ort.InferenceSession("model.onnx")          # or "model_fp16.onnx" — same I/O

img = Image.open("photo.jpg").convert("RGB")
orig_w, orig_h = img.size
x = np.asarray(img.resize((504, 504), Image.BILINEAR), dtype=np.float32) / 255.0
x = (x - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225]
x = x.transpose(2, 0, 1)[None].astype(np.float32)  # [1, 3, 504, 504]

depth, sky = sess.run(["depth", "sky"], {"image": x})
depth, sky = depth[0, 0], sky[0, 0]                # [504, 504] each

# Canonical → meters. Example: 60° horizontal FOV.
hfov = np.deg2rad(60)
focal_px = 0.5 * 504 / np.tan(hfov / 2)            # focal at the 504-wide network input
depth_meters = depth * focal_px / 300

sky_mask = sky >= 0.5                              # depth is meaningless on sky pixels
depth_meters[sky_mask] = np.inf
```

## Why two variants

- **fp32** is the safe default — matches the upstream PyTorch reference to ~1e-5.
- **fp16** halves disk footprint and model-load memory, with fp32 kept at the I/O boundary so no caller changes are needed. Depth differs from fp32 by at most ~0.15% — below the model's own per-pixel error. On GPU / NPU with native fp16 you also get a modest speedup; CPU runtimes upcast internally and run at fp32 speed.

## License

**Apache-2.0** — same as upstream [depth-anything/DA3METRIC-LARGE](https://huggingface.co/depth-anything/DA3METRIC-LARGE). Note this applies to the metric monocular variant specifically: the DA3 any-view Large/Giant checkpoints are CC-BY-NC 4.0 and are **not** part of this export. The ONNX-export step (and the fp16 numerical conversion) doesn't change licensing — same model, different serialization.
