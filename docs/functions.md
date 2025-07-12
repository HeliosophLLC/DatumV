# Functions Reference

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md)

DatumIngest provides a comprehensive function library for data transformation, ML feature engineering, and image processing. All functions can be composed in SQL expressions.

## Numeric / Array

| Function | Signature | Description |
|----------|-----------|-------------|
| `normalize` | `normalize(val, [min], [max])` | Normalize to 0–1 range. Byte/byte[]: default 0–255. Scalar/Vector: requires min/max. |
| `clamp` | `clamp(val, min, max)` | Clamp value to [min, max]. Works on Scalar, Vector, Matrix, Tensor. |
| `denormalize` | `denormalize(val, factor)` | Multiply by factor (reverse of normalize). |
| `reshape` | `reshape(tensor, dim1, dim2, ...)` | Reinterpret tensor shape without copying. Element count must match. |

## String

| Function | Signature | Description |
|----------|-----------|-------------|
| `len` | `len(val)` | Length of string or collection. |
| `mid` | `mid(str, start, length)` | Extract substring by position and length (0-based). |
| `substring` | `substring(str, start, [length])` | Extract substring from start position (0-based). |
| `get_filename` | `get_filename(path)` | Return file name with extension from path. |
| `get_file_extension` | `get_file_extension(path)` | Return extension (with dot) from path. |
| `get_path` | `get_path(path)` | Return directory portion of path. |

## JSON Column Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `json_value` | `json_value(col, path)` | Extract scalar from JSON string at path. Returns String, Scalar, or null. |
| `json_query` | `json_query(col, path)` | Extract JSON fragment (array/object). Returns JsonValue or Vector if all-numeric. |
| `json_exists` | `json_exists(col, path)` | Returns 1.0 if path exists in JSON, 0.0 otherwise. |
| `json_array_length` | `json_array_length(col, [path])` | Count elements in JSON array at root or path. |

## Type Conversion

| Function | Signature | Description |
|----------|-----------|-------------|
| `cast` | `cast(val, targetKind)` | Explicit type conversion. Date→Scalar yields epoch days; DateTime→Scalar yields epoch seconds. |
| `to_epoch` | `to_epoch(val)` | Convert Date to epoch days or DateTime to epoch seconds (since 1970-01-01) as Scalar. |

## Temporal Feature Extraction

| Function | Signature | Description |
|----------|-----------|-------------|
| `date_part` | `date_part(part, val)` | Extract a named component from a Date or DateTime as Scalar. |
| `cyclical_encode` | `cyclical_encode(val, period)` | Encode a Scalar as a 2-element Vector `[sin(2π·val/period), cos(2π·val/period)]`. |

### `date_part` supported parts

| Part Name | Returns | Example |
|-----------|---------|----------|
| `year` | Year number | 2026 |
| `month` | 1–12 | 3 |
| `day` | 1–31 | 16 |
| `day_of_week` | 0 (Sunday) – 6 (Saturday) | 1 (Monday) |
| `hour` | 0–23 (Date returns 0) | 14 |
| `minute` | 0–59 (Date returns 0) | 30 |
| `second` | 0–59 (Date returns 0) | 45 |
| `day_of_year` | 1–366 | 75 |
| `week_of_year` | 1–53 (ISO 8601) | 12 |
| `quarter` | 1–4 | 1 |
| `is_weekend` | 0 or 1 | 0 |

### Temporal ML encoding examples

```sql
-- Convert date to epoch days for use as a numeric feature
SELECT to_epoch(date_col) AS epoch_days FROM data

-- Equivalent via CAST
SELECT CAST(date_col AS Scalar) AS epoch_days FROM data

-- Extract individual components
SELECT date_part('year', date_col) AS year,
       date_part('month', date_col) AS month,
       date_part('day_of_week', date_col) AS dow
FROM data

-- Cyclical encoding for periodic features (preserves month 12 → 1 proximity)
SELECT cyclical_encode(date_part('month', date_col), 12) AS month_encoded,
       cyclical_encode(date_part('hour', datetime_col), 24) AS hour_encoded
FROM data

-- Full temporal feature vector via concatenation
SELECT vec_concat(
    cyclical_encode(date_part('month', d), 12),
    cyclical_encode(date_part('day_of_week', d), 7),
    cyclical_encode(date_part('hour', d), 24)
) AS temporal_features
FROM data
```

