---
license: apache-2.0
library_name: onnx
tags:
  - image-classification
  - imagenet
  - mobilenet
  - mobilenetv2
  - onnx
base_model: google/mobilenet_v2_1.0_224
pipeline_tag: image-classification
---

# MobileNetV2 — ImageNet Classification (ONNX)

ONNX checkpoint of Google's [MobileNetV2](https://arxiv.org/abs/1801.04381), trained on ImageNet-1K. Tiny (~14 MB), CPU-friendly, and a sensible baseline for whole-image classification, embedding extraction, or as a backbone for fine-tuning.

Not converted locally — this is the opset-12 checkpoint the [ONNX Model Zoo](https://github.com/onnx/models) publishes alongside the MobileNetV2 reference release.

Credit: Sandler, Howard, Zhu, Zhmoginov, Chen — Google AI Research.

## What this repo contains

```
mobilenetv2-12.onnx     # ~14 MB — opset 12, fp32, full ImageNet-1K classifier
imagenet-classes.json   # 1000-entry array: index = class id, value = human label
```

The `.onnx` and the `.json` are designed to travel together — the model emits a `[1, 1000]` logits tensor whose argmax index is meaningless without the matching synset → label table. Bundling both in one repo means a single download gives you a usable classifier.

## Input / output

| | Spec |
|---|---|
| Input name | `input` |
| Input shape | `[1, 3, 224, 224]` (NCHW) |
| Input dtype | float32 |
| Input color order | **RGB** |
| Preprocessing | Resize shortest side to 256, center-crop to 224×224, scale to `[0,1]`, then normalize: `mean=[0.485, 0.456, 0.406]`, `std=[0.229, 0.224, 0.225]` (ImageNet) |
| Output name | `output` |
| Output shape | `[1, 1000]` |
| Output | Raw logits — apply `softmax` for probabilities, `argmax` for top-1, top-k sort for top-k |

## How to use

```python
import json
import numpy as np
import onnxruntime as ort
from PIL import Image

# Load model + class table
sess = ort.InferenceSession("mobilenetv2-12.onnx")
with open("imagenet-classes.json") as f:
    classes = json.load(f)  # list of 1000 labels

# Preprocess
img = Image.open("photo.jpg").convert("RGB")
img = img.resize((256, 256), Image.BILINEAR)
left = (256 - 224) // 2
img = img.crop((left, left, left + 224, left + 224))

arr = np.asarray(img, dtype=np.float32) / 255.0
arr = (arr - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225]
arr = arr.transpose(2, 0, 1)[None, ...]  # NCHW

# Inference
logits = sess.run(None, {"input": arr.astype(np.float32)})[0]
probs = np.exp(logits[0] - logits[0].max())
probs /= probs.sum()

top5 = np.argsort(probs)[-5:][::-1]
for idx in top5:
    print(f"{classes[idx]}: {probs[idx]:.3f}")
```

## When to use this vs alternatives

- **MobileNetV2** (this repo): tiny, CPU-friendly, broad coverage from ImageNet-1K. Use when whole-image classification is enough and disk / latency matter.
- **CLIP**: when you want zero-shot or open-vocabulary classification (any English label, not just 1000 ImageNet classes), or cross-modal text↔image search.
- **YOLOX / RT-DETR**: when you need *where* an object is, not just *what's in the picture*.
- **Florence-2**: when you want a single model for classification + captioning + OCR + detection at the cost of much larger compute.

For embedding extraction (pooled feature vector instead of class probabilities), MobileNetV2's penultimate global-average-pool output is the conventional choice — a small modeling adjustment turns this into a 1280-dim image embedder.

## License

**Apache-2.0** — both the ONNX Model Zoo checkpoint and the original Google MobileNetV2 weights / reference code ship under Apache-2.0. `LICENSE` file included.
