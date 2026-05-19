---
license: mit
library_name: onnx
tags:
  - depth-estimation
  - metric-depth
  - zoedepth
  - dpt
  - onnx
base_model: Intel/zoedepth-nyu-kitti
pipeline_tag: depth-estimation
---

# ZoeDepth — Metric Monocular Depth (ONNX)

ONNX export of [Intel/zoedepth-nyu-kitti](https://huggingface.co/Intel/zoedepth-nyu-kitti) — Intel ISL's metric-depth follow-up to DPT-Large. Same DPT-Large backbone with calibrated metric-bin heads grafted on (one trained on NYU indoor depths, one on KITTI outdoor depths, combined via a domain-routing classifier). Outputs depth in **real-world meters**, not just relative ordering.

Re-exported from upstream PyTorch weights — Intel publishes only safetensors at the source repo. Provenance trail: Bhat et al. → Intel/zoedepth-nyu-kitti → `transformers.ZoeDepthForDepthEstimation` + thin wrapper → `torch.onnx.export` → these files. fp16 sibling produced from the fp32 trace via onnxconverter-common.

Toolchain: `torch 2.4.x` (CUDA 12.4), `torchvision 0.19` (matched ABI), `transformers 4.45.2`, `optimum[onnxruntime] 1.24.0`, `onnxconverter-common>=1.14`, opset 17, `do_constant_folding=True`. Full conversion script: [`scripts/export-zoedepth.ps1`](https://github.com/HeliosophLLC/Heliosoph/blob/main/scripts/export-zoedepth.ps1) in the Heliosoph repo (run once for fp32, again with `-Fp16` for the half-precision sibling).

Credit: Shariq Farooq Bhat, Reiner Birkl, Diana Wofk, Peter Wonka, Matthias Müller (Intel Intelligent Systems Lab). Paper: *"ZoeDepth: Zero-shot Transfer by Combining Relative and Metric Depth"*, 2023.

## What this repo contains

| File | Variant | Size | Use |
|---|---|---|---|
| `model.onnx` | fp32 | ~1.3 GB | Default — full precision, identical numerics to the PyTorch upstream. |
| `model_fp16.onnx` | fp16 | ~660 MB | Half precision — same architecture, ~½ the disk footprint. Identical output on CPU runtimes that upcast fp16→fp32; modest speedup on GPU/NPU with native fp16. |
| `config.json` | — | <3 KB | HuggingFace model config (preserved for re-instantiation if needed). |
| `preprocessor_config.json` | — | <1 KB | `ZoeDepthImageProcessor` settings — image-size targets, normalization stats. |

The fp32 `model.onnx` is **self-contained** — it came in at 1.37 GB, just under the 2 GB protobuf limit, so no external-data `.onnx.data` sidecar. Same for the fp16 variant at 693 MB.

## What "metric depth" means (vs the other depth models on Heliosoph)

| Repo | Output | When to use |
|---|---|---|
| **Heliosoph/zoedepth-nyu-kitti-onnx** (this repo) | **Metric** depth (meters) | 3D reconstruction at real-world scale, distance measurement, AR overlays, multi-image cloud fusion |
| [Heliosoph/dpt-large-onnx](https://huggingface.co/Heliosoph/dpt-large-onnx) | Relative depth | Visualization, single-image effects, "what's closer than what" without needing real units |
| [Heliosoph/midas-small-onnx](https://huggingface.co/Heliosoph/midas-small-onnx) | Relative depth | Edge / CPU / mobile — fast relative depth |
| [onnx-community/depth-anything-v2-small](https://huggingface.co/onnx-community/depth-anything-v2-small) | Relative depth (SOTA) | Modern default for relative depth |

Pick this repo specifically when you need **meters** — most monocular depth models give you a number per pixel that's only meaningful relative to other pixels in the same image; this one gives you a number that's calibrated against real-world distance.

## Input / output

| | Spec |
|---|---|
| Input name | `pixel_values` |
| Input shape | `[batch, 3, H, W]` (NCHW) |
| Input dtype | float32 (fp32 variant) or float16 (fp16 variant) |
| Constraint | H and W must each be divisible by 32 |
| Preprocessing | Use `ZoeDepthImageProcessor` from the included `preprocessor_config.json` — resize + normalize with the ZoeDepth-specific image stats |
| Output name | `predicted_depth` |
| Output shape | `[batch, H, W]` (single-channel; no extra channel dim) |
| Output unit | **meters** (real-world distance from camera to surface) |
| Dynamic axes | batch, height, width |

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image
from transformers import ZoeDepthImageProcessor

# Use the included preprocessor — it handles the 32-alignment + normalize.
proc = ZoeDepthImageProcessor.from_pretrained(".")
sess = ort.InferenceSession("model.onnx")          # or "model_fp16.onnx"

img = Image.open("photo.jpg").convert("RGB")
inputs = proc(images=img, return_tensors="np")     # NCHW float32, 32-aligned

depth_meters = sess.run(
    None,
    {"pixel_values": inputs["pixel_values"]},
)[0][0]                                            # [H, W], meters

# depth_meters[y, x] = distance from camera to that surface point, in meters
```

For the fp16 model, the input also needs to be float16 — cast `inputs["pixel_values"]` to `np.float16` before feeding it in.

## Why two variants

- **fp32** is the safe default — identical numerics to the upstream PyTorch reference, no surprises.
- **fp16** halves disk footprint and model-load memory. On GPU / NPU with native fp16 you also get a modest speedup; on CPU runtimes that upcast fp16→fp32 internally the speed is identical to fp32 but you save the memory. Depth output is essentially identical (the fp16 quantization noise is below the model's own per-pixel error).

If you're not sure: pick fp32 for accuracy-sensitive scientific work, fp16 for shipping / deployment / edge.

## License

**MIT** — same as upstream [Intel/zoedepth-nyu-kitti](https://huggingface.co/Intel/zoedepth-nyu-kitti). `LICENSE` file included. The ONNX-export step (and the fp16 numerical conversion) doesn't change licensing — same model, different serialization.
