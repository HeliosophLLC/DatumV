---
title: Aggregate Functions
category: aggregate
---

# Aggregate Functions

Aggregate functions reduce multiple rows into a single result per group. Used with `GROUP BY` or as global aggregations (see [SQL Reference -- GROUP BY](../sql/group-by.md)).

All aggregate functions support the `DISTINCT` modifier (e.g. `COUNT(DISTINCT expr)`, `SUM(DISTINCT expr)`), which deduplicates argument values before accumulation. `COUNT(DISTINCT *)` is not supported -- use `COUNT(DISTINCT column)` instead.

### COUNT

`COUNT(*)` or `COUNT(expr)` -> Float32

Count all rows (`*`) or non-null values (`expr`).

### SUM

`SUM(expr)` -> Float32

Sum of non-null `Float32` values. Returns null if all inputs are null.

### AVG

`AVG(expr)` -> Float32

Arithmetic mean of non-null `Float32` values. Nulls excluded from denominator.

### MIN

`MIN(expr)` -> Float32/UInt8/String/Date/DateTime/Time

Minimum value. Supports Float32, UInt8, String, Date, DateTime, Time.

### MAX

`MAX(expr)` -> Float32/UInt8/String/Date/DateTime/Time

Maximum value. Supports Float32, UInt8, String, Date, DateTime, Time.

### VARIANCE

`VARIANCE(expr)` -> Float32

Sample variance (N-1 denominator). Alias for `VAR_SAMP`.

### VAR_SAMP

`VAR_SAMP(expr)` -> Float32

Sample variance (N-1). Null for fewer than 2 values.

### VAR_POP

`VAR_POP(expr)` -> Float32

Population variance (N denominator).

### STDDEV

`STDDEV(expr)` -> Float32

Sample standard deviation (N-1). Alias for `STDDEV_SAMP`.

### STDDEV_SAMP

`STDDEV_SAMP(expr)` -> Float32

Sample standard deviation (N-1). Null for fewer than 2 values.

### STDDEV_POP

`STDDEV_POP(expr)` -> Float32

Population standard deviation (N denominator).

### MEDIAN

`MEDIAN(expr)` -> Float32

Median (50th percentile) of non-null `Float32` values. Averages two middle values for even counts.

### PERCENTILE_CONT

`PERCENTILE_CONT(expr, fraction)` -> Float32

Continuous percentile with linear interpolation. Fraction in [0, 1].

### PERCENTILE_DISC

`PERCENTILE_DISC(expr, fraction)` -> Float32

Discrete percentile (nearest rank). Returns an actually observed value. Fraction in [0, 1].

### MODE

`MODE(expr)` -> Float32

Most frequently occurring value. Ties broken by first occurrence. Works on any comparable type.

### CORR

`CORR(y, x)` -> Float32

Pearson correlation coefficient between two numeric columns. Returns value in [-1, 1].

### COVAR_POP

`COVAR_POP(y, x)` -> Float32

Population covariance (N denominator) between two numeric columns.

### COVAR_SAMP

`COVAR_SAMP(y, x)` -> Float32

Sample covariance (N-1 denominator). Null for fewer than 2 pairs.

### APPROX_MEDIAN

`APPROX_MEDIAN(expr)` -> Float32

Approximate median using reservoir sampling. O(1) memory, ~1-5% error for large groups.

### APPROX_PERCENTILE

`APPROX_PERCENTILE(expr, fraction)` -> Float32

Approximate percentile using reservoir sampling. O(1) memory, ~1-5% error.

### pca_fit_agg

`pca_fit_agg(vec, k)` -> Struct

Fits a k-component PCA model over the group's Float32 vectors. Returns `Struct{mean Float32[d], components Float32[k, d], variance_ratio Float32[k]}` — the centering mean, the principal axes as rows of a k×d matrix, and each axis's share of total variance. Feed the model to [pca_project](vector.md#pca_project) to map vectors into the fitted space.

`k` must be a constant between 1 and the vector dimensionality; it is captured from the first row. Null vectors are skipped; the first non-null vector pins the dimensionality and later mismatches raise. Groups with no vectors return null; a single-vector group raises (covariance is undefined). Memory is O(d²) regardless of group size, with dimensionality capped at 4096. Results are deterministic: each component's sign is pinned so its largest-magnitude entry is positive.

Also works as a window aggregate, which fits and projects in one statement:

```sql
-- 2-D coordinates for every embedding, fit over the whole table
SELECT id, pca_project(m, embedding) AS xy
FROM (SELECT id, embedding, pca_fit_agg(embedding, 2) OVER () AS m FROM docs) s
```

Access model fields by name when the model itself is the result:

```sql
SELECT model['variance_ratio']
FROM (SELECT pca_fit_agg(embedding, 2) AS model FROM docs) s
```

### STRING_AGG

`STRING_AGG(expr, separator [ORDER BY ...])` -> String

Concatenates non-null string values with a separator. Supports intra-aggregate ORDER BY.

### ARRAY_AGG

`ARRAY_AGG(expr [ORDER BY ...])` -> Array

Collects non-null values into a typed `Array`. Accepts any data kind. Supports intra-aggregate ORDER BY and DISTINCT. Returns null if all inputs are null.

### image_stack

`image_stack(img, axis [ORDER BY ...])` -> Image

Concatenates non-null images in a group along `axis` -- a case-insensitive string enum: `'horizontal'` (output width = sum of widths, height = max of heights) or `'vertical'` (width = max, height = sum). Images with smaller perpendicular dimensions are centred; excess is transparent. The axis is captured from the first non-null row and must be constant across the group. Supports intra-aggregate ORDER BY for deterministic placement. Returns null when every image in the group is null.

```sql
-- Build a contact sheet, one row per label
SELECT label, image_stack(image, 'horizontal' ORDER BY id) AS strip
FROM images
GROUP BY label
```

### ARG_MAX

`ARG_MAX(value, key)` -> (matches value type)

Returns the `value` from the row where `key` is at its maximum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. Key must be a comparable type.

### ARG_MIN

`ARG_MIN(value, key)` -> (matches value type)

Returns the `value` from the row where `key` is at its minimum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. Key must be a comparable type.

## See Also

- [Window Functions](window.md) -- aggregates can also be used with OVER clauses
- [Table-Valued Functions](table-valued.md) -- functions that produce rows
- [SQL Reference -- GROUP BY](../sql/group-by.md) -- grouping syntax
