---
title: Window Functions
---

Window functions compute a value for each row based on a "window" of related rows, without collapsing groups like GROUP BY. Every window function call requires an `OVER` clause.

```sql
-- Basic syntax
function_name(args) OVER (
    [PARTITION BY expr, ...]
    [ORDER BY expr [ASC|DESC], ...]
    [ROWS BETWEEN frame_start AND frame_end]
)
```

### Ranking Functions

```sql
-- Sequential numbering within each group
SELECT *, ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) AS rn
FROM data

-- Rank with gaps on ties (1, 1, 3, 4)
SELECT *, RANK() OVER (ORDER BY score DESC) AS rnk FROM data

-- Rank without gaps on ties (1, 1, 2, 3)
SELECT *, DENSE_RANK() OVER (ORDER BY score DESC) AS drnk FROM data

-- Distribute rows into N equal buckets
SELECT *, NTILE(4) OVER (ORDER BY score) AS quartile FROM data
```

### Offset Functions

```sql
-- Previous row's value (default offset = 1)
SELECT *, LAG(price) OVER (ORDER BY date) AS prev_price FROM prices

-- Next row's value with custom offset and default
SELECT *, LEAD(price, 2, 0) OVER (ORDER BY date) AS price_after_next FROM prices
```

### Value Functions

```sql
-- First value per partition
SELECT *, FIRST_VALUE(price) OVER (PARTITION BY product ORDER BY date) AS first_price
FROM prices

-- Last value with explicit frame (default frame returns current row)
SELECT *, LAST_VALUE(price) OVER (
    PARTITION BY product ORDER BY date
    ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING
) AS last_price
FROM prices

-- Nth value from the end of the frame
SELECT *, NTH_VALUE(reading, 2) FROM LAST OVER (ORDER BY ts) AS second_to_last
FROM sensor_data

-- First non-null value (skip nulls)
SELECT *, FIRST_VALUE(status) IGNORE NULLS OVER (ORDER BY ts) AS first_status
FROM events
```

Value functions support two optional modifiers between the closing `)` and `OVER`:

| Modifier | Applies to | Description |
|----------|-----------|-------------|
| `FROM FIRST` (default) | `NTH_VALUE` | Count from the beginning of the frame. |
| `FROM LAST` | `NTH_VALUE` | Count from the end of the frame. |
| `RESPECT NULLS` (default) | All three | Include NULL values when searching. |
| `IGNORE NULLS` | All three | Skip NULL values when searching. |

### Aggregate Functions over Windows

All single-argument aggregate functions (COUNT, SUM, AVG, MIN, MAX, VARIANCE, STDDEV, MEDIAN, MODE, PERCENTILE_CONT, and their variants) can be used with OVER to compute running or partitioned aggregates. Two-argument aggregates (CORR, COVAR_POP, COVAR_SAMP, ARG_MAX, ARG_MIN), STRING_AGG, and ARRAY_AGG are not supported as window functions.

```sql
-- Running total
SELECT *, SUM(amount) OVER (ORDER BY date ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_total
FROM transactions

-- Per-category average alongside each row
SELECT *, AVG(score) OVER (PARTITION BY category) AS category_avg
FROM data

-- 3-row moving average
SELECT *, AVG(value) OVER (ORDER BY ts ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS moving_avg
FROM sensor_data
```

### Window Frames

Window frames control which rows within the partition are visible to the function. Only `ROWS` frames are supported.

| Bound | Meaning |
|-------|---------|
| `UNBOUNDED PRECEDING` | First row of the partition. |
| `N PRECEDING` | N rows before the current row. |
| `CURRENT ROW` | The current row. |
| `N FOLLOWING` | N rows after the current row. |
| `UNBOUNDED FOLLOWING` | Last row of the partition. |

```sql
-- Default frame (when ORDER BY is present): ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
-- Default frame (when ORDER BY is absent): entire partition

-- Explicit frame: 3-row trailing window
SUM(val) OVER (ORDER BY ts ROWS BETWEEN 2 PRECEDING AND CURRENT ROW)

-- Full partition frame
SUM(val) OVER (PARTITION BY group_col ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)
```

### Window Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `ROW_NUMBER()` | `ROW_NUMBER() OVER (...)` | Sequential integer starting at 1 per partition. |
| `RANK()` | `RANK() OVER (...)` | Rank with gaps on ties. Requires ORDER BY. |
| `DENSE_RANK()` | `DENSE_RANK() OVER (...)` | Rank without gaps on ties. Requires ORDER BY. |
| `NTILE(n)` | `NTILE(n) OVER (...)` | Distribute rows into `n` roughly equal buckets. |
| `LAG(expr [, offset [, default]])` | `LAG(...) OVER (...)` | Value from the row `offset` rows before current (default 1). |
| `LEAD(expr [, offset [, default]])` | `LEAD(...) OVER (...)` | Value from the row `offset` rows after current (default 1). |
| `FIRST_VALUE(expr)` | `FIRST_VALUE(expr) [IGNORE NULLS] OVER (...)` | First value in the window frame. |
| `LAST_VALUE(expr)` | `LAST_VALUE(expr) [IGNORE NULLS] OVER (...)` | Last value in the window frame. |
| `NTH_VALUE(expr, n)` | `NTH_VALUE(expr, n) [FROM FIRST \| FROM LAST] [IGNORE NULLS] OVER (...)` | Nth value (1-based) in the window frame. |
| `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`, and variants | `agg(...) OVER (...)` | Any single-argument aggregate function used with OVER becomes a window aggregate. Includes VARIANCE, STDDEV, MEDIAN, MODE, PERCENTILE_CONT, PERCENTILE_DISC, and APPROX_ variants. Two-argument aggregates (CORR, COVAR_POP, COVAR_SAMP, ARG_MAX, ARG_MIN), STRING_AGG, and ARRAY_AGG are excluded. |

### Execution model

Window functions are blocking operators — all input rows within each partition must be materialized before results are computed. Rows are hash-partitioned by PARTITION BY expressions, sorted within each partition by ORDER BY, and then each window function is evaluated. The original row order is preserved in the output.

## See Also

- [SELECT](select.md)
- [GROUP BY](group-by.md)
- [LET Bindings](let-bindings.md)