## Table-Valued Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `unnest` | `unnest(array_col)` | Expand array-valued column into separate rows. Works with Vector, UInt8Array, JsonValue arrays. |
| `range` | `range(start, end[, step])` | Generate a sequence of rows with a `Value` column from start to end (inclusive). Default step is 1. |

## Example SQL with functions

```sql
-- Normalize a numeric column
SELECT id, normalize(score, 0, 100) AS norm_score FROM data

-- JSON extraction
SELECT json_value(metadata, '$.category') AS cat FROM records

-- String manipulation
SELECT id, get_filename(file_path) AS name FROM files WHERE len(file_path) > 10

-- Reshape vectors
SELECT reshape(embedding, 16, 16) AS matrix_embed FROM features

-- Type casting
SELECT id, cast(score, 'UInt8') AS byte_score FROM data

-- Math functions
SELECT abs(delta), sqrt(variance), pow(base_val, 2) FROM metrics

-- ML activations on embeddings
SELECT sigmoid(score), relu(raw_output), gelu(activation) FROM model_outputs

-- Tensor introspection
SELECT rank(weights) AS ndim, shape(weights) AS dims, rdim(weights, 0) AS rows FROM features

-- Vector reductions
SELECT vec_mean(embedding), vec_norm(embedding), vec_std(features) FROM vectors

-- Distance computation
SELECT cosine_similarity(query_vec, doc_vec) AS similarity FROM search_results

-- Softmax normalization
SELECT softmax(logits) AS probabilities FROM predictions

-- Vector manipulation
SELECT vec_slice(embedding, 0, 128) AS half, vec_sort(scores) FROM data

-- Utility
SELECT coalesce(primary_score, fallback_score) AS score FROM results

-- Image preprocessing pipeline
SELECT resize(file_bytes, 224, 224) AS img, label FROM images
SELECT decode_image(resize(file_bytes, 224, 224)) AS pixels FROM images
SELECT width(file_bytes) AS w, height(file_bytes) AS h FROM images WHERE pixel_count(file_bytes) > 1000000

-- Image augmentation
SELECT noise(grayscale(file_bytes), 'gaussian', 5) AS augmented FROM training_images
```

## Math — Basic Arithmetic (8)

| Function | Signature | Description |
|----------|-----------|-------------|
| `abs` | `abs(x)` | Absolute value. Element-wise for vectors/matrices/tensors. |
| `sign` | `sign(x)` | Returns -1, 0, or 1. Element-wise. |
| `negate` | `negate(x)` | Negation (-x). Element-wise. |
| `mod` | `mod(a, b)` | Modulus (a % b). Element-wise with broadcast. |
| `add` | `add(a, b)` | Addition. Element-wise with scalar broadcast. |
| `subtract` | `subtract(a, b)` | Subtraction. Element-wise with scalar broadcast. |
| `multiply` | `multiply(a, b)` | Multiplication. Element-wise with scalar broadcast. |
| `divide` | `divide(a, b)` | Division. Element-wise with scalar broadcast. |

## Math — Powers, Roots & Logarithms (10)

| Function | Signature | Description |
|----------|-----------|-------------|
| `sqrt` | `sqrt(x)` | Square root. |
| `cbrt` | `cbrt(x)` | Cube root. |
| `square` | `square(x)` | Square (x²). |
| `exp` | `exp(x)` | Natural exponential (eˣ). |
| `exp2` | `exp2(x)` | Base-2 exponential (2ˣ). |
| `ln` | `ln(x)` | Natural logarithm. |
| `log2` | `log2(x)` | Base-2 logarithm. |
| `log10` | `log10(x)` | Base-10 logarithm. |
| `pow` | `pow(base, exp)` | Power function. Element-wise with broadcast. |
| `log` | `log(x, base)` | Logarithm with custom base. |

## Math — Trigonometric & Hyperbolic (14)

| Function | Signature | Description |
|----------|-----------|-------------|
| `sin` | `sin(x)` | Sine (radians). |
| `cos` | `cos(x)` | Cosine (radians). |
| `tan` | `tan(x)` | Tangent (radians). |
| `asin` | `asin(x)` | Arc sine → radians. |
| `acos` | `acos(x)` | Arc cosine → radians. |
| `atan` | `atan(x)` | Arc tangent → radians. |
| `atan2` | `atan2(y, x)` | Two-argument arc tangent. |
| `sinh` | `sinh(x)` | Hyperbolic sine. |
| `cosh` | `cosh(x)` | Hyperbolic cosine. |
| `tanh` | `tanh(x)` | Hyperbolic tangent. |
| `degrees` | `degrees(x)` | Radians → degrees. |
| `radians` | `radians(x)` | Degrees → radians. |
| `pi` | `pi()` | Returns π constant. |
| `euler` | `euler()` | Returns Euler's number e. |

