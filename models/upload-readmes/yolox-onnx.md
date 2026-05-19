---
license: apache-2.0
library_name: onnx
tags:
  - object-detection
  - yolox
  - coco
  - onnx
base_model: Megvii-BaseDetection/YOLOX
pipeline_tag: object-detection
---

# YOLOX — ONNX (full size ladder)

ONNX bundle of [Megvii-BaseDetection/YOLOX](https://github.com/Megvii-BaseDetection/YOLOX) covering all seven published size variants in a single repo. Apache-2.0 licensed alternative to Ultralytics YOLOv8 (which is AGPL-3.0).

Not converted locally — these are the ONNX checkpoints Megvii publishes alongside YOLOX itself.

Credit: Megvii.

## What this repo contains

| File | Params | Size | mAP (COCO val) | Inference cost |
|---|---|---|---|---|
| `yolox_nano.onnx` | 0.9M | 3.5 MB | 25.8 | smallest, mobile/edge |
| `yolox_tiny.onnx` | 5.1M | 20 MB | 32.8 | mobile / fast CPU |
| `yolox_s.onnx` | 9.0M | 35 MB | 40.5 | **recommended general default** |
| `yolox_m.onnx` | 25.3M | 97 MB | 46.9 | balanced |
| `yolox_l.onnx` | 54.2M | 207 MB | 49.7 | quality > speed |
| `yolox_x.onnx` | 99.1M | 378 MB | 51.1 | maximum accuracy |
| `yolox_darknet.onnx` | 63.7M | 244 MB | 47.7 | Darknet backbone variant, mostly historical |

All variants share the same 80-class COCO label set and the same input/output tensor signature — you can swap sizes without rewriting inference code.

## How to use

```python
import onnxruntime as ort
import numpy as np

sess = ort.InferenceSession("yolox_s.onnx")

# Input: [1, 3, 640, 640] float32, RGB, NOT normalized (YOLOX expects raw [0,255]).
# Letterbox-pad images to 640x640 maintaining aspect ratio before feeding.
outputs = sess.run(None, {"images": img_tensor})[0]

# Outputs: [1, 8400, 85]
#   8400 = anchor predictions (P3/P4/P5 grids combined)
#   85   = (cx, cy, w, h, obj_conf, *80 class probs)
# Decode by multiplying obj_conf × max(class_probs), apply NMS.
```

Reference postprocessing in Python and C++: [Megvii's YOLOX/demo/ONNXRuntime](https://github.com/Megvii-BaseDetection/YOLOX/tree/main/demo/ONNXRuntime).

## Which one should I use?

- **CPU / mobile / NPU:** `yolox_nano` or `yolox_tiny`
- **General-purpose:** `yolox_s` (the recommended default)
- **GPU, accuracy matters:** `yolox_l` or `yolox_x`
- **`yolox_darknet`** is included for reproducibility but the CSPDarknet-backbone variants above generally supersede it.

## Why not YOLOv8?

YOLOv8 (Ultralytics) is **AGPL-3.0** — using it in commercial or closed-source software triggers strong copyleft obligations. YOLOX is **Apache-2.0** and roughly comparable on accuracy. The Heliosoph catalog uses YOLOX as the default detector for this reason.

## License

**Apache-2.0** — same as upstream. `LICENSE` file included.
