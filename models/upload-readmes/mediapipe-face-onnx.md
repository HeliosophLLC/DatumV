---
license: apache-2.0
library_name: onnx
tags:
  - face-detection
  - face-landmark
  - mediapipe
  - blazeface
  - onnx
base_model: google/mediapipe
pipeline_tag: object-detection
---

# MediaPipe Face — Detection + 6-point Landmarks (ONNX)

Commercial-clean face pipeline bundling both precision variants of Google's MediaPipe Face Detection + Face Landmark models. Apache-2.0 throughout the entire chain: Google MediaPipe → zmurez/MediaPipePyTorch port → Qualcomm AI Hub ONNX export.

Two-stage pipeline:

1. **Face detector** (BlazeFace-derived) — finds face bounding boxes
2. **Face landmark detector** — for each detected face, returns 6 keypoints (left eye, right eye, nose tip, mouth, left eye tragion, right eye tragion)

Re-hosted under Heliosoph as a single bundled repo for convenience — float + int8 variants live in separate subfolders.

Credit: Google MediaPipe team (original models), Zak Murez (PyTorch port), Qualcomm AI Hub (ONNX export).

## What this repo contains

```
float/                              # fp32 — recommended default
  face_detector.onnx                # 78 KB graph
  face_detector.data                # 517 KB external weights
  face_landmark_detector.onnx       # 58 KB graph
  face_landmark_detector.data       # 2.4 MB external weights
  metadata.json
int8/                               # W8A8 — quantized, smaller/faster
  face_detector.onnx
  face_detector.data
  face_landmark_detector.onnx
  face_landmark_detector.data
  metadata.json
LICENSE
README.md
```

**Important: external weights pattern.** Each `.onnx` file is paired with a `.data` file holding the actual tensor weights. Both files must be in the same directory at load time — ONNX Runtime resolves the `.data` file by relative path from the `.onnx`.

## How to use

```python
import onnxruntime as ort
import numpy as np

# Stage 1: detect faces
detector = ort.InferenceSession("float/face_detector.onnx")
# Input: 128×128 RGB, normalized to [-1, 1]
detections = detector.run(None, {"image": preprocessed_128x128_image})

# Stage 2: landmark each detected face
landmarker = ort.InferenceSession("float/face_landmark_detector.onnx")
# Input: 192×192 RGB crop around each detected face
landmarks = landmarker.run(None, {"image": face_crop_192x192})
```

Reference preprocessing + decoding: [zmurez/MediaPipePyTorch](https://github.com/zmurez/MediaPipePyTorch) has the canonical Python implementation.

## float vs int8 — which to pick

| Variant | Size | Best for |
|---|---|---|
| `float/` (this default) | ~3 MB | GPU, max accuracy. Recommended general default. |
| `int8/` | ~1.5 MB | CPU, NPU (OpenVINO), mobile. Some accuracy loss on small/distant faces; near-identical on close portraits. |

Catalog entries: `mediapipe-face` (float) and `mediapipe-face-int8` (int8). Both reference this single repo with different `include` patterns.

## Why MediaPipe over alternatives

- **vs InsightFace SCRFD** — SCRFD's released weights are non-commercial-research-only (WIDER FACE dataset terms). MediaPipe was trained by Google on commercial-friendly data and released under permissive terms.
- **vs YuNet** — YuNet is technically also encumbered by WIDER FACE; upstream just doesn't surface that. MediaPipe is unambiguous.
- **vs YOLOv8-Face** — Ultralytics AGPL-3.0. MediaPipe is Apache-2.0.

If you need higher accuracy on small faces in dense scenes (crowd photos, surveillance angles), MediaPipe will underperform RetinaFace-class detectors. For the common case (close portraits, video conferencing, photo tagging), MediaPipe is the right default.

## License

**Apache-2.0** — same as upstream (Google MediaPipe). `LICENSE` file included; chain of attribution is documented above.