## Math — Rounding & Quantization (7)

| Function | Signature | Description |
|----------|-----------|-------------|
| `ceil` | `ceil(x)` | Round up to nearest integer. |
| `floor` | `floor(x)` | Round down to nearest integer. |
| `truncate` | `truncate(x)` | Remove fractional part toward zero. |
| `round` | `round(x, [decimals])` | Round to nearest integer or specified decimal places. |
| `quantize` | `quantize(x, step)` | Round to nearest multiple of step. |
| `bucketize` | `bucketize(val, boundaries)` | Assign value to bucket index based on sorted boundary vector. |
| `clip` | `clip(x, min, max)` | Clip to range (alias for clamp). |

## Math — ML Activation Functions (12)

| Function | Signature | Description |
|----------|-----------|-------------|
| `sigmoid` | `sigmoid(x)` | Logistic sigmoid σ(x) = 1/(1+e⁻ˣ). |
| `relu` | `relu(x)` | Rectified Linear Unit max(0, x). |
| `selu` | `selu(x)` | Scaled Exponential Linear Unit. |
| `gelu` | `gelu(x)` | Gaussian Error Linear Unit (fast approximation). |
| `swish` | `swish(x)` | Swish activation x·σ(x). |
| `softplus` | `softplus(x)` | Softplus ln(1+eˣ). |
| `softsign` | `softsign(x)` | Softsign x/(1+\|x\|). |
| `mish` | `mish(x)` | Mish activation x·tanh(softplus(x)). |
| `hard_sigmoid` | `hard_sigmoid(x)` | Piecewise linear approximation of sigmoid. |
| `hard_swish` | `hard_swish(x)` | Hard Swish x·hard_sigmoid(x). |
| `leaky_relu` | `leaky_relu(x, [alpha])` | Leaky ReLU with configurable slope (default α=0.01). |
| `elu` | `elu(x, [alpha])` | Exponential Linear Unit (default α=1.0). |

## Math — Softmax & Normalization (3)

| Function | Signature | Description |
|----------|-----------|-------------|
| `softmax` | `softmax(vec)` | Numerically stable softmax → probability vector. |
| `log_softmax` | `log_softmax(vec)` | Log-softmax via log-sum-exp trick. |
| `l2_normalize` | `l2_normalize(vec)` | L2 normalize to unit length. |

## Math — Vector Reductions (14)

| Function | Signature | Description |
|----------|-----------|-------------|
| `vec_sum` | `vec_sum(x)` | Sum of all elements → Scalar. |
| `vec_mean` | `vec_mean(x)` | Mean of all elements → Scalar. |
| `vec_min` | `vec_min(x)` | Minimum element → Scalar. |
| `vec_max` | `vec_max(x)` | Maximum element → Scalar. |
| `vec_std` | `vec_std(x)` | Population standard deviation → Scalar. |
| `vec_var` | `vec_var(x)` | Population variance → Scalar. |
| `vec_median` | `vec_median(x)` | Median → Scalar. |
| `vec_argmin` | `vec_argmin(x)` | Index of minimum element → Scalar. |
| `vec_argmax` | `vec_argmax(x)` | Index of maximum element → Scalar. |
| `vec_norm` | `vec_norm(x, [p])` | Lp norm (default p=2). p=∞ for max-norm. |
| `vec_count_nonzero` | `vec_count_nonzero(x)` | Count of non-zero elements → Scalar. |
| `vec_any` | `vec_any(x)` | 1 if any element is non-zero, else 0. |
| `vec_all` | `vec_all(x)` | 1 if all elements are non-zero, else 0. |
| `vec_product` | `vec_product(x)` | Product of all elements → Scalar. |

## Math — Tensor Introspection (3)

| Function | Signature | Description |
|----------|-----------|-------------|
| `rank` | `rank(x)` | Number of dimensions → Scalar. Vector=1, Matrix=2, Tensor=N. |
| `rdim` | `rdim(x, axis)` | Size of a specific dimension → Scalar. |
| `shape` | `shape(x)` | All dimension sizes → Vector. |

