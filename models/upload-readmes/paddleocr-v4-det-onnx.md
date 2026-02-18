---
license: apache-2.0
library_name: onnx
tags:
  - ocr
  - text-detection
  - paddleocr
  - onnx
base_model: PaddlePaddle/PaddleOCR
pipeline_tag: object-detection
---

# PaddleOCR PP-OCRv4 — Text Region Detection (ONNX)

ONNX export of PaddlePaddle's [PP-OCRv4](https://github.com/PaddlePaddle/PaddleOCR) text-region detector. Despite the `ch_` prefix in the filename, the detector itself is largely language-agnostic — script-specific weights live in the recognizer, not the detector.

Re-hosted under Heliosoph for distribution stability — PaddleOCR's GitHub is the authoritative source.

Credit: Baidu / PaddlePaddle OCR team.

## What this repo contains

```
ch_PP-OCRv4_det.onnx    # ~5 MB
```

The detection stage of a two-stage OCR pipeline. Pair with a recognizer (TrOCR, PP-OCRv4 recognizer, or use Florence-2's `<OCR_WITH_REGION>` mode as an end-to-end alternative).

## Input/output

| | Spec |
|---|---|
| Input name | `x` |
| Input shape | dynamic — typically `[1, 3, H, W]` where H, W are multiples of 32 |
| Input dtype | float32 |
| Input color order | **RGB** |
| Preprocessing | Resize to nearest multiple of 32 (with max dim cap, e.g. 960), normalize by `mean=[0.485, 0.456, 0.406]`, `std=[0.229, 0.224, 0.225]` |
| Output | Probability map `[1, 1, H, W]` |
| Postprocessing | Threshold at ~0.3, find connected components, fit minimum-area rectangle or quadrilateral per component |

The output is a per-pixel "is this part of a text region?" probability. The postprocessing turns the heatmap into polygons.

## How to use

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

sess = ort.InferenceSession("ch_PP-OCRv4_det.onnx")

# Resize maintaining aspect ratio, snap to multiples of 32
img = Image.open("document.jpg").convert("RGB")
img_resized = resize_to_multiple_of_32(img, max_side=960)  # custom helper

arr = (np.asarray(img_resized, dtype=np.float32) / 255.0 - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225]
arr = arr.transpose(2, 0, 1)[None, ...]  # 1x3xHxW

prob_map = sess.run(None, {"x": arr})[0][0, 0]  # HxW probability map

# Threshold and extract polygons
binary = (prob_map > 0.3).astype(np.uint8)
polygons = extract_polygons_from_binary_mask(binary)  # custom — see PaddleOCR DBNet postprocess
```

Reference postprocessing: [PaddleOCR's DBNet decoder](https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/postprocess/db_postprocess.py).

## When to use this vs Florence-2 OCR

- **PaddleOCR detector + recognizer**: faster, tinier, language-specific recognizer choice, classical two-stage pipeline
- **Florence-2 `<OCR_WITH_REGION>`**: single model, broader language coverage, larger compute footprint
- **TrOCR**: best for printed Latin text; not language-agnostic

If you're building a document-OCR pipeline today, PP-OCRv4 detection + TrOCR recognition is hard to beat for English. For mixed languages, Florence-2.

## License

**Apache-2.0** — same as upstream. `LICENSE` file included.
