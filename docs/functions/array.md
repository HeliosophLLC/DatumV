---
title: Array Functions
category: array
---

# Array Functions

Typed array construction, inspection, search, manipulation, and string conversion.

> **Tip:** Arrays can be constructed with bracket syntax: `[1, 2, 3]` is equivalent to `array(1, 2, 3)`.

## Multi-dim arrays

Columns declared with multiple dimensions (`Array<Float32>(2, 3)`) and function outputs whose ONNX tensor rank is ≥ 2 (e.g. `infer()` for a `[1, H, W]` depth map) carry an explicit shape — the multi-dim flag survives across SQL expressions and round-trips through `.datum`.

**Supported element kinds.** Multi-dim is supported for **every** element kind in the type system: fixed-width primitives (`Int*`, `UInt*`, `Float*`, `Decimal`, `Date`, `Time`, `Duration`, `Uuid`, `Point*`, `Boolean`), byte arrays (`UInt8`), `String`, the blob-element kinds (`Image`, `Audio`, `Video`, `Json`, `PointCloud`, `Mesh`), and `Struct`. `Array<String>(m, n)` stores one UTF-8-encoded slot per cell; the blob kinds each store one encoded payload per cell; `Array<Struct>(m, n)` stores one field-array slot per cell with a per-element TypeId in the slot bytes.

Each function below behaves in one of two ways on multi-dim input:

| Behavior | Meaning | Functions |
|---|---|---|
| **Shape-aware** | Reads the shape and operates per-dim. | `cardinality`, `array_length`, `array_ndims`, `array_shape`, `array_get`, `m[y, x]` bracket syntax |
| **Shape-agnostic (flatten)** | Iterates the flat element span; the per-dim shape is irrelevant. The output is a flat 1-D array of the same total length. | `array_concat`, `array_contains`, `array_to_string` |

The shape-agnostic functions intentionally "drop" the multi-dim shape on output — the result is always a flat 1-D array. This is what model-pipeline SQL expects: e.g. Florence-2's `array_concat(visual_features, prompt_embeds)` stitches the flat element spans regardless of source rank. If you need shape-preserving multi-dim operations, decompose with `array_get` / `array_shape` and rebuild.

### array

`array(a, b, c, ...)` → Array | QU: 1

Construct a typed Array from one or more values. All arguments must share the same data kind.

```sql
SELECT array(1, 2, 3) AS nums
SELECT [1, 2, 3] AS nums  -- bracket syntax equivalent
```

### cardinality

`cardinality(arr)` → Int32 | QU: 1

Total number of elements in the array (product of all dimensions for a multi-dim array). PostgreSQL-compatible.

```sql
SELECT cardinality(array(1, 2, 3))            -- 3
SELECT cardinality(matrix_2x3)                -- 6
```

### array_length

`array_length(arr, dim)` → Int32 | QU: 1

Size of the requested dimension (1-based). PostgreSQL-compatible. Returns NULL for an out-of-range dimension.

```sql
SELECT array_length(array(1, 2, 3), 1)        -- 3
SELECT array_length(matrix_2x3, 1)            -- 2
SELECT array_length(matrix_2x3, 2)            -- 3
SELECT array_length(matrix_2x3, 99)           -- NULL
```

### array_ndims

`array_ndims(arr)` → Int32 | QU: 1

Number of dimensions of an array. PostgreSQL-compatible. Flat (1-D) arrays return `1`; multi-dim arrays return their declared ndim.

```sql
SELECT array_ndims(array(1, 2, 3))            -- 1
SELECT array_ndims(matrix_2x3)                -- 2
```

### array_shape

`array_shape(arr)` → Array<Int32> | QU: 1

Per-dimension sizes of an array.

```sql
SELECT array_shape(array(1, 2, 3))            -- [3]
SELECT array_shape(matrix_2x3)                -- [2, 3]
```

### array_get

`array_get(arr, i0, i1, ...)` → element kind | QU: 1

Read a single element by positional indices. The number of indices must equal the array's `ndim`. Indices are 1-based (PostgreSQL semantics) and row-major. Equivalent to the `arr[i]` / `arr[y, x]` bracket-syntax accessor. Returns NULL for a 1-D out-of-bounds index; throws on dimension-count or per-dim multi-dim-index range errors.

```sql
SELECT array_get(array(10, 20, 30), 1)        -- 10 (first element)
SELECT array_get(array('a', 'b', 'c'), 2)     -- 'b'
SELECT array_get(matrix_2x3, 1, 3)            -- element at row 1, col 3
```

### array_join

`array_join(arr, separator)` → String | QU: 1

