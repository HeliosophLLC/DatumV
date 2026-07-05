---
title: Image Functions
category: image
---

# Image Functions

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

## ONNX Tensor Conversion

The functions in this section are the bridge between typed `Image`
values and the flat `Float32[]` tensors that ONNX model bodies
consume. They appear almost exclusively inside
[`CREATE MODEL`](../sql/create-model.md) bodies as the first or last
step of an inference pipeline.

### image_to_tensor_chw

`image_to_tensor_chw(img, target_size)` → `Float32[]`
`image_to_tensor_chw(img, target_size, mean, std)` → `Float32[]`

Stretch-resize an `Image` to `target_size = [height, width]` and
flatten its RGB pixel data into a normalised NCHW Float32 tensor —
the canonical preprocessing shape for most ONNX vision models
(ResNet, MobileNet, ViT, YOLO backbones, CLIP image encoders).

Output length is `3 × height × width`, channel-major:
`dest[c*H*W + y*W + x]`. Per-element formula:
`(pixel_byte / 255.0 - mean[c]) / std[c]`. The 2-arg shortcut omits
mean/std and produces raw `pixel/255`.

`target_size` is `[height, width]`, matching the NCHW tensor
convention `[batch, channels, height, width]`. Resize filter is
bilinear (matches the OpenCV / torchvision / TensorFlow defaults).

```sql
-- ImageNet-normalised (most CV backbones):
DECLARE tensor Float32[] = image_to_tensor_chw(
    img, [224::Int32, 224::Int32], imagenet_mean(), imagenet_std());

-- SigLIP / ViT-half-normalisation (Moondream2 vision encoder):
DECLARE tensor Float32[] = image_to_tensor_chw(
    img, [378::Int32, 378::Int32],
    [0.5::Float32, 0.5::Float32, 0.5::Float32],
    [0.5::Float32, 0.5::Float32, 0.5::Float32]);

-- Raw pixel/255 (model applies its own normalisation in-graph):
DECLARE tensor Float32[] = image_to_tensor_chw(img, [512::Int32, 512::Int32]);
```

