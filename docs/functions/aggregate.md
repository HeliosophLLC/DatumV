---
title: Aggregate Functions
category: aggregate
---

# Aggregate Functions

[< Back to Functions Reference](string.md) | [SQL Reference](../sql/select.md)

Aggregate functions reduce multiple rows into a single result per group. Used with `GROUP BY` or as global aggregations (see [SQL Reference -- GROUP BY](../sql/group-by.md)).

All aggregate functions support the `DISTINCT` modifier (e.g. `COUNT(DISTINCT expr)`, `SUM(DISTINCT expr)`), which deduplicates argument values before accumulation. The DISTINCT deduplication adds no additional Query Units. `COUNT(DISTINCT *)` is not supported -- use `COUNT(DISTINCT column)` instead.

### COUNT

`COUNT(*)` or `COUNT(expr)` -> Float32 | QU: 1

Count all rows (`*`) or non-null values (`expr`).

### SUM

`SUM(expr)` -> Float32 | QU: 1

Sum of non-null `Float32` values. Returns null if all inputs are null.

### AVG

`AVG(expr)` -> Float32 | QU: 1

Arithmetic mean of non-null `Float32` values. Nulls excluded from denominator.

### MIN

`MIN(expr)` -> Float32/UInt8/String/Date/DateTime/Time | QU: 1

Minimum value. Supports Float32, UInt8, String, Date, DateTime, Time.

### MAX

`MAX(expr)` -> Float32/UInt8/String/Date/DateTime/Time | QU: 1

Maximum value. Supports Float32, UInt8, String, Date, DateTime, Time.

### VARIANCE

`VARIANCE(expr)` -> Float32 | QU: 1

Sample variance (N-1 denominator). Alias for `VAR_SAMP`.

### VAR_SAMP

`VAR_SAMP(expr)` -> Float32 | QU: 1

Sample variance (N-1). Null for fewer than 2 values.

### VAR_POP

`VAR_POP(expr)` -> Float32 | QU: 1

Population variance (N denominator).

### STDDEV

`STDDEV(expr)` -> Float32 | QU: 1

Sample standard deviation (N-1). Alias for `STDDEV_SAMP`.

### STDDEV_SAMP

`STDDEV_SAMP(expr)` -> Float32 | QU: 1

Sample standard deviation (N-1). Null for fewer than 2 values.

### STDDEV_POP

`STDDEV_POP(expr)` -> Float32 | QU: 1

Population standard deviation (N denominator).

### MEDIAN

`MEDIAN(expr)` -> Float32 | QU: 2

Median (50th percentile) of non-null `Float32` values. Averages two middle values for even counts.

### PERCENTILE_CONT

`PERCENTILE_CONT(expr, fraction)` -> Float32 | QU: 2

Continuous percentile with linear interpolation. Fraction in [0, 1].

### PERCENTILE_DISC

`PERCENTILE_DISC(expr, fraction)` -> Float32 | QU: 2

Discrete percentile (nearest rank). Returns an actually observed value. Fraction in [0, 1].

### MODE

`MODE(expr)` -> Float32 | QU: 2

Most frequently occurring value. Ties broken by first occurrence. Works on any comparable type.

### CORR

`CORR(y, x)` -> Float32 | QU: 1

Pearson correlation coefficient between two numeric columns. Returns value in [-1, 1].

### COVAR_POP

`COVAR_POP(y, x)` -> Float32 | QU: 1

Population covariance (N denominator) between two numeric columns.

### COVAR_SAMP

`COVAR_SAMP(y, x)` -> Float32 | QU: 1

Sample covariance (N-1 denominator). Null for fewer than 2 pairs.

### APPROX_MEDIAN

`APPROX_MEDIAN(expr)` -> Float32 | QU: 2

Approximate median using reservoir sampling. O(1) memory, ~1-5% error for large groups.

### APPROX_PERCENTILE

`APPROX_PERCENTILE(expr, fraction)` -> Float32 | QU: 2

Approximate percentile using reservoir sampling. O(1) memory, ~1-5% error.

### STRING_AGG

`STRING_AGG(expr, separator [ORDER BY ...])` -> String | QU: 2

Concatenates non-null string values with a separator. Supports intra-aggregate ORDER BY.

### ARRAY_AGG

`ARRAY_AGG(expr [ORDER BY ...])` -> Array | QU: 1

Collects non-null values into a typed `Array`. Accepts any data kind. Supports intra-aggregate ORDER BY and DISTINCT. Returns null if all inputs are null.

### ARG_MAX

`ARG_MAX(value, key)` -> (matches value type) | QU: 1

Returns the `value` from the row where `key` is at its maximum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. Key must be a comparable type.

### ARG_MIN

`ARG_MIN(value, key)` -> (matches value type) | QU: 1

Returns the `value` from the row where `key` is at its minimum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. Key must be a comparable type.

## See Also

- [Window Functions](window.md) -- aggregates can also be used with OVER clauses
- [Table-Valued Functions](table-valued.md) -- functions that produce rows
- [SQL Reference -- GROUP BY](../sql/group-by.md) -- grouping syntax
