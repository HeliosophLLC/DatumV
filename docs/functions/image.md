---
title: Image Functions
category: image
---

# Image Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Compute Backend](../compute.md)

> **Resolution-aware costs:** Image analysis (Tier 4) and transform (Tier 5) functions incur a supplemental cost proportional to input resolution: `⌊width × height / 100,000⌋` additional QU per invocation. A 224×224 image adds 0 QU; a 1920×1080 image adds 20 QU; a 4K image adds 82 QU.

## Metadata

### width

`width(img)` → Float32 | QU: 1

Image width in pixels (header-only, no full decode).

```sql
SELECT width(file_bytes) AS w FROM images
```

### height

`height(img)` → Float32 | QU: 1

Image height in pixels (header-only).

```sql
SELECT height(file_bytes) AS h FROM images
```

### channels

`channels(img)` → Float32 | QU: 1

Number of color channels (header-only).

### pixel_count

`pixel_count(img)` → Float32 | QU: 1

Total pixel count (width × height, header-only).

```sql
SELECT * FROM images WHERE pixel_count(file_bytes) > 1000000
```

### dimensions

`dimensions(img, format)` → Vector | QU: 1

Dimension vector in specified format: `'HWC'`, `'CHW'`, `'WH'`, or `'WHC'`.

## Analysis

### image_brightness_mean

`image_brightness_mean(img)` → Float32 | QU: 10 + ⌊px/100K⌋

Mean brightness (BT.601 luminance) across all pixels, in the range 0--255.

### image_brightness_std

`image_brightness_std(img)` → Float32 | QU: 10 + ⌊px/100K⌋

Standard deviation of brightness across all pixels.

### image_brightness_histogram

`image_brightness_histogram(img)` → Vector | QU: 10 + ⌊px/100K⌋

256-bin brightness histogram. Each element is the pixel count for that luminance bin.

### detect_blur

`detect_blur(img)` → Float32 | QU: 10 + ⌊px/100K⌋

Laplacian variance blur detector. Higher values indicate a sharper image.

### compression_artifact_score

`compression_artifact_score(img)` → Float32 | QU: 10 + ⌊px/100K⌋

JPEG blockiness score in the range 0--1. Measures 8×8 block boundary discontinuities.

## Pixel Statistics

### image_pixel_mean

`image_pixel_mean(img[, channels])` → Float32 or Vector | QU: 10 + ⌊px/100K⌋

Mean pixel value. Without channels: overall mean as Float32. With channels vector (0=R, 1=G, 2=B, 3=A): per-channel means as Vector.

### image_pixel_std

`image_pixel_std(img[, channels])` → Float32 or Vector | QU: 10 + ⌊px/100K⌋

Standard deviation of pixel values. Same signature as `image_pixel_mean`.

## Loading & Decode

### load_image

`load_image(bytes)` → Image | QU: 1

Load encoded bytes (UInt8Array from ZIP/binary column) as an Image for use with transform and analysis functions. No decode -- wraps the bytes as an opaque Image value for the fused pipeline.

### image_to_bytes

`image_to_bytes(img)` → UInt8Array | QU: 50 + ⌊px/100K⌋

Extract raw RGBA pixel bytes as UInt8Array (length H×W×4).

### image_to_tensor_hwc

`image_to_tensor_hwc(img)` → Tensor | QU: 50 + ⌊px/100K⌋

Decode to [H, W, 3] RGB float tensor (values 0--255). TensorFlow/NumPy layout.

```sql
SELECT image_to_tensor_hwc(resize(file_bytes, 224, 224)) AS pixels FROM images
```

### image_to_tensor_chw

`image_to_tensor_chw(img)` → Tensor | QU: 50 + ⌊px/100K⌋

Decode to [3, H, W] RGB float tensor (values 0--255). PyTorch layout.

```sql
SELECT image_to_tensor_chw(resize(file_bytes, 224, 224)) AS pixels FROM images
```

## Transforms

All transform functions accept an optional trailing `fmt` argument (`'jpeg'`, `'png'`, `'webp'`) to control output encoding. Default preserves the original format.

### resize

`resize(img, w, h[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Resize image to target width/height.

```sql
SELECT resize(file_bytes, 224, 224) AS img, label FROM images
```

### crop

`crop(img, x, y, w, h[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Crop rectangular region.

### grayscale

`grayscale(img[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Convert to grayscale (BT.601 luminance).

### rotate

`rotate(img, degrees[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Rotate by arbitrary angle. Canvas expands for non-90° rotations.

### noise

`noise(img, type, val[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Add noise. Type: `'gaussian'` (val=stddev) or `'salt_pepper'` (val=ratio).

```sql
SELECT noise(grayscale(file_bytes), 'gaussian', 5) AS augmented FROM training_images
```

### blur

`blur(img, radius[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Gaussian blur with the given sigma radius.

### brighten

`brighten(img, intensity[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Increase brightness by adding intensity to RGB channels.

### darken

`darken(img, intensity[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Decrease brightness by subtracting intensity from RGB channels.

### sobel

`sobel(img[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Sobel edge detection producing a grayscale edge magnitude image.

### resize_and_crop

`resize_and_crop(img, w, h, gravity[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Resize to fill then crop to exact dimensions. Gravity: `'center'`, `'top'`, `'bottom'`, `'left'`, `'right'`.

### affine_transform

`affine_transform(img, angle, sx, sy, shx, shy[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Affine transformation with rotation (degrees), scale, and shear parameters.

### elastic_deform

`elastic_deform(img, alpha, sigma[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Elastic deformation (Simard et al.). Alpha = displacement intensity, sigma = smoothing.

### perspective_warp

`perspective_warp(img, intensity[, fmt])` or `perspective_warp(img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y[, fmt])` → Image | QU: 50 + ⌊px/100K⌋

Perspective distortion. Intensity mode: random warp. Explicit mode: normalized corner coordinates.

## Hashing

### perceptual_hash

`perceptual_hash(img)` → Vector | QU: 10 + ⌊px/100K⌋

Difference hash (dHash) producing a 64-element Vector of 0/1 bits. Use with `hamming_distance()` for similarity.

## Fused Pipelines

When image transforms are nested -- e.g. `resize(grayscale(crop(img, ...)), 224, 224)` -- the engine automatically fuses the decode/encode cycle. Without fusion each function would decode the image from bytes, apply its transform, and re-encode to bytes, only for the next function to decode those bytes again. With fusion, only the first function in the chain decodes and only the final consumer encodes, eliminating N-1 redundant decode/encode cycles.

This is implemented via `ImageHandle`, a smart wrapper that carries either encoded bytes, a decoded `SKBitmap`, or both. Key properties:

- **Lazy decode** -- `ImageHandle` created from encoded bytes defers decoding until a transform actually needs the bitmap.
- **Lazy encode** -- `ImageHandle` created from a bitmap defers encoding until the bytes are needed (output writer, statistics, etc.).
- **Format propagation** -- Each handle tracks the requested output format. Functions without a format argument inherit the input's format; functions with an explicit format argument override it.
- **Deterministic disposal** -- The expression evaluator disposes intermediate `ImageHandle` bitmaps as soon as they are consumed, releasing native SkiaSharp memory promptly rather than waiting for GC finalization.

## See Also

- [Vector & Tensor Functions](vector.md) -- operations on tensors produced by image_to_tensor_hwc/chw
- [Compute Backend -- Resource Governance](../compute.md#resource-governance) -- QU cost tracking and resolution-aware budgets
- [Functions Reference](string.md) -- complete function listing across all categories