Join elements into a String with separator. Null elements are skipped. String elements used directly; others converted via ToString.

```sql
SELECT array_join(array('a', 'b', 'c'), ', ') -- 'a, b, c'
```

### array_contains

`array_contains(arr, value)` → Boolean | QU: 1

Returns whether the array contains the value (by equality).

```sql
SELECT array_contains(array(1, 2, 3), 2) -- true
```

### array_position

`array_position(arr, value)` → Float32 | QU: 1

1-based index of the first matching element, or null if not found.

```sql
SELECT array_position(array('a', 'b', 'c'), 'b') -- 2
```

### array_sort

`array_sort(arr)` → Array | QU: 1

Sorted copy (ascending). Uses ORDER BY comparison semantics -- nulls sort last. Supports Float32, UInt8, String, Date, DateTime elements.

```sql
SELECT array_sort(array(3, 1, 2)) -- [1, 2, 3]
```

### array_reverse

`array_reverse(arr)` → Array | QU: 1

Reversed copy of the array.

```sql
SELECT array_reverse(array(1, 2, 3)) -- [3, 2, 1]
```

### array_distinct

`array_distinct(arr)` → Array | QU: 1

Remove duplicates, preserving first-occurrence order. Uses DataValue equality.

```sql
SELECT array_distinct(array(1, 2, 2, 3, 1)) -- [1, 2, 3]
```

### array_slice

`array_slice(arr, start, length)` → Array | QU: 1

Sub-array extraction. 1-based start, clamped to bounds. Returns empty array if out of range.

```sql
SELECT array_slice(array(10, 20, 30, 40), 2, 2) -- [20, 30]
```

### array_concat

`array_concat(arr1, arr2)` → Array | QU: 1

Concatenate two arrays. Both must share the same element kind.

```sql
SELECT array_concat(array(1, 2), array(3, 4)) -- [1, 2, 3, 4]
```

### array_min

`array_min(arr)` → element type | QU: 1

Minimum element, skipping nulls. Returns null for an empty or all-null array. Return type matches the array's element kind.

```sql
SELECT array_min(array(3, 1, 2)) -- 1
```

### array_max

`array_max(arr)` → element type | QU: 1

Maximum element, skipping nulls. Returns null for an empty or all-null array. Return type matches the array's element kind.

```sql
SELECT array_max(array(3, 1, 2)) -- 3
```

### array_sum

`array_sum(arr)` → Float32 | QU: 1

Sum of numeric (Float32 or UInt8) elements, skipping nulls. Returns null for an empty or all-null array. Always returns Float32.

```sql
SELECT array_sum(array(1, 2, 3)) -- 6
```

### array_avg

`array_avg(arr)` → Float32 | QU: 1

Average (mean) of numeric elements, skipping nulls. Returns null for an empty or all-null array. Always returns Float32.

```sql
SELECT array_avg(array(2, 4, 6)) -- 4
```

### array_transform

`array_transform(arr, element -> expr)` → Array | QU: 1

Applies a lambda to each element, returning a new array of transformed values.

```sql
SELECT array_transform(array(1, 2, 3), x -> x * 2) -- [2, 4, 6]
```

### array_filter

`array_filter(arr, element -> Boolean)` → Array | QU: 1

Filters an array, keeping only elements where the lambda predicate returns true.

```sql
SELECT array_filter(array(1, 2, 3, 4, 5), x -> x > 2) -- [3, 4, 5]
```

## Numeric Array Primitives

A small family of element-wise BLAS-flavoured primitives and shape
transforms used heavily by SQL-defined model bodies. They aren't
locked to ML — they're general-purpose array operations — but most
current uses today are inside `CREATE MODEL` bodies (diffusion
samplers, depth-map resizing, vision-language attention masks),
where a per-element SQL loop would be orders of magnitude slower
than the tight C# loop these functions wrap.

### array_axpy

`array_axpy(y, a, x)` → `Float32[]`

