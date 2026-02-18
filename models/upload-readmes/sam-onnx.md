---
license: apache-2.0
library_name: onnx
tags:
  - segmentation
  - sam
  - mobile-sam
  - onnx
base_model:
  - facebook/sam-vit-base
  - ChaoningZhang/MobileSAM
pipeline_tag: image-segmentation
---

# Segment Anything — Mobile Encoder + SAM Mask Decoder Bundle (ONNX)

ONNX bundle pairing the [MobileSAM](https://github.com/ChaoningZhang/MobileSAM) ViT-T image encoder with Meta's original SAM mask decoder. Together they form a complete Segment Anything pipeline at **~60 MB total** — orders of magnitude smaller than the original SAM ViT-H setup (~2.4 GB) and largely matched in accuracy on common segmentation tasks.

Not converted locally — these are the upstream-published ONNX checkpoints, bundled here for distribution stability.

Credit: Meta (SAM) and Kyungpook National University / Chaoning Zhang et al. (MobileSAM).

## What this repo contains

| File | Size | Role |
|---|---|---|
| `mobile_sam_image_encoder.onnx` | ~27 MB | ViT-T (Tiny) image encoder; replaces SAM's ViT-H |
| `sam_mask_decoder_single.onnx` | ~16 MB | Mask decoder, single-mask output mode |
| `sam_mask_decoder_multi.onnx` | ~16 MB | Mask decoder, multi-mask output mode (returns top-3 masks per prompt) |

The encoder runs once per image to produce a per-pixel embedding (1024-dim feature map). The mask decoder then runs cheaply per prompt (a click, box, or mask hint) against that embedding to produce segmentation masks. This split is what makes interactive segmentation tractable — only the decoder runs in the per-prompt loop.

## How to use

```python
import onnxruntime as ort
import numpy as np

# 1. Encode the image once
encoder = ort.InferenceSession("mobile_sam_image_encoder.onnx")
img_embedding = encoder.run(None, {"input_image": preprocessed_image})[0]
# img_embedding shape: [1, 256, 64, 64]

# 2. Decode masks per prompt (point click here)
decoder = ort.InferenceSession("sam_mask_decoder_single.onnx")
point_coords = np.array([[[500, 375]]], dtype=np.float32)  # one click
point_labels = np.array([[1]], dtype=np.float32)           # 1 = positive

masks, iou_predictions, low_res_masks = decoder.run(None, {
    "image_embeddings":  img_embedding,
    "point_coords":      point_coords,
    "point_labels":      point_labels,
    "mask_input":        np.zeros((1, 1, 256, 256), dtype=np.float32),
    "has_mask_input":    np.array([0], dtype=np.float32),
    "orig_im_size":      np.array([img_h, img_w], dtype=np.float32),
})
```

Use `sam_mask_decoder_single.onnx` for "give me the single best mask for this prompt" (typical interactive UX). Use `sam_mask_decoder_multi.onnx` for "give me top-3 candidate masks" — useful when the prompt is ambiguous (e.g., clicking on a person's shirt could yield mask of the shirt, the torso, or the whole person).

## Why MobileSAM over original SAM

- **38× smaller encoder**: ViT-T (~10M params) vs SAM ViT-H (~636M params).
- **~5× faster inference** on CPU; near-real-time even without GPU.
- **Quality is comparable** on common segmentation benchmarks — MobileSAM was distilled from SAM ViT-H specifically to preserve quality.
- Choose original SAM ViT-H/L only when maximum accuracy on edge cases matters more than speed/size.

## License

**Apache-2.0** — same as both upstream projects. `LICENSE` file included.
