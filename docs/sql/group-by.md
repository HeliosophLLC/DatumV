---
title: GROUP BY
---

Group rows by one or more key expressions and compute aggregate results per group:

```sql
-- Count rows per category
SELECT category, COUNT(*) FROM products GROUP BY category

-- Multiple aggregates with aliases
SELECT department, COUNT(*) AS headcount, AVG(salary) AS avg_salary
FROM employees
GROUP BY department

-- Multiple grouping keys
SELECT department, status, SUM(amount) AS total
FROM orders
GROUP BY department, status

-- Global aggregation (no GROUP BY — entire table is one group)
SELECT COUNT(*), SUM(price), AVG(quantity), MIN(price), MAX(price) FROM orders
```

### GROUP BY ALL

`GROUP BY ALL` automatically infers grouping keys from the SELECT list. Every non-aggregate expression in the column list becomes a grouping key, eliminating the need to repeat column names:

```sql
-- Equivalent to: GROUP BY category, region
SELECT category, region, SUM(amount) AS total, COUNT(*) AS orders
FROM sales
GROUP BY ALL

-- Works with single keys
SELECT department, AVG(salary) AS avg_salary
FROM employees
GROUP BY ALL

-- Works with HAVING, ORDER BY, and LIMIT
SELECT category, SUM(revenue) AS total_revenue
FROM products
GROUP BY ALL
HAVING SUM(revenue) > 1000
ORDER BY total_revenue DESC
LIMIT 10
```

LET bindings that reference non-aggregate expressions and have an output alias (via `AS`) are also included as grouping keys. Expressions that contain aggregate function calls (`COUNT`, `SUM`, `AVG`, etc.) are excluded automatically.

### Aggregate Functions

| Function | Description |
|----------|-------------|
| `COUNT(*)` | Count all rows in the group. |
| `COUNT(expr)` | Count non-null values of `expr`. |
| `COUNT(DISTINCT expr)` | Count distinct non-null values of `expr`. |
| `SUM(expr)` | Sum of all non-null values. Returns null if all values are null. |
| `SUM(DISTINCT expr)` | Sum of distinct non-null values. |
| `AVG(expr)` | Arithmetic mean of non-null values. Returns null if all values are null. |
| `AVG(DISTINCT expr)` | Mean of distinct non-null values. |
| `MIN(expr)` | Minimum value. Works on Float32, UInt8, String, Date, DateTime, Time. |
| `MAX(expr)` | Maximum value. Works on Float32, UInt8, String, Date, DateTime, Time. |
| `VARIANCE(expr)` | Sample variance (N−1 denominator) of non-null values. Alias for `VAR_SAMP`. |
| `VAR_SAMP(expr)` | Sample variance (N−1). Returns null for fewer than 2 values. |
| `VAR_POP(expr)` | Population variance (N denominator) of non-null values. |
| `STDDEV(expr)` | Sample standard deviation (N−1). Alias for `STDDEV_SAMP`. |
| `STDDEV_SAMP(expr)` | Sample standard deviation (N−1). Returns null for fewer than 2 values. |
| `STDDEV_POP(expr)` | Population standard deviation (N denominator) of non-null values. |
| `MEDIAN(expr)` | Median (50th percentile). Averages two middle values for even counts. |
| `PERCENTILE_CONT(expr, fraction)` | Continuous percentile with linear interpolation. Fraction in [0, 1]. Accepts any numeric kind; returns Float64. |
| `PERCENTILE_CONT(fraction) WITHIN GROUP (ORDER BY expr [ASC\|DESC])` | SQL standard ordered-set form of `PERCENTILE_CONT`. Equivalent to `PERCENTILE_CONT(expr, fraction)`. |
| `PERCENTILE_DISC(expr, fraction)` | Discrete percentile (nearest rank). Returns an observed value. Fraction in [0, 1]. Accepts any numeric kind; returns Float64. |
| `PERCENTILE_DISC(fraction) WITHIN GROUP (ORDER BY expr [ASC\|DESC])` | SQL standard ordered-set form of `PERCENTILE_DISC`. Equivalent to `PERCENTILE_DISC(expr, fraction)`. |
| `MODE(expr)` | Most frequently occurring value. Ties broken by first occurrence. |
| `MODE() WITHIN GROUP (ORDER BY expr [ASC\|DESC])` | Ordered-set form: most frequently occurring value of `expr`, with tie-breaking by insertion order. Picks the same result as `MODE(expr)` — the WITHIN GROUP form is provided for SQL standard parity. |
| `CORR(y, x)` | Pearson correlation coefficient. Returns value in [−1, 1]. |
| `COVAR_POP(y, x)` | Population covariance (N denominator). |
| `COVAR_SAMP(y, x)` | Sample covariance (N−1 denominator). Null for fewer than 2 pairs. |
| `APPROX_MEDIAN(expr)` | Approximate median via reservoir sampling. O(1) memory, ~1–5% error. |
| `APPROX_PERCENTILE(expr, fraction)` | Approximate percentile via reservoir sampling. O(1) memory, ~1–5% error. |
| `STRING_AGG(expr, separator [ORDER BY expr [ASC|DESC]])` | Concatenates non-null strings with a separator. Supports intra-aggregate ORDER BY. |
| `ARRAY_AGG(expr [ORDER BY expr [ASC|DESC]])` | Collects non-null values into a typed Array. Accepts any data kind. Supports intra-aggregate ORDER BY and DISTINCT. Returns null if all inputs are null. |
| `ARG_MAX(value, key)` | Returns the `value` from the row where `key` is at its maximum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. |
| `ARG_MIN(value, key)` | Returns the `value` from the row where `key` is at its minimum. Null keys are skipped. Ties broken by first-encountered row. Supports intra-aggregate ORDER BY for deterministic tie-breaking. |

