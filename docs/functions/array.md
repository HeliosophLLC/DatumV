---
title: Array Functions
category: array
---

# Array Functions

Typed array construction, inspection, search, manipulation, and string conversion.

> **Tip:** Arrays can be constructed with bracket syntax: `[1, 2, 3]` is equivalent to `array(1, 2, 3)`.

## Multi-dim arrays

Columns declared with multiple dimensions (`Array<Float32>(2, 3)`) and function outputs whose ONNX tensor rank is ≥ 2 (e.g. `infer()` for a `[1, H, W]` depth map) carry an explicit shape — the multi-dim flag survives across SQL expressions and round-trips through `.datum`.

**Supported element kinds.** Multi-dim is supported for fixed-width primitives (`Int*`, `UInt*`, `Float*`, `Decimal`, `Date`, `Time`, `Duration`, `Uuid`, `Point*`, `Boolean`), byte arrays (`UInt8`), `String`, and `Image`. The remaining reference / blob kinds (`Struct`, `Audio`, `Video`, `Json`, `PointCloud`, `Mesh`) reject at DDL time. `Array<String>(m, n)` stores one UTF-8-encoded slot per cell; `Array<Image>(m, n)` stores one encoded-image blob per cell.

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

## See Also

- [String Functions](string.md) -- string_to_array, regexp_split_to_array, and array_join
- [JSON Functions](json.md) -- json_array_length and JSON array extraction
- [Utility & Type Conversion Functions](utility.md) -- type checks and casting