## Math — Vector Manipulation (12)

| Function | Signature | Description |
|----------|-----------|-------------|
| `vec` | `vec(a, b, ...)` | Construct a vector from scalars and/or vectors. Scalars contribute one element; vectors are flattened in order. |
| `tensor` | `tensor(v1, v2, ...)` | Stack two or more equal-length vectors as rows into a Matrix with shape [N, M]. |
| `vec_slice` | `vec_slice(vec, start, len)` | Extract sub-vector by position and length. |
| `vec_concat` | `vec_concat(v1, v2, ...)` | Concatenate two or more vectors. |
| `vec_reverse` | `vec_reverse(vec)` | Reverse element order. |
| `vec_sort` | `vec_sort(vec)` | Sort ascending (returns copy). |
| `vec_unique` | `vec_unique(vec)` | Unique elements preserving first-occurrence order. |
| `vec_flatten` | `vec_flatten(x)` | Flatten Matrix/Tensor to Vector. |
| `vec_pad` | `vec_pad(vec, len, fill)` | Pad vector to target length with fill value. |
| `vec_repeat` | `vec_repeat(vec, count)` | Repeat vector n times. |
| `linspace` | `linspace(start, stop, n)` | Generate n evenly spaced values from start to stop. |
| `arange` | `arange(start, stop, step)` | Generate values with fixed step (excludes stop). |

## Math — Distance & Similarity (5)

| Function | Signature | Description |
|----------|-----------|-------------|
| `cosine_similarity` | `cosine_similarity(a, b)` | Cosine similarity [-1, 1] between two vectors. |
| `euclidean_distance` | `euclidean_distance(a, b)` | Euclidean (L2) distance between two vectors. |
| `manhattan_distance` | `manhattan_distance(a, b)` | Manhattan (L1) distance between two vectors. |
| `dot` | `dot(a, b)` | Dot product of two vectors. |
| `hamming_distance` | `hamming_distance(a, b)` | Hamming distance between two strings. |

## Math — Utility & Conditional (7)

| Function | Signature | Description |
|----------|-----------|-------------|
| `coalesce` | `coalesce(a, b, ...)` | Returns first non-null argument. |
| `greatest` | `greatest(a, b, ...)` | Returns maximum of scalar arguments. |
| `least` | `least(a, b, ...)` | Returns minimum of scalar arguments. |
| `is_nan` | `is_nan(x)` | Returns 1 if NaN, 0 otherwise. |
| `is_finite` | `is_finite(x)` | Returns 1 if finite, 0 if NaN or infinite. |
| `if_null` | `if_null(x, default)` | Returns x if not null, otherwise default. |
| `random` | `random()` | Random float in [0, 1). |

## Image — Metadata (5)

| Function | Signature | Description |
|----------|-----------|-------------|
| `width` | `width(img)` | Image width in pixels (header-only, no full decode). |
| `height` | `height(img)` | Image height in pixels (header-only). |
| `channels` | `channels(img)` | Number of color channels (header-only). |
| `pixel_count` | `pixel_count(img)` | Total pixel count (width × height, header-only). |
| `dimensions` | `dimensions(img, format)` | Dimension vector in specified format: `'HWC'`, `'CHW'`, `'WH'`, or `'WHC'`. |

## Image — Analysis (5)

| Function | Signature | Description |
|----------|-----------|-------------|
| `image_brightness_mean` | `image_brightness_mean(img)` | Mean brightness (BT.601 luminance) across all pixels → Scalar 0–255. |
| `image_brightness_std` | `image_brightness_std(img)` | Standard deviation of brightness across all pixels → Scalar. |
| `image_brightness_histogram` | `image_brightness_histogram(img)` | 256-bin brightness histogram → Vector. Each element is the pixel count for that luminance bin. |
| `detect_blur` | `detect_blur(img)` | Laplacian variance blur detector → Scalar. Higher values = sharper image. |
| `compression_artifact_score` | `compression_artifact_score(img)` | JPEG blockiness score → Scalar 0–1. Measures 8×8 block boundary discontinuities. |

## Image — Pixel Statistics (2)

| Function | Signature | Description |
|----------|-----------|-------------|
| `image_pixel_mean` | `image_pixel_mean(img[, channels])` | Mean pixel value. Without channels: overall mean → Scalar. With channels vector (0=R,1=G,2=B,3=A): per-channel means → Vector. |
| `image_pixel_std` | `image_pixel_std(img[, channels])` | Standard deviation of pixel values. Same signature as `image_pixel_mean`. |