The `DISTINCT` modifier deduplicates argument values before accumulation. It is supported on all aggregate functions. Note that `COUNT(DISTINCT *)` is not supported — use `COUNT(DISTINCT column)` instead. DISTINCT in window function aggregates (`COUNT(DISTINCT x) OVER (...)`) is not currently supported.

### Statistical and Bivariate Aggregates

```sql
-- Standard deviation and variance per group
SELECT category, STDDEV(price) AS price_stddev, VAR_POP(price) AS price_var
FROM products
GROUP BY category

-- Median and percentiles
SELECT department, MEDIAN(salary) AS median_sal,
       PERCENTILE_CONT(salary, 0.9) AS p90_sal
FROM employees
GROUP BY department

-- Ordered-set form (SQL standard) — equivalent to the two-argument form above.
-- Works on any numeric column kind (Int32, Int64, Float64, etc.)
SELECT department,
       PERCENTILE_DISC(0.5) WITHIN GROUP (ORDER BY salary) AS median_salary,
       PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY salary) AS p90_salary
FROM employees
GROUP BY department

-- Correlation and covariance between two columns
SELECT CORR(height, weight) AS r,
       COVAR_SAMP(height, weight) AS cov
FROM measurements

-- Per-group correlation
SELECT species, CORR(petal_length, sepal_length) AS r
FROM iris
GROUP BY species

-- Most frequent value per group
SELECT region, MODE(payment_method) AS most_common_payment FROM orders GROUP BY region

-- Same result using the ordered-set (WITHIN GROUP) form — SQL standard syntax
SELECT user_id, MODE() WITHIN GROUP (ORDER BY order_hour_of_day) AS preferred_hour
FROM orders
GROUP BY user_id

-- Concatenate labels per group
SELECT category, STRING_AGG(name, ', ' ORDER BY name ASC) AS items
FROM products
GROUP BY category

-- Collect values into arrays per group
SELECT category, ARRAY_AGG(name ORDER BY name ASC) AS names
FROM products
GROUP BY category

-- Return product name with highest score per category
SELECT category, ARG_MAX(name, score) AS best_product
FROM products
GROUP BY category

-- Return cheapest product per department
SELECT department, ARG_MIN(name, price) AS cheapest_product
FROM products
GROUP BY department

-- Deterministic tie-breaking with ORDER BY
SELECT category, ARG_MAX(name, score ORDER BY id ASC) AS best_product
FROM products
GROUP BY category

-- Array functions: construct, inspect, search, join
SELECT array(1, 2, 3) AS literal_array,
       cardinality(ARRAY_AGG(name)) AS name_count,
       array_contains(ARRAY_AGG(tag), 'urgent') AS has_urgent,
       array_join(ARRAY_AGG(name ORDER BY name ASC), ', ') AS name_list
FROM products
GROUP BY category

-- Array manipulation: sort, deduplicate, slice, concatenate, reverse
SELECT array_sort(ARRAY_AGG(score)) AS sorted_scores,
       array_distinct(ARRAY_AGG(tag)) AS unique_tags,
       array_slice(ARRAY_AGG(name ORDER BY name ASC), 1, 3) AS top3_names,
       array_concat(ARRAY_AGG(tag), array('extra')) AS tags_extended,
       array_reverse(ARRAY_AGG(name ORDER BY name ASC)) AS names_desc
FROM products
GROUP BY category

-- Array element access and numeric reduction (Tier 3)
SELECT array_get(ARRAY_AGG(name ORDER BY score DESC), 1) AS top_name,
       array_min(ARRAY_AGG(score)) AS min_score,
       array_max(ARRAY_AGG(score)) AS max_score,
       array_sum(ARRAY_AGG(score)) AS total_score,
       array_avg(ARRAY_AGG(score)) AS avg_score
FROM products
GROUP BY category

-- Approximate percentile for large groups (O(1) memory)
SELECT category, APPROX_PERCENTILE(score, 0.95) AS approx_p95
FROM reviews
GROUP BY category
```

### HAVING

Filter groups after aggregation. HAVING operates on aggregate results, while WHERE filters individual rows before grouping:

```sql
-- Only categories with more than 5 products
SELECT category, COUNT(*) AS cnt
FROM products
GROUP BY category
HAVING COUNT(*) > 5

-- Combine WHERE (pre-filter) and HAVING (post-filter)
SELECT category, AVG(price) AS avg_price
FROM products
WHERE price > 0
GROUP BY category
HAVING AVG(price) > 100
```

### Execution model

GROUP BY uses hash-based aggregation: all groups are accumulated in memory using a hash table keyed by the GROUP BY expressions. This is a blocking operator — all input rows must be consumed before any output rows are emitted. When a memory budget is configured and the in-memory accumulators exceed the budget, incoming rows for new groups are hash-partitioned to 64 temporary spill files. Known in-memory groups continue to accumulate normally. In a drain phase, spilled partitions are re-aggregated one at a time, ensuring arbitrarily large group-by operations complete without out-of-memory failures.

## See Also

- [SELECT](select.md)
- [WHERE](filtering.md)
- [Window Functions](window-functions.md)
