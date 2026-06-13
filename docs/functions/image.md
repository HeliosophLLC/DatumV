---
title: Image Functions
category: image
---

# Image Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Compute Backend](../compute.md)

## Metadata

### image_channels

`image_channels(img)` → Int32

Colour-channel count (1=grayscale, 3=RGB, 4=RGBA). [Elidable accessor](../technical/planner-time-elision.md) — reads the inline channels byte stamped at ingest / bitmap construction; falls back to a SkiaSharp decode when the byte is unstamped.

### pixel_count

`pixel_count(img)` → Int32

Total pixel count, `width × height`. Lowered at plan time to `image_width(img) * image_height(img)` — both factors become struct reads via the inline-accessor elider, and CSE collapses any sibling width/height references in the same query.

```sql
SELECT * FROM images WHERE pixel_count(file_bytes) > 1000000
```

### dimensions

`dimensions(img, format)` → Int32[]

Dimensions in the requested axis order. Supported formats (case-insensitive): `'WH'` → `[w, h]`, `'WHC'` → `[w, h, c]`, `'HWC'` → `[h, w, c]`, `'CHW'` → `[c, h, w]`. When `format` is a string literal, the call lowers at plan time to `array(...)` of `image_width` / `image_height` / `image_channels` — each composing through the inline-accessor elider. Non-literal `format` (e.g. a column reference) takes the runtime path.

## Analysis

### image_brightness_mean

`image_brightness_mean(img)` → Float32

Mean BT.601 luminance across all pixels, in the range 0–255. Alpha is ignored.

### image_brightness_std

`image_brightness_std(img)` → Float32

Population standard deviation of BT.601 luminance across all pixels. Alpha is ignored.

### image_brightness_histogram

`image_brightness_histogram(img)` → Float32[]

256-bin BT.601 luminance histogram. Each element is the pixel count for that luminance bin.

### detect_blur

`detect_blur(img)` → Float32

Laplacian variance blur detector over BT.601 grayscale. Higher values indicate a sharper image.

### compression_artifact_score

`compression_artifact_score(img)` → Float32

JPEG blockiness score in the range 0–1. Measures 8×8 block boundary discontinuities relative to interior gradients.

## Pixel Statistics

### image_pixel_mean

`image_pixel_mean(img[, channels])` → Float32 or Float32[]

Mean pixel value. Without `channels`: overall mean over R, G, B (alpha excluded) as Float32. With `channels` as an `Int32[]` of channel indices (0=R, 1=G, 2=B, 3=A): per-channel means as Float32[] in the requested order.

### image_pixel_std

`image_pixel_std(img[, channels])` → Float32 or Float32[]

Population standard deviation of pixel values. Same signature as `image_pixel_mean`.

## Loading & Decode

### image_decode

`image_decode(bytes)` → Image
`image_decode(path)` → Image

Wraps a raw encoded-image source as a typed `Image` value. The first form takes a `UInt8[]` of encoded bytes (PNG / JPEG / WebP / BMP / TIFF — anything SkiaSharp recognises); the second takes a `String` filesystem path and reads the file. No pixel decoding happens — the bytes pass through verbatim with the kind flipped to `Image`, and width/height parsing stays lazy at the materialization boundary so `image_width()` and friends keep their fast inline-metadata path. Returns `NULL` when the argument is `NULL`.

`image_decode` is intentionally **permissive** on unrecognised input: bytes that don't match any known image signature still produce a non-NULL `Image` value, just with zero-sentinel inline metadata, so downstream `image_width()` / `image_height()` return `NULL` rather than throwing. For a validated conversion that throws at the call site with a hex-byte preview of the mismatch, use **`CAST(bytes AS Image)`** instead — see [Type System](../sql/type-system.md). The validated form accepts PNG / JPEG / WebP / GIF / BMP / TIFF magic signatures; `try_cast(bytes AS Image)` returns typed `NULL` on failure, and `can_cast(bytes, Image)` returns a boolean usable as a `WHERE`-clause filter to drop bad rows in a folder scan.

```sql
-- Lift an archive entry into the typed-Image surface (permissive)
SELECT image_decode(o.bytes) AS img
FROM open_archive(:source, '%.jpg') AS o

-- One-off file lookup
SELECT image_width(image_decode('C:/data/sample.png'))

-- Validated form — drops files whose bytes aren't a recognised image
SELECT path, CAST(bytes AS Image) AS img
FROM open_folder('C:/data/screenshots')
WHERE can_cast(bytes, Image) = true
```

### image_to_bytes

`image_to_bytes(img)` → UInt8[]

Extract raw RGBA pixel bytes as `UInt8[]` of length `H × W × 4` in row-major RGBA order. Decodes and color-converts to RGBA8888 when needed.

### video_frame_to_image

`video_frame_to_image(frame [, target_width [, target_height]])` → Image

Materialises a `VideoFrame` handle into an `Image` by routing it through the per-query video registry. Single-argument form decodes at the source video's native resolution; with `target_width`, resizes while preserving the source aspect ratio (height auto-computed); with both `target_width` and `target_height`, resizes to those exact dimensions. The resize fuses with the YUV→BGRA pixel conversion inside swscale — no extra per-frame copy.