## Image — Loading & Decode (2)

| Function | Signature | Description |
|----------|-----------|-------------|
| `load_image` | `load_image(bytes)` | Load encoded bytes (UInt8Array from ZIP/binary column) as an Image for use with transform and analysis functions. No decode — wraps the bytes as an opaque Image value for the fused pipeline. |
| `decode_image` | `decode_image(img)` | Decode to [H, W, 4] RGBA float tensor (values 0–255). |

## Image — Transforms (13)

| Function | Signature | Description |
|----------|-----------|-------------|
| `resize` | `resize(img, w, h[, fmt])` | Resize image to target width/height. |
| `crop` | `crop(img, x, y, w, h[, fmt])` | Crop rectangular region. |
| `grayscale` | `grayscale(img[, fmt])` | Convert to grayscale (BT.601 luminance). |
| `rotate` | `rotate(img, degrees[, fmt])` | Rotate by arbitrary angle. Canvas expands for non-90° rotations. |
| `noise` | `noise(img, type, val[, fmt])` | Add noise. Type: `'gaussian'` (val=stddev) or `'salt_pepper'` (val=ratio). |
| `blur` | `blur(img, radius[, fmt])` | Gaussian blur with the given sigma radius. |
| `brighten` | `brighten(img, intensity[, fmt])` | Increase brightness by adding intensity to RGB channels. |
| `darken` | `darken(img, intensity[, fmt])` | Decrease brightness by subtracting intensity from RGB channels. |
| `sobel` | `sobel(img[, fmt])` | Sobel edge detection → grayscale edge magnitude image. |
| `resize_and_crop` | `resize_and_crop(img, w, h, gravity[, fmt])` | Resize to fill then crop to exact dimensions. Gravity: `'center'`, `'top'`, `'bottom'`, `'left'`, `'right'`. |
| `affine_transform` | `affine_transform(img, angle, sx, sy, shx, shy[, fmt])` | Affine transformation with rotation (degrees), scale, and shear parameters. |
| `elastic_deform` | `elastic_deform(img, alpha, sigma[, fmt])` | Elastic deformation (Simard et al.). Alpha = displacement intensity, sigma = smoothing. |
| `perspective_warp` | `perspective_warp(img, intensity[, fmt])` or `perspective_warp(img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y[, fmt])` | Perspective distortion. Intensity mode: random warp. Explicit mode: normalized corner coordinates. |

## Image — Hashing (1)

| Function | Signature | Description |
|----------|-----------|-------------|
| `perceptual_hash` | `perceptual_hash(img)` | Difference hash (dHash) → 64-element Vector of 0/1 bits. Use with `hamming_distance()` for similarity. |

> All transform functions accept an optional trailing `fmt` argument (`'jpeg'`, `'png'`, `'webp'`) to control output encoding. Default preserves the original format.

## Fused image pipelines

When image transforms are nested — e.g. `resize(grayscale(crop(img, ...)), 224, 224)` — the engine automatically fuses the decode/encode cycle. Without fusion each function would decode the image from bytes, apply its transform, and re-encode to bytes, only for the next function to decode those bytes again. With fusion, only the first function in the chain decodes and only the final consumer encodes, eliminating N−1 redundant decode/encode cycles.

This is implemented via `ImageHandle`, a smart wrapper that carries either encoded bytes, a decoded `SKBitmap`, or both. Key properties:

- **Lazy decode** — `ImageHandle` created from encoded bytes defers decoding until a transform actually needs the bitmap.
- **Lazy encode** — `ImageHandle` created from a bitmap defers encoding until the bytes are needed (output writer, statistics, etc.).
- **Format propagation** — Each handle tracks the requested output format. Functions without a format argument inherit the input's format; functions with an explicit format argument override it. This matches the non-fused output byte-for-byte.
- **Deterministic disposal** — The expression evaluator disposes intermediate `ImageHandle` bitmaps as soon as they are consumed, releasing native SkiaSharp memory promptly rather than waiting for GC finalization.

Non-image consumers (`AsImage()` callers such as output writers, statistics accumulators, header-only metadata functions, and `CAST`) are transparent to the optimization — they receive encoded bytes on demand via lazy encoding.