This is a **stretch** resize — the input is squashed to fit the
target dimensions, no aspect preservation. For detectors and other
aspect-sensitive models, see
[`image_letterbox_tensor_chw`](#image_letterbox_tensor_chw) or
[`yolox_preprocess`](inference.md#yolox_preprocess).

### image_to_tensor_hwc

`image_to_tensor_hwc(img, target_size)` → `Float32[]`
`image_to_tensor_hwc(img, target_size, mean, std)` → `Float32[]`

NHWC sibling of `image_to_tensor_chw`. Same stretch resize and
per-channel normalisation; the only difference is the output index
layout — pixels are interleaved as `[R, G, B]` triples
(`dest[(y*W + x)*3 + c]`) instead of channel-major planes. Pair with
ONNX models whose input shape is `[B, H, W, C]` (typically
TF-exported graphs).

### image_to_tensor_chw_bgr

`image_to_tensor_chw_bgr(img, target_size)` → `Float32[]`
`image_to_tensor_chw_bgr(img, target_size, mean, std)` → `Float32[]`

BGR sibling of `image_to_tensor_chw`. Same stretch resize and
normalisation, but emits the tensor in BGR channel order. Used by
legacy detectors and depth estimators trained against cv2-loaded BGR
images (MiDaS-small v2.1, some YOLO variants).

The `mean` and `std` arrays are indexed in *output* channel order —
i.e. BGR. Pass `[meanB, meanG, meanR]` if the upstream Python
reference cites the values in BGR order.

### image_letterbox_tensor_chw

`image_letterbox_tensor_chw(img, target_size, mean, std, pad_fill)` → `Float32[]`

Aspect-preserving letterbox resize plus per-channel normalisation
into a square NCHW Float32 tensor — the canonical preprocessing for
object detectors (YOLOX, SCRFD, RetinaFace) and any model whose
accuracy depends on not stretching the input.

- `target_size` — single `Int32`. The output canvas is
  `target_size × target_size`; the image is aspect-scaled to fit
  inside it and padded along the shorter side.
- `mean`, `std` — `Float32[3]`. Per-channel normalisation; same role
  as in `image_to_tensor_chw`.
- `pad_fill` — `Float32`. The post-normalisation value written into
  the padded region. YOLOX uses `114` (the raw byte 114, paired with
  raw normalisation `mean=[0,0,0]`, `std=[1/255, 1/255, 1/255]`); for
  ImageNet-normalised letterbox padding the canonical choice is `0`.

Output length = `3 × target_size × target_size`. For a YOLOX-tuned
all-in-one preset (BGR + 0–255 + 114-gray padding bundled together),
see [`yolox_preprocess`](inference.md#yolox_preprocess).

### tensor_to_image_chw

`tensor_to_image_chw(tensor, height, width)` → `Image`
`tensor_to_image_chw(tensor, height, width, mean, std)` → `Image`

Inverse of `image_to_tensor_chw`: takes a flat NCHW Float32 tensor
(`3 × height × width`), optionally denormalises with `mean` / `std`,
and packs the bytes back into a PNG-encoded RGB image.

The 3-arg shortcut assumes the tensor is already in `[0, 1]` (i.e.
produced with `mean=[0,0,0]`, `std=[1,1,1]`); multiplies by 255 and
clamps to byte range. The 5-arg form applies the inverse normalize
`(value * std + mean) * 255` before clamping — pass the same
mean/std the producer used. Values outside `[0, 1]` post-denormalize
clamp rather than wrap (diffusion outputs frequently land slightly
outside the range).

```sql
-- SD VAE-decoder output in [-1, 1] → image:
RETURN tensor_to_image_chw(rgb, size, size,
    [0.5::Float32, 0.5::Float32, 0.5::Float32],
    [0.5::Float32, 0.5::Float32, 0.5::Float32])
```

Canonical use: `models.sd_turbo`

### tensor_to_image_hwc

`tensor_to_image_hwc(tensor, height, width)` → `Image`
`tensor_to_image_hwc(tensor, height, width, mean, std)` → `Image`

NHWC sibling of `tensor_to_image_chw`. Same call shape and
semantics; the input tensor is interleaved `[R, G, B]` triples
instead of channel-major planes.

## Normalization Presets

Per-channel mean / std constants for the most common training-data
normalisations. Each returns `Float32[3]`; pass directly into
`image_to_tensor_chw`'s `mean` / `std` arguments.

These exist as named scalars because inline array literals like
`[0.485, 0.456, 0.406]` parse as `Array<Int8>` and don't auto-cast
to `Array<Float32>` — the named helpers sidestep that and are
cheaper to type than `CAST(... AS Float32)` triples.

### imagenet_mean

`imagenet_mean()` → `Float32[3]`

ImageNet RGB channel mean: `[0.485, 0.456, 0.406]`. The standard
normalisation used across PyTorch / torchvision vision models trained
on ImageNet — ResNet, MobileNet, EfficientNet, ViT, and most
detection / segmentation backbones derived from them.

### imagenet_std

`imagenet_std()` → `Float32[3]`

ImageNet RGB channel std: `[0.229, 0.224, 0.225]`. Pair with
`imagenet_mean()`.

```sql
DECLARE tensor Float32[] = image_to_tensor_chw(
    resized, [rh, rw], imagenet_mean(), imagenet_std());
```

### clip_mean

`clip_mean()` → `Float32[3]`

OpenAI CLIP RGB channel mean:
`[0.48145466, 0.4578275, 0.40821073]`. The normalisation used by
CLIP image encoders and the models that re-use them (BLIP,
Florence-2's vision tower, MetaCLIP, OpenCLIP variants).

### clip_std

`clip_std()` → `Float32[3]`

OpenAI CLIP RGB channel std:
`[0.26862954, 0.26130258, 0.27577711]`. Pair with `clip_mean()`.

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

### image_concat

`image_concat(a, b)` → Image
`image_concat(a, b, direction)` → Image

Join two images into one. The two-argument form places them side-by-side (horizontal); pass a third `direction` argument to choose the axis (case-insensitive): `'horizontal'` / `'h'` places `b` to the right of `a`, `'vertical'` / `'v'` places `b` below `a`.

The images need not share dimensions. A horizontal join is `(w_a + w_b) × max(h_a, h_b)`; a vertical join is `max(w_a, w_b) × (h_a + h_b)`. Each image is centred along the perpendicular (cross) axis and any leftover margin is filled with transparent pixels — a shorter image beside a taller one gains transparent bands rather than being stretched, so the output is always RGBA. Returns `NULL` when either image is `NULL`.

```sql
-- Side-by-side before/after strip for a super-resolution model
SELECT image_concat(img, models.realesrgan_x4v3(img)) AS comparison FROM photos

-- Stack the original above its depth map
SELECT image_concat(img, apply_colormap(models.midas_small(img)), 'vertical') AS panel
FROM photos
```

### image_draw_bounding_boxes

`image_draw_bounding_boxes(img, boxes Array<Struct> [, stroke_color Color [, fill_color Color]])` → Image
`image_draw_bounding_boxes(img, box Struct [, stroke_color Color [, fill_color Color]])` → Image

Overlay bounding-box rectangles (with optional labels) on an image. The element struct exposes `x`, `y`, `w`, `h` (numeric, in source-image pixel coordinates, top-left origin) plus optional `label` (String) and `score` (numeric, 0–1) fields. Two element shapes are accepted: flat `{x, y, w, h, ...}`, or a nested `{bbox: BoundingBox, label?, score?}` where the inner `BoundingBox` carries `x/y/w/h` — the named-type detection shapes (`ScoredDetection`, `FaceDetection`, `OcrLine`, `RegionScore`, ...) use the nested form. Field names are resolved via the per-query type registry, so any box-producing model that declares its `OutputFields` drops in directly. When `label` and/or `score` are present, a label chip is drawn at the box's top-left corner showing `"<label> <score>"` (or whichever fields are non-null). A null or empty `boxes` argument returns the source image unchanged.

Two optional trailing `Color` arguments customise the overlay:

- **`stroke_color`** — the box outline colour. Defaults to opaque red (`#FF4040`). The label chip background tracks this colour (at a fixed alpha), so the whole overlay reads as one colour scheme.
- **`fill_color`** — the box *interior* colour, painted beneath the outline. Defaults to fully transparent (no fill). Because `Color` carries an alpha component, a translucent fill paints a see-through highlight over each box — e.g. `color(0, 255, 0, 64)` shades detections in faint green without hiding the underlying image.

Build colours with `color(r, g, b [, a])` or `color_hex('#rrggbbaa')` — see [Drawing Functions](drawing.md). A `NULL` color argument falls back to its default.

```sql
-- Array of detections from a YOLO-style model
SELECT image_draw_bounding_boxes(img, models.yolov8n(img)) AS annotated FROM photos

-- Single bounding box (e.g. one face from a face detector)
SELECT image_draw_bounding_boxes(img, models.scrfd(img)[0]) AS annotated FROM portraits

-- Blue outline with a translucent blue fill highlight
SELECT image_draw_bounding_boxes(
    img, models.yolov8n(img), color_hex('#1e88e5'), color(30, 136, 229, 48)
) AS annotated FROM photos
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

### image_resize_to_stride

`image_resize_to_stride(img, max_side, stride)` → `Image`

Aspect-preserving resize so the longest side is at most `max_side`,
with both output dimensions rounded down to the nearest multiple of
`stride`. The combination matches the PaddleOCR `DetResizeForTest`
recipe and any other model whose ONNX export pins the input height /
width to a stride multiple (typically 32 for FPN-style backbones).

```sql
-- PaddleOCR detector: longest side ≤ 960, both dims aligned to 32.
DECLARE resized Image = image_resize_to_stride(img, 960, 32);
```

Canonical use: `models.paddleocr_v4_det`.

### image_resize_foreground

`image_resize_foreground(img, ratio)` → `Image`

Crop the input to its alpha bounding box, then centre it in a square
canvas with `ratio` of each side occupied by the subject (the rest is
transparent margin). The standard preprocessor for single-object
generative-3D pipelines (TripoSR's `resize_foreground(_, 0.85)`
reference): a photo where the subject covers a third of the frame
gets framed so the model has consistent context regardless of source
crop.

`ratio` is typically in `[0.7, 0.9]`. Smaller leaves more breathing
room (helpful when the subject has extremities like legs or antennas
that would otherwise hit the edge); larger zooms in more aggressively.

Canonical use: `models.triposr`.

### image_composite_over

`image_composite_over(img, background_rgb)` → `Image`

Flatten an alpha-bearing image against a solid background colour:
`out = rgb · alpha + background · (1 - alpha)`. `background_rgb` is a
`Float32[3]` with values in `[0, 1]`. No-op for inputs that are
already fully opaque RGB.

Used when the downstream model was trained on imagery composited
over a specific background colour — feeding it a raw cutout
(alpha = 0 outside subject) or a flatten-to-black render produces
silhouette ghosts or fringing artifacts.

```sql
-- TripoSR's training data is rembg-cleaned then composited over 0.5 gray.
DECLARE flat Image = image_composite_over(
    framed, [0.5::Float32, 0.5::Float32, 0.5::Float32]);
```

### image_cutout

`image_cutout(image, mask)` → `Image`

Apply a grayscale mask as the alpha channel of an image. The output
has the original image's RGB and a new alpha channel sampled from
the mask's red channel — opaque where the mask is white, transparent
where it is black. Pair with `models.u2netp(img)` or
`models.mobilesam(img)` to turn segmentation output into a typed
RGBA cutout in a single SQL expression.

```sql
SELECT image_cutout(img, models.u2netp(img)) AS subject FROM photos
```

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

- [Inference Helpers](inference.md) — vision-model preprocess / postprocess (`yolox_preprocess`, `sam_preprocess`, `dbnet_postprocess`, `mask_nms_planes`, `depth_map_to_image`, ...) paired with the tensor-conversion surface here.
- [CREATE MODEL](../sql/create-model.md) — the DDL surface that wires `image_to_tensor_chw` and friends into ONNX inference bodies.
- [Tokenization Functions](tokenization.md) — text → tensor preprocessing for vision-language models.
- [Vector & Tensor Functions](vector.md) — operations on tensors produced by `image_to_tensor_hwc` / `image_to_tensor_chw`.
- [Functions Reference](string.md) — complete function listing across all categories.