```sql
-- Source resolution
SELECT video_frame_to_image(f.frame) AS img
FROM video_unnest_frames('clip.mp4') AS f

-- Aspect-preserved resize to 384px width (e.g. 1920×1080 → 384×216)
SELECT video_frame_to_image(f.frame, 384) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f

-- Exact 384×384 (typical depth-model input)
SELECT models.midas_small(video_frame_to_image(f.frame, 384, 384)) AS depth
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video, 0, 5, 50) AS f
```

Sequential access (frame N → N+1 → N+2) is fast (~3–5 ms/frame at 384px, ~11 ms at 1080p on a reference H.264 source); backward access seeks to the file head and decodes forward. Stay in `frame_index` order whenever possible.

### image_to_tensor_hwc

`image_to_tensor_hwc(img)` → Tensor

Decode to [H, W, 3] RGB float tensor (values 0–255). TensorFlow/NumPy layout.

```sql
SELECT image_to_tensor_hwc(resize(file_bytes, 224, 224)) AS pixels FROM images
```

### image_to_tensor_chw

`image_to_tensor_chw(img)` → Tensor

Decode to [3, H, W] RGB float tensor (values 0–255). PyTorch layout.

```sql
SELECT image_to_tensor_chw(resize(file_bytes, 224, 224)) AS pixels FROM images
```

## Transforms

### resize

`resize(img, w, h [, mode])` → Image

Resize image to target width × height. Width and height must be positive; float arguments truncate to integers. Optional `mode` selects the sampling filter (case-insensitive); defaults to `'bilinear'`.

| mode | filter | when to use |
|---|---|---|
| `'nearest'` | nearest-neighbour | pixel-art upscales, label/index maps where blending would invent new classes |
| `'bilinear'` *(default)* | 2×2 linear | general-purpose; matches the OpenCV / torchvision / TensorFlow defaults used by most CV preprocessing |
| `'trilinear'` | bilinear + linear mipmap | downscales of more than ~2×; closer to OpenCV `INTER_AREA` |
| `'mitchell'` | Mitchell–Netravali cubic (B=1/3, C=1/3) | photographic resampling when bilinear looks too soft; balanced sharpness/ringing |
| `'catmullrom'` | Catmull–Rom cubic (B=0, C=0.5) | sharper than Mitchell at the cost of more visible ringing on hard edges |

Lanczos is not available — SkiaSharp does not ship it; the cubic resamplers are the highest-quality option.

```sql
-- Default bilinear
SELECT resize(file_bytes, 224, 224) AS img, label FROM images

-- Nearest-neighbour for a segmentation mask
SELECT resize(mask, 512, 512, 'nearest') AS mask FROM segmentation
```

### image_crop

`image_crop(img, x, y, w, h)` → Image
`image_crop(img, rect Struct{x, y, w, h})` → Image
`image_crop(img, rects Array<Struct{x, y, w, h}>)` → Array<Image>

Crop rectangular region. Three call shapes: explicit pixel coordinates, a single rect struct (field names case-insensitive), or an array of rect structs (returns one cropped Image per rect in input order). All numeric kinds are accepted for the coordinate values; floats truncate to integer pixel offsets.

### image_draw_bounding_boxes

`image_draw_bounding_boxes(img, boxes Array<Struct>)` → Image
`image_draw_bounding_boxes(img, box Struct)` → Image

Overlay bounding-box rectangles (with optional labels) on an image. The element struct exposes `x`, `y`, `w`, `h` (numeric, in source-image pixel coordinates, top-left origin) plus optional `label` (String) and `score` (numeric, 0–1) fields. Two element shapes are accepted: flat `{x, y, w, h, ...}`, or a nested `{bbox: BoundingBox, label?, score?}` where the inner `BoundingBox` carries `x/y/w/h` — the named-type detection shapes (`ScoredDetection`, `FaceDetection`, `OcrLine`, `RegionScore`, ...) use the nested form. Field names are resolved via the per-query type registry, so any box-producing model that declares its `OutputFields` drops in directly. When `label` and/or `score` are present, a red label chip is drawn at the box's top-left corner showing `"<label> <score>"` (or whichever fields are non-null). A null or empty `boxes` argument returns the source image unchanged.

```sql
-- Array of detections from a YOLO-style model
SELECT image_draw_bounding_boxes(img, models.yolov8n(img)) AS annotated FROM photos

-- Single bounding box (e.g. one face from a face detector)
SELECT image_draw_bounding_boxes(img, models.scrfd(img)[0]) AS annotated FROM portraits
```

### grayscale

`grayscale(img)` → Image

Convert to grayscale (BT.601 luminance). Alpha is preserved.

### rotate

`rotate(img, degrees)` → Image

Rotate clockwise by arbitrary angle. Canvas expands for non-90° rotations; new corners are transparent.

### noise

`noise(img, val)` or `noise(img, type, val)` → Image

