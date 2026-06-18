---
title: Window Functions
category: window
---

# Window Functions

[< Back to Functions Reference](string.md) | [SQL Reference](../sql/select.md)

Window functions compute a value for each row based on a window of related rows defined by an `OVER` clause. Unlike aggregates with `GROUP BY`, window functions do not collapse rows -- every input row produces an output row. See [SQL Reference -- Window Functions](../sql/window-functions.md) for full syntax.

### ROW_NUMBER

`ROW_NUMBER() OVER (...)` -> Float32

Sequential integer 1..N per partition.

### RANK

`RANK() OVER (...)` -> Float32

Rank with gaps on ties (1, 1, 3). Requires ORDER BY.

### DENSE_RANK

`DENSE_RANK() OVER (...)` -> Float32

Rank without gaps (1, 1, 2). Requires ORDER BY.

### NTILE

`NTILE(n) OVER (...)` -> Float32

Distribute rows into `n` roughly equal buckets.

### LAG

`LAG(expr [, offset [, default]]) OVER (...)` -> (matches expr type)

Value from `offset` rows before current (default offset 1, default value NULL).

### LEAD

`LEAD(expr [, offset [, default]]) OVER (...)` -> (matches expr type)

Value from `offset` rows after current (default offset 1, default value NULL).

### FIRST_VALUE

`FIRST_VALUE(expr) [IGNORE NULLS] OVER (...)` -> (matches expr type)

Value from the first row in the window frame. `IGNORE NULLS` skips null values.

### LAST_VALUE

`LAST_VALUE(expr) [IGNORE NULLS] OVER (...)` -> (matches expr type)

Value from the last row in the window frame. `IGNORE NULLS` skips null values.

### NTH_VALUE

`NTH_VALUE(expr, n) [FROM FIRST | FROM LAST] [IGNORE NULLS] OVER (...)` -> (matches expr type)

Value from the Nth row (1-based) in the window frame. `FROM LAST` counts from the end.

## Aggregates as Window Functions

All single-argument aggregate functions (COUNT, SUM, AVG, MIN, MAX, VARIANCE, VAR_SAMP, VAR_POP, STDDEV, STDDEV_SAMP, STDDEV_POP, MEDIAN, MODE, PERCENTILE_CONT, PERCENTILE_DISC, APPROX_MEDIAN, APPROX_PERCENTILE) can also be used with an OVER clause to produce windowed results instead of grouped results. Two-argument aggregates (CORR, COVAR_POP, COVAR_SAMP, ARG_MAX, ARG_MIN), STRING_AGG, and ARRAY_AGG are not supported as window functions.

```sql
-- Running sum
SELECT SUM(amount) OVER (ORDER BY date ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM data

-- Partition-level average alongside each row
SELECT *, AVG(score) OVER (PARTITION BY category) AS category_avg FROM data
```

## See Also

- [Aggregate Functions](aggregate.md) -- full list of aggregate functions
- [Table-Valued Functions](table-valued.md) -- functions that produce rows
- [SQL Reference -- Window Functions](../sql/window-functions.md) -- full window syntax