Element-wise `y[i] + a * x[i]` over two equal-length `Float32[]`
arrays with a `Float32` scalar `a`. Returns a fresh array of the
same length; arrays of different lengths raise. Named after the
[BLAS Level-1 `axpy`](https://www.netlib.org/blas/) routine that
fills the same role.

The diffusion Euler update is the canonical site:

```sql
SET latents = array_axpy(latents, sigma_next - sigma, noise_pred)
```

### array_scale

`array_scale(a, s)` → `Float32[]`

Multiplies every element of a `Float32[]` by a `Float32` scalar `s`;
returns a fresh array.

```sql
-- Scale the initial diffusion latent by sigma_max:
DECLARE latents Float32[] = array_scale(sample_normal(n), sigmas[1]);

-- VAE post-decode scale-factor divide:
SET latents = array_scale(latents, CAST(1.0 / 0.18215 AS Float32))
```

### array_clamp

`array_clamp(a, min, max)` → `Float32[]`

Element-wise clamp to `[min, max]`; returns a fresh `Float32[]` of
the same length. `min > max` raises.

The motivating use is keeping a Float32 activation inside the fp16
representable range (±65504) before feeding it to an fp16 ONNX
session — values that would otherwise become ±Inf on cast and
NaN-poison the next attention softmax:

```sql
SET pooled = array_clamp(pooled, -65504.0::Float32, 65504.0::Float32)
```

### array_concat_last_dim

`array_concat_last_dim(a, a_inner, b, b_inner)` → `Float32[]`

Concatenate two flat `[outer, inner_a]` and `[outer, inner_b]`
tensors along their inner (last) dimension, producing
`[outer, inner_a + inner_b]` with per-row interleaving — each block
of `inner_a + inner_b` output elements is one row of `a` followed by
the matching row of `b`.

`outer` is derived as `cardinality(a) / a_inner` and must equal
`cardinality(b) / b_inner` or the function throws.

Distinct from
[`array_concat`](#array_concat), which flattens its inputs and
appends end-to-end with no per-row interleaving. SDXL uses this to
merge the two text encoders' per-token hidden states along the
hidden dim (CLIP-L `[1, 77, 768]` + OpenCLIP-G `[1, 77, 1280]` →
`[1, 77, 2048]`):

```sql
DECLARE combined_embeds Float32[] = array_concat_last_dim(
    clip_l_hidden, 768::Int32, openclip_g_hidden, 1280::Int32);
```

### array_repeat

`array_repeat(value, count)` → `Array<T>`

Build an array of `count` copies of `value`. The element kind of the
result matches the value's scalar kind. Supports `Int64`, `Int32`,
`Float32`, and `Boolean` values. `count` must be non-negative;
`count = 0` produces an empty array.

Used to construct fixed-content tensors that model bodies need at
runtime — all-ones attention masks of length matching a concatenated
visual + prompt sequence, zero-filled mask-input tensors for SAM
decoders, etc.

```sql
-- All-ones attention mask of length n:
DECLARE mask Int64[] = array_repeat(1::Int64, n);

-- Zero-filled SAM mask_input tensor (256 × 256 = 65536 floats):
DECLARE mask_input Float32[] = array_repeat(0.0::Float32, 256 * 256)
```

### array_resize_2d

`array_resize_2d(arr, dst_h, dst_w)` → `Array<Float32>(dst_h, dst_w)`

Bilinear-resample a 2D `Float32` field onto a new pixel grid. Result
is a shape-aware multi-dim array, so downstream consumers can read
its dimensions via [`array_shape`](#array_shape) and index with
[`array_get(arr, y, x)`](#array_get).

Rank handling: rank-2 `(h, w)` inputs are consumed directly; rank-3
`(1, h, w)` inputs (the typical ONNX `[batch=1, h, w]` shape for
depth / segmentation outputs) auto-squeeze the leading dim;
anything else raises with the observed shape.

Sample positions follow the standard half-pixel convention used by
PIL / OpenCV / SkiaSharp (`(src_y + 0.5) * src_h / dst_h - 0.5`),
boundary samples clamp. Linear interpolation preserves the source
value's units — metres for `models.zoedepth_nyu_kitti_meters`,
probabilities for segmentation masks, etc.

```sql
-- Resize ZoeDepth's [1, 384, 384] metric output onto the source image grid:
DECLARE depth_native Array<Float32> = models.zoedepth_nyu_kitti_meters(img);
DECLARE depth_full   Array<Float32> = array_resize_2d(
    depth_native, image_height(img), image_width(img));
```

## See Also

- [Inference Helpers](inference.md) -- ML-specific dispatch surface (`infer`, `decode_seq2seq`, ...) and the diffusion schedule / noise functions (`sd_turbo_schedule`, `sample_normal`) that pair with the numeric array primitives above
- [CREATE MODEL](../sql/create-model.md) -- the DDL surface where most uses of `array_axpy`, `array_scale`, `array_clamp`, `array_concat_last_dim`, `array_repeat`, and `array_resize_2d` show up today
- [String Functions](string.md) -- string_to_array, regexp_split_to_array, and array_join
- [JSON Functions](json.md) -- json_array_length and JSON array extraction
- [Utility & Type Conversion Functions](utility.md) -- type checks and casting