Add noise. Two-arg form defaults to `'gaussian'`. Three-arg type values: `'gaussian'` (val = stddev in 0–255 byte units, added per pixel to R/G/B via Box–Muller, alpha untouched) or `'salt_pepper'` (val = fraction of pixels flipped to pure black or pure white). Non-pure (each call draws fresh randomness; CSE will not collapse repeated calls).

```sql
SELECT noise(grayscale(file_bytes), 'gaussian', 5) AS augmented FROM training_images
```

### blur

`blur(img, radius)` → Image

Gaussian blur with the given sigma radius (must be non-negative).

### brighten

`brighten(img, intensity)` → Image

Increase brightness by adding intensity (0–255 byte units) to each RGB channel. Channel values clamp at 255. Alpha is preserved.

### darken

`darken(img, intensity)` → Image

Decrease brightness by subtracting intensity (0–255 byte units) from each RGB channel. Channel values clamp at 0. Alpha is preserved.

### sobel

`sobel(img)` → Image

Sobel edge detection producing a grayscale edge-magnitude image. The 1-pixel border is opaque black.

### resize_and_crop

`image_resize_and_crop(img, w, h, gravity)` → Image

Scale the image to fill the target rectangle (aspect preserved — the larger of the X/Y scale factors wins), then crop the excess from the gravity anchor. Gravity (case-insensitive): `'center'`, `'top'`, `'bottom'`, `'left'`, `'right'`.

### affine_transform

`affine_transform(img, angle, scale_x, scale_y, shear_x, shear_y)` → Image

Decomposed affine transform — rotation (degrees), per-axis scale, X/Y shear — anchored at the image centre. Output canvas matches the input; freed pixels are transparent. For directional shear (the MNIST augmentation), pass `angle=0`, `scale_x=scale_y=1`, and small `shear_x` or `shear_y` (e.g. ±0.2).

### elastic_deform

`elastic_deform(img, alpha, sigma)` → Image

Simard, Steinkraus & Platt (2003) elastic deformation — the canonical MNIST augmentation. Generates a per-pixel random displacement field in `[-1, 1]`, smooths each axis with a separable Gaussian of width `sigma`, scales by `alpha`, then resamples via bilinear interpolation. `sigma` must be positive. Non-pure.

### perspective_warp

`perspective_warp(img, intensity)` or `perspective_warp(img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y)` → Image

Perspective distortion. Intensity form: random per-corner displacement bounded by `intensity` (fraction of image dimensions). Explicit form: normalised 0–1 destination coordinates for each corner (tl/tr/bl/br). Solves the 8-equation projective system via Gaussian elimination with partial pivoting; degenerate corner configurations raise an error. Non-pure (covers the random variant).

### directional_warp

`directional_warp(img, dx, dy, intensity)` → Image

Linear directional shear along the 2D vector `(dx, dy)`. The direction is normalised internally (its length is ignored — only its angle matters); `intensity` is the absolute pixel displacement applied at the perpendicularly-furthest edge of the image. Pixels on the centre line orthogonal to `(dx, dy)` don't move; opposite edges displace in opposite directions along the direction vector. Bilinear sampling, edge clamping. Pure.

Designed for handwriting-style synthetic data augmentation — e.g. `directional_warp(img, 1, 0, 2)` on a 28×28 MNIST digit gives a 2-pixel horizontal lean (italic-like). Image y-axis is down (SkiaSharp convention).

```sql
-- Generate one shear-augmented variant of each MNIST digit per epoch:
SELECT label, directional_warp(image, 1, 0, random(-3, 3)) AS augmented
FROM mnist_train
```

## Hashing

### perceptual_hash

`perceptual_hash(img)` → Float32[]

Difference hash (dHash): resize the image to 9×8 BT.601 grayscale, then for each row compare horizontally adjacent pixel pairs (8 pairs × 8 rows = 64 bits) — emitting `1.0` when the left pixel is brighter than the right, `0.0` otherwise. Pair with `hamming_distance()` for similarity comparison.

## Fused Pipelines

When image transforms are nested -- e.g. `resize(grayscale(crop(img, ...)), 224, 224)` -- the engine automatically fuses the decode/encode cycle. Without fusion each function would decode the image from bytes, apply its transform, and re-encode to bytes, only for the next function to decode those bytes again. With fusion, only the first function in the chain decodes and only the final consumer encodes, eliminating N-1 redundant decode/encode cycles.

This is implemented via `ImageHandle`, a smart wrapper that carries either encoded bytes, a decoded `SKBitmap`, or both. Key properties:

- **Lazy decode** -- `ImageHandle` created from encoded bytes defers decoding until a transform actually needs the bitmap.
- **Lazy encode** -- `ImageHandle` created from a bitmap defers encoding until the bytes are needed (output writer, statistics, etc.).
- **Format propagation** -- Each handle tracks the requested output format. Functions without a format argument inherit the input's format; functions with an explicit format argument override it.
- **Deterministic disposal** -- The expression evaluator disposes intermediate `ImageHandle` bitmaps as soon as they are consumed, releasing native SkiaSharp memory promptly rather than waiting for GC finalization.

## See Also

- [Vector & Tensor Functions](vector.md) -- operations on tensors produced by image_to_tensor_hwc/chw
- [Functions Reference](string.md) -- complete function listing across all categories
