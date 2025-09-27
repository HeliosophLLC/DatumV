# SQL Reference

[← Back to README](../README.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Star Schema](star-schema.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest supports a subset of SQL designed for ML dataset ETL: SELECT, SELECT DISTINCT, SELECT * EXCEPT, SELECT * REPLACE, FROM, JOIN (including LATERAL / APPLY), WHERE, GROUP BY, GROUP BY ALL, HAVING, window functions (OVER/PARTITION BY), SCAN (fold/prefix-scan), QUALIFY, ASSERT, DEFINE, PIVOT, UNPIVOT, INTO, ORDER BY, LIMIT, OFFSET, subqueries, Common Table Expressions (WITH / WITH RECURSIVE), set operations (UNION, INTERSECT, EXCEPT), and DDL/DML for session-scoped temp tables (CREATE TEMP TABLE, INSERT INTO, UPDATE, DELETE, ALTER TABLE, DROP TABLE, ANALYZE).

## Comments

Line comments start with `--` and continue to the end of the line. Block comments are enclosed in `/* ... */`. Both styles are stripped during tokenization and may appear anywhere whitespace is allowed.

```sql
-- This is a line comment
SELECT
    col1,           -- inline comment
    /* col2, */     -- block comment can disable code
    col3
FROM my_table
```

Block comments do not nest.

## SELECT

```sql
SELECT *
SELECT col1, col2, col3
SELECT a.col1, b.col2
SELECT col1 AS alias, normalize(col2) AS norm_col
SELECT table_alias.*
SELECT * EXCEPT (col1, col2)
SELECT * REPLACE (upper(name) AS name)
SELECT t.* EXCEPT (id) REPLACE (score * 100 AS score)
```

### SELECT * EXCEPT

Exclude specific columns from wildcard expansion. Applies to both `*` and `table.*`:

```sql
-- All columns except the raw image blob
SELECT * EXCEPT (image_bytes) FROM training_data

-- Exclude from a specific table in a join
SELECT a.* EXCEPT (id), b.label
FROM features a JOIN labels b ON a.id = b.id
```

Column names in the EXCEPT list must exist in the expanded column set; unknown names produce a planner error.

### SELECT * REPLACE

Replace specific columns in wildcard expansion with new expressions. The alias must match an existing column name — the replacement takes the original column's position:

```sql
-- Normalize a column in-place
SELECT * REPLACE (normalize(score, 0, 100) AS score) FROM data

-- Replace multiple columns
SELECT * REPLACE (upper(name) AS name, round(price) AS price) FROM products

-- Combine EXCEPT and REPLACE on the same wildcard
SELECT * EXCEPT (raw_data) REPLACE (trim(name) AS name) FROM records
```

EXCEPT is applied before REPLACE — you cannot replace a column that has been excluded. REPLACE aliases that do not match any column in the expansion produce a planner error.

### SELECT DISTINCT

Eliminate duplicate rows from the result set. Deduplication is based on all projected columns:

```sql
-- Unique categories
SELECT DISTINCT category FROM products

-- Unique combinations
SELECT DISTINCT department, status FROM employees
```

`SELECT DISTINCT` uses a streaming hash-based operator. When a memory budget is configured and the in-memory set exceeds the budget, the operator spills unseen rows to hash-partitioned temporary files and deduplicates them in a drain phase. This adds no Query Units (0 QU).

**ORDER BY constraint:** When `SELECT DISTINCT` is combined with `ORDER BY`, every `ORDER BY` expression must appear in the `SELECT` list. This avoids ambiguity because DISTINCT collapses rows before sorting.

```sql
-- Valid: ORDER BY column is in SELECT list
SELECT DISTINCT name FROM users ORDER BY name

-- Invalid: age is not projected
SELECT DISTINCT name FROM users ORDER BY age
```

### LET Bindings

`LET` declares named, memoized intermediate expressions in the SELECT list. Each binding is evaluated once per row and its value is cached for all subsequent references. LET bindings are **not included in the output** unless explicitly aliased with `AS`.

```sql
SELECT
  LET tensor = image_to_tensor_hwc(image),
  LET features = reshape(tensor, 16, 16),
  rank(tensor) AS ndim,
  vec_mean(features) AS average
FROM data
-- Result columns: ndim, average
```

#### Syntax

```
LET <identifier> = <expression> [AS <alias>]
LET (<name1>, <name2>, ...) = <expression>
LET {<field1>, <field2>, ...} = <expression>
```

- **`<identifier>`** — the binding name, used to reference the cached value in later LET bindings and SELECT columns.
- **`AS <alias>`** — when present, the LET value appears in the output with this column name.
- All LET bindings must precede regular output columns in the SELECT list.
- Later LET bindings can reference earlier ones (left-to-right chaining).
- `SELECT *` expands source table columns only — never includes LET bindings (aliased or not).

#### Output visibility

By default, LET bindings are hidden from the result set. To include a binding's value in the output, add `AS <alias>`:

```sql
SELECT
  LET total = price * quantity AS "line_total",  -- output as "line_total"
  LET tax = total * 0.08,                        -- hidden from output
  total + tax AS final_price
FROM orders
-- Result columns: line_total, final_price
```

#### Tuple Destructuring

A LET binding can unpack a multi-valued result into several named variables in one step.

**Positional** — extracts by zero-based index. Supported on Array, Vector, and Struct:

```sql
-- Unpack a 2-element Vector (e.g. cyclical_encode output)
SELECT LET (sin_v, cos_v) = cyclical_encode(month, 12),
       sin_v AS s, cos_v AS c
FROM events

-- Unpack a float array column
SELECT LET (r, g, b) = pixel, r, g, b FROM images
```

**Named** — extracts by field name. Supported on Struct only; field order in the pattern is independent of the struct's declaration order:

```sql
-- Extract named fields from a struct literal
SELECT LET {alpha, beta} = {beta: 8.0, alpha: 7.0},
       alpha AS av, beta AS bv
FROM data

-- Named destructure of a scalar LET alias
SELECT LET s = {score: 0.9, label: 'cat'},
       LET {score, label} = s,
       score, label
FROM predictions
```

The source expression is evaluated **once per row** regardless of how many names are extracted. Destructured names are plain LET bindings and can be used in subsequent LET expressions. Named destructuring on a Vector or Array is a runtime error — use positional destructuring instead.

#### Memoization

LET expressions are computed once per row. This is value caching, not textual macro expansion. Functions with side-effects like `uuidv4()` produce a single value that is reused for all references within the same row:

```sql
SELECT
  LET id = uuidv4(),
  uuid_str(id) AS first,
  uuid_str(id) AS second
FROM data
-- first and second are always identical for each row
```

#### Clause interactions

| Clause | Can reference LET bindings? | Notes |
|---|---|---|
| WHERE | No | Evaluated before SELECT |
| JOIN ON | No | Evaluated before SELECT |
| GROUP BY | — | LET expressions follow the same rules as SELECT expressions: must be aggregates or grouping keys |
| HAVING | No | Evaluated before SELECT |
| QUALIFY | Yes | LET references are resolved via expression substitution |
| ASSERT | Yes | Evaluated after SELECT projection against the projected row |
| ORDER BY | Yes | Can reference aliased LET output column names |

## FROM

```sql
FROM table_name
FROM table_name AS alias
FROM (SELECT ... FROM ...) AS subquery
FROM RANGE(0, 360) AS r
```

### Quoted table names

Table names that contain spaces, hyphens, start with a digit, or collide with SQL keywords must be quoted. Two quoting styles are supported:

```sql
-- Double-quoted (recommended)
SELECT * FROM "my table"

-- Single-quoted
SELECT * FROM 'my table'
```

Most table names derived from filenames are valid bare identifiers (e.g. `orders_csv`, `data_json`) and do not need quoting. The `.tables` command and tab completion automatically double-quote names that need it, so you can copy them directly into your SQL.

Table-valued functions can be used as data sources in FROM and JOIN clauses:

```sql
-- Generate rows with Value 0 through 360 (inclusive)
SELECT r."Value" FROM RANGE(0, 360) AS r

-- With a custom step
SELECT r."Value" FROM RANGE(0, 1, 0.1) AS r

-- Compute a sine wave
SELECT r."Value" AS x, ((SIN(2.0 * PI() * r."Value") + 1.0) / 2.0) AS y
FROM RANGE(0, 360) AS r

-- Use in a CROSS JOIN
SELECT t.name, r."Value" AS angle
FROM data AS t
CROSS JOIN RANGE(0, 360) AS r
```

## JOIN

All five standard join types are supported:

```sql
-- INNER JOIN: only matching rows
SELECT * FROM a INNER JOIN b ON a.id = b.id

-- LEFT JOIN: all rows from left, matching from right
SELECT * FROM a LEFT JOIN b ON a.id = b.id

-- RIGHT JOIN: all rows from right, matching from left
SELECT * FROM a RIGHT JOIN b ON a.id = b.id

-- FULL OUTER JOIN: all rows from both sides
SELECT * FROM a FULL OUTER JOIN b ON a.id = b.id

-- CROSS JOIN: cartesian product
SELECT * FROM a CROSS JOIN b
```

NULL keys never match (SQL three-valued logic). Hash join is used for INNER/LEFT/RIGHT/FULL OUTER; nested loop for CROSS.

### LATERAL JOIN / APPLY

A **lateral join** re-executes the right-hand source for every row from the left side, allowing the right side to reference columns from the left. The explicit `LATERAL` keyword is required after `CROSS JOIN` or `LEFT [OUTER] JOIN`. The T-SQL `CROSS APPLY` and `OUTER APPLY` syntax is also supported.

```sql
-- CROSS JOIN LATERAL: expand array column per row (no match → row excluded)
SELECT t.name, s.value
FROM data AS t
CROSS JOIN LATERAL UNNEST(t.scores) AS s

-- LEFT JOIN LATERAL: preserve rows with empty arrays (NULL-padded)
SELECT t.name, s.value
FROM data AS t
LEFT JOIN LATERAL UNNEST(t.scores) AS s

-- Lateral subquery: correlated derived table referencing outer columns
SELECT o.customer, sub.product
FROM orders AS o
LEFT JOIN LATERAL (
    SELECT i.product FROM items AS i WHERE i.order_id = o.id
) AS sub ON 1 = 1

-- T-SQL CROSS APPLY (equivalent to CROSS JOIN LATERAL)
SELECT t.name, s.value
FROM data AS t
CROSS APPLY UNNEST(t.scores) AS s

-- T-SQL OUTER APPLY (equivalent to LEFT JOIN LATERAL)
SELECT t.name, s.value
FROM data AS t
OUTER APPLY UNNEST(t.scores) AS s
```

LATERAL is supported with `CROSS JOIN` and `LEFT [OUTER] JOIN` only. The right-hand source can be a table-valued function or a subquery.

> **Performance:** Lateral joins use O(N × M) nested-loop execution — the right side is re-executed for each left row. No hash acceleration is possible. For large left-side tables, consider filtering the left side before the lateral join.

## WHERE

```sql
WHERE col > 10
WHERE col1 = 'value' AND col2 < 100
WHERE col IN ('a', 'b', 'c')
WHERE col BETWEEN 10 AND 50
WHERE col LIKE 'prefix_%'
WHERE col ILIKE '%pattern%'
WHERE col REGEXP '^\d{3}-\d{4}$'
WHERE col IS NULL
WHERE col IS NOT NULL
WHERE col IS Int32
WHERE col IS NOT String
WHERE NOT (col1 > 10 OR col2 < 5)
```

Supported operators: `=`, `!=`, `<`, `>`, `<=`, `>=`, `AND`, `OR`, `NOT`, `LIKE`, `ILIKE`, `REGEXP`, `IN`, `BETWEEN`, `IS NULL`, `IS NOT NULL`, `IS Type`, `IS NOT Type`.

### Pattern matching

`LIKE` performs case-sensitive pattern matching with `%` (zero or more characters) and `_` (exactly one character) wildcards. `ILIKE` is the case-insensitive variant.

`REGEXP` matches against a .NET regular expression. The match is unanchored (substring match) — use `^` and `$` anchors for full-string matching. Case-sensitive by default; use inline `(?i)` for case-insensitive matching.

```sql
-- Case-sensitive wildcard matching
SELECT * FROM logs WHERE message LIKE 'ERROR:%'

-- Case-insensitive wildcard matching
SELECT * FROM users WHERE name ILIKE '%smith%'

-- Regular expression matching
SELECT * FROM data WHERE phone REGEXP '^\d{3}-\d{4}$'
SELECT * FROM logs WHERE line REGEXP '(?i)warning|error'
```

All three operators support negation via `NOT`: `NOT LIKE`, `NOT ILIKE`, `NOT REGEXP`.

### ESCAPE clause

By default `%` and `_` are wildcards in `LIKE` / `ILIKE` patterns. Use the `ESCAPE` clause to designate a character that causes the next `%` or `_` to be treated as a literal:

```sql
-- Match strings containing a literal percent sign
SELECT * FROM data WHERE value LIKE '%100\%' ESCAPE '\'

-- Match strings starting with an underscore (case-insensitive)
SELECT * FROM users WHERE name ILIKE '\_%' ESCAPE '\'
```

The escape character must be a single character. It only affects the immediately following `%` or `_`.

## GROUP BY / Aggregation

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
       array_length(ARRAY_AGG(name)) AS name_count,
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

## Window Functions

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

## QUALIFY

Filter rows based on the result of a window function, without needing a subquery wrapper. QUALIFY is evaluated after window functions but before SELECT projection.

```sql
-- Syntax
SELECT ...
FROM table
QUALIFY <predicate>
```

### Referencing window aliases

QUALIFY can reference aliases defined in the SELECT list:

```sql
SELECT
    department,
    employee,
    salary,
    ROW_NUMBER() OVER (PARTITION BY department ORDER BY salary DESC) AS rn
FROM employees
QUALIFY rn <= 3
```

### Inline window functions

QUALIFY also supports inline window function calls that do not appear in the SELECT list. The window function is computed internally and the synthetic column is stripped from the output:

```sql
SELECT department, employee, salary
FROM employees
QUALIFY ROW_NUMBER() OVER (PARTITION BY department ORDER BY salary DESC) <= 3
```

### Combining with other clauses

QUALIFY slots into the pipeline between HAVING and ORDER BY:

```
FROM → JOIN → WHERE → GROUP BY → HAVING → Window → SCAN → QUALIFY → SELECT → DISTINCT → ORDER BY → LIMIT
```

All clauses can coexist:

```sql
SELECT department, SUM(salary) AS total,
       ROW_NUMBER() OVER (ORDER BY department) AS rn
FROM employees
WHERE status = 'active'
GROUP BY department
HAVING COUNT(*) > 1
QUALIFY rn = 1
ORDER BY total DESC
LIMIT 10
```

### QUALIFY vs. subquery

QUALIFY eliminates the common subquery pattern for window-function filtering:

```sql
-- Without QUALIFY (subquery wrapper)
SELECT * FROM (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) AS rn
    FROM data
) sub
WHERE rn <= 5

-- With QUALIFY (equivalent, no subquery)
SELECT *, ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) AS rn
FROM data
QUALIFY rn <= 5
```

### Execution model

QUALIFY is a streaming filter (0 query units) — it applies a FilterOperator to each row after window function computation. The window function itself remains a blocking operator, but the QUALIFY predicate adds zero additional memory or cost.

## ASSERT

ASSERT validates a predicate against every projected row. Unlike WHERE (which filters silently before projection), ASSERT runs after projection and can abort the query, skip failing rows, or emit diagnostic warnings depending on the configured failure mode.

### Syntax

```sql
SELECT columns
FROM table
ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN]
```

Multiple ASSERT clauses may follow a single SELECT; they are evaluated left-to-right and all must pass (or be configured to skip/warn):

```sql
SELECT id, amount, name FROM orders
ASSERT amount > 0     MESSAGE 'amount must be positive'   ON FAIL SKIP
ASSERT name IS NOT NULL                                   ON FAIL WARN
```

### Failure modes

| Mode | Behavior |
|------|----------|
| `ABORT` (default) | Throws immediately. No further rows are produced. |
| `SKIP` | Omits the failing row from the output silently. |
| `WARN` | Keeps the row in the output and records a diagnostic. |

### MESSAGE

The optional `MESSAGE` expression provides a human-readable failure description. It may reference any column in the projected row, including LET bindings:

```sql
SELECT id, amount FROM orders
ASSERT amount > 0 MESSAGE CONCAT('bad amount on order ', CAST(id AS VARCHAR))
```

### Interaction with LET and QUALIFY

ASSERT runs after SELECT projection (including LET evaluation). This means ASSERT predicates may reference computed LET bindings directly:

```sql
SELECT LET total = price * qty, id, price, qty, total FROM line_items
ASSERT total >= 0 MESSAGE 'negative total'
```

QUALIFY is a pre-projection window filter; ASSERT is a post-projection row validator. The pipeline order is:

```
FROM → JOIN → WHERE → GROUP BY → HAVING → Window → SCAN → QUALIFY → SELECT (LET) → ASSERT → DISTINCT → ORDER BY → LIMIT
```

### Execution model

ASSERT is a streaming pass (0 query units). Each row is checked individually; no buffering occurs. ABORT mode short-circuits the entire pipeline on the first failure.

## DEFINE

DEFINE is syntactic sugar that groups LET bindings and ASSERT clauses inside a brace-delimited block placed directly after SELECT. It is purely a readability aid — at parse time the block is flattened into the query's LET bindings and ASSERT clauses.

### Syntax

```sql
SELECT DEFINE {
    LET name = expression [AS alias];
    ASSERT predicate [MESSAGE expression] [ON FAIL ABORT | SKIP | WARN];
} columns
FROM table
```

Declarations inside the block are separated by semicolons (trailing semicolon before `}` is optional). LET and ASSERT declarations may appear in any order inside the block; all LET bindings are evaluated before any ASSERT is checked, regardless of declaration order.

### Examples

```sql
-- All definitions grouped at the top for readability
SELECT DEFINE {
    LET tax      = amount * 0.1;
    LET subtotal = amount - discount;
    ASSERT amount > 0    MESSAGE 'amount must be positive'  ON FAIL SKIP;
    ASSERT discount >= 0 MESSAGE 'discount cannot be negative';
} id, amount, discount, subtotal, tax
FROM orders
```

Destructuring bindings work inside DEFINE blocks too:

```sql
-- Unpack a Vector result and validate components in the same block
SELECT DEFINE {
    LET (sin_m, cos_m) = cyclical_encode(month, 12);
    ASSERT sin_m BETWEEN -1.0 AND 1.0 ON FAIL WARN;
} sin_m AS s, cos_m AS c
FROM events

-- Named destructuring with an ASSERT guard
SELECT DEFINE {
    LET {lo, hi} = bounds_column;
    ASSERT hi > lo MESSAGE 'inverted bounds' ON FAIL SKIP;
} lo, hi
FROM ranges
```

### Equivalence to inline LET + trailing ASSERT

These two queries are identical:

```sql
-- DEFINE block form
SELECT DEFINE {
    LET total = price * qty;
    ASSERT total > 0 ON FAIL SKIP;
} total
FROM line_items

-- Equivalent inline form
SELECT LET total = price * qty, total
FROM line_items
ASSERT total > 0 ON FAIL SKIP
```

DEFINE assertions from the block and any trailing ASSERT clauses written after the column list are all collected into the same assertion list, with block-sourced assertions applied first.

### Constraints

- A SELECT may have at most one DEFINE block.
- DEFINE cannot be combined with inline LET bindings in the same SELECT.

## PIVOT

PIVOT rotates distinct values of a column into output columns, computing one aggregate per cell.

### Syntax

```sql
SELECT ...
FROM table
PIVOT (
    aggregate1 [, aggregate2 ...]
    FOR pivot_column
    [IN (value1 [, value2 ...])]
) [AS alias]
```

### Examples

**Explicit value list:**

```sql
SELECT product, North, South, East, West
FROM sales
PIVOT (
    SUM(amount)
    FOR region
    IN ('North', 'South', 'East', 'West')
)
```

This produces one column per region. The output schema is:
`product | North | South | East | West`

**Auto-discover values (no IN clause):**

```sql
SELECT *
FROM experiment_results
PIVOT (AVG(score) FOR condition)
```

Distinct values of `condition` are discovered at runtime and each becomes an output column. This mode is subject to the cardinality cap (see below).

**Multiple aggregates:**

When more than one aggregate is specified, each output column is named `<value>_<aggregate>`:

```sql
SELECT *
FROM sales
PIVOT (SUM(amount), COUNT(id) FOR region IN ('North', 'South'))
-- Output columns: product, North_SUM(amount), North_COUNT(id), South_SUM(amount), South_COUNT(id)
```

### Output schema

- All source columns **except** the pivot column become key (pass-through) columns.
- One output column is created per `(pivot value, aggregate)` pair.
- Single-aggregate queries use the pivot value as the column name directly.
- Multi-aggregate queries prefix each column with `<value>_`.
- The SELECT column list preceding `PIVOT` is ignored; PIVOT defines the output schema.

### Cardinality cap

Auto-discover mode (no `IN` clause) caps the number of distinct pivot values at **1000**. Queries that exceed this limit fail with an error:

> PIVOT auto-discover exceeded the cardinality cap of 1000 distinct values. Use an explicit IN (value1, value2, …) list to select the values to pivot.

Use an explicit `IN (…)` list to select a known subset of values and avoid the cap.

### Execution model

PIVOT is a **blocking** operator — it must buffer all input rows before emitting any output, since it must discover all distinct pivot values (in auto-discover mode) before building the output schema. When a memory budget is configured and the in-memory row buffer exceeds the budget, overflow rows are spilled to a temporary file on disk and replayed during the aggregation phase. This bounds peak memory regardless of input size.

---

## UNPIVOT

UNPIVOT rotates a set of columns into rows, emitting a (name, value) pair per column per input row.

### Syntax

```sql
SELECT ...
FROM table
UNPIVOT [INCLUDE NULLS] (
    value_column
    FOR name_column
    IN (source_col1 [, source_col2 ...])
) [AS alias]
```

### Examples

**Basic unpivot:**

```sql
SELECT id, quarter, revenue
FROM wide_sales
UNPIVOT (
    revenue
    FOR quarter
    IN (q1, q2, q3, q4)
)
```

For each input row with columns `id, q1, q2, q3, q4`, this emits four output rows:
```
id=1, quarter='q1', revenue=<q1 value>
id=1, quarter='q2', revenue=<q2 value>
id=1, quarter='q3', revenue=<q3 value>
id=1, quarter='q4', revenue=<q4 value>
```

**Include NULLs:**

By default, rows where the source column value is NULL are omitted. Use `INCLUDE NULLS` to retain them:

```sql
SELECT *
FROM sparse_data
UNPIVOT INCLUDE NULLS (
    measurement
    FOR sensor
    IN (temp, pressure, humidity)
)
```

### Output schema

- All source columns **not listed in the IN clause** become key (pass-through) columns.
- One output column carries the cell value (named by the first identifier before `FOR`).
- One output column carries the source column name as a string (named by the identifier after `FOR`).
- The SELECT column list preceding `UNPIVOT` is ignored; UNPIVOT defines the output schema.

### Execution model

UNPIVOT is a **streaming** operator — it processes each input row immediately and emits up to N output rows (where N is the number of source columns). No buffering is required.

---

## INTO

Write query results to a file. The output format is inferred from the file extension (`.csv`, `.parquet`, `.h5`/`.hdf5`):

```sql
SELECT * FROM data INTO 'output.csv'
SELECT * FROM data INTO 'output.parquet'
SELECT * FROM data INTO 'output.h5'
```

### Sharding

```sql
-- New shard every 10,000 rows: output_shard_00000.csv, output_shard_00001.csv, ...
SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000

-- New shard every 100MB
SELECT * FROM data INTO 'output.parquet' SHARD ON byte_size 104857600
```

### Checkpointing (resumable writes)

Add `--checkpoint` to resume a sharded write from the last completed shard after a crash or interruption:

```bash
# First run — crashes after writing shards 0–4
datum-ingest query "SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000" \
  --source csv:data=large.csv --checkpoint

# Re-run the same command — resumes from shard 5
datum-ingest query "SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000" \
  --source csv:data=large.csv --checkpoint
```

How it works:

1. After each shard is finalized, a `{shard_path}.checkpoint` marker file is written containing the row count, byte count, and source fingerprints.
2. On restart with `--checkpoint`, existing markers are scanned and validated against current source files (size + modification time).
3. If sources match, the pipeline fast-forwards by skipping already-written rows, then continues writing from the next shard index.
4. If the process crashed mid-shard, the incomplete file (which has no marker) is automatically deleted before writing the replacement.
5. After successful completion, all `.checkpoint` marker files are cleaned up.

**Requirements:**

- `SHARD ON` must be specified. Without it, `--checkpoint` prints a warning and is ignored.
- The query must produce rows in a **deterministic, stable order** between runs. Queries with `ORDER BY RANDOM()` or non-deterministic source ordering will produce incorrect results on resume. Ensuring deterministic ordering is the user's responsibility.
- Source data files must not change between runs. If file size or modification time differs, the resume is aborted with an error.

## ORDER BY / LIMIT / OFFSET

```sql
SELECT * FROM data ORDER BY score DESC
SELECT * FROM data ORDER BY category ASC, score DESC
SELECT * FROM data LIMIT 100
SELECT * FROM data LIMIT 100 OFFSET 50
SELECT * FROM data ORDER BY score DESC LIMIT 10
```

When ORDER BY + LIMIT are combined, a bounded priority queue (top-N sort) avoids materializing the full result set.

When a memory budget is configured, ORDER BY uses an external sort strategy: in-memory rows are sorted in runs, and when the budget is exceeded each run is flushed to a temporary file on disk. The final output is produced by a k-way merge of all sorted runs, ensuring arbitrarily large sorts complete without out-of-memory failures.

## CASE Expressions

CASE expressions provide inline conditional logic, similar to if/else chains.

### Searched CASE

Each WHEN clause contains an independent boolean condition:

```sql
SELECT CASE
         WHEN score >= 90 THEN 'A'
         WHEN score >= 80 THEN 'B'
         WHEN score >= 70 THEN 'C'
         ELSE 'F'
       END AS grade
FROM students
```

### Simple CASE

Compares an operand against each WHEN value:

```sql
SELECT CASE status
         WHEN 1 THEN 'active'
         WHEN 2 THEN 'inactive'
         WHEN 3 THEN 'banned'
         ELSE 'unknown'
       END AS status_label
FROM users
```

### Behavior

- WHEN clauses are evaluated in order; the first match wins.
- If no WHEN clause matches and no ELSE is provided, the result is NULL.
- For simple CASE, a NULL operand never matches any WHEN value (SQL three-valued logic).
- CASE expressions can appear anywhere an expression is valid: SELECT, WHERE, ORDER BY, GROUP BY, HAVING, and JOIN ON.
- CASE expressions can be nested.
- For simple single-condition cases, the `iif()` function provides a more concise alternative.

### Branch Type Coercion

When THEN/ELSE branches return different data types, DatumIngest applies
implicit type coercion following SQL Server–style type precedence rules:

1. **Common type wins.** If all branches share a common type through the
   standard widening chain (e.g. `UInt8 → Float32`, `Boolean → Float32`), that
   common type is used.
2. **Non-String type wins over String.** When some branches return `String` and
   others return a numeric, boolean, or temporal type, the non-String type is
   chosen as the result type. String values are parsed to the target type at
   runtime.
3. **Unparseable strings become NULL.** If a String branch value cannot be
   parsed to the target type at runtime, the result is NULL rather than an
   error. This is an ETL-friendly behavior — data flows through without
   interruption, and downstream consumers can detect NULLs as conversion
   failures.

```sql
-- The result type is Float32, not String.
-- The string '0' is parsed to the number 0 at runtime.
SELECT CASE WHEN x > 0 THEN '0' ELSE 1 END AS value
FROM data

-- If 'not_a_number' cannot be parsed, the result is NULL.
SELECT CASE WHEN x > 0 THEN 'not_a_number' ELSE 1 END AS value
FROM data
```

Coercible String targets include: `Float32`, `UInt8`, `Boolean`, `Date`,
`DateTime`, `Time`, `Duration`, `Uuid`, and `JsonValue`. Types like `Vector`,
`Matrix`, `Tensor`, `Image`, and `UInt8Array` cannot be coerced from String.

## Lambda Expressions

Lambda expressions define inline anonymous functions for use with higher-order functions such as `array_transform` and `array_filter`. They are not first-class values — they can only appear as arguments to functions that expect them.

### Syntax

A single parameter needs no parentheses:

```sql
SELECT array_transform(prices, p -> p * 1.1) FROM products
```

Parentheses are optional for a single parameter and required for multiple parameters:

```sql
SELECT array_filter(scores, (s) -> s > 0.5) FROM students
```

The arrow operator is `->` (thin arrow). The body is any scalar expression.

### Closure capture

Lambda bodies can reference columns from the enclosing row:

```sql
SELECT array_transform(prices, p -> p * discount) FROM products
```

Here `discount` is a column on the `products` table, captured by the lambda at evaluation time.

### Restrictions

- Lambdas cannot appear outside a higher-order function argument list.
- Lambdas cannot be aliased, stored, or passed between queries.
- Lambda parameter names shadow column names of the same name within the body.

## Array Literals

Bracket syntax constructs arrays inline. `[a, b, c]` is syntactic sugar for `array(a, b, c)`.

```sql
SELECT [1, 2, 3]                          -- array of numbers
SELECT ['hello', 'world']                  -- array of strings
SELECT []                                  -- empty array
SELECT array_filter([10, 20, 30], x -> x > 15)  -- combined with lambdas
```

Nested array literals are supported:

```sql
SELECT [[1, 2], [3, 4]]
```

## Struct Literals

Brace syntax constructs struct values inline. Each field is a `name: expression` pair:

```sql
SELECT {name: 'alice', score: 9.5}                  -- two-field struct
SELECT {x: lng, y: lat, label: category} FROM data  -- fields from column references
SELECT {}                                            -- empty struct
```

Field names become the keys of the resulting struct value. Types are inferred from each field's expression at plan time. Struct literals can be nested:

```sql
SELECT {point: {x: 1.0, y: 2.0}, radius: 5.0}
```

## Index Access

The postfix `[index]` operator accesses array elements by position or struct fields by name. Multiple subscripts chain left-to-right.

### Array element access

```sql
SELECT scores[0]           -- first element (0-based)
SELECT matrix[1]           -- second element of a vector/array column
```

### Struct field access

```sql
SELECT row['name']         -- access field 'name' from a struct column
SELECT meta['created_at']  -- string key returns the named field
```

Field name lookup is case-insensitive. Accessing a field that does not exist returns null.

### Chained access

```sql
-- Access an element of an array stored inside a struct field
SELECT record['scores'][2] FROM data

-- Access a field of an inline struct literal
SELECT {x: 10, y: 20}['y']   -- returns 20
```

## Common Table Expressions (WITH)

CTEs define named temporary result sets scoped to a single statement. They can simplify complex queries by breaking them into readable, composable stages.

```sql
WITH filtered AS (
  SELECT id, name, score FROM data WHERE score > 50
)
SELECT name, score FROM filtered ORDER BY score DESC
```

### Multiple CTEs

Comma-separate multiple CTEs. Later CTEs can reference earlier ones:

```sql
WITH
  high_scores AS (
    SELECT id, name, score FROM data WHERE score > 80
  ),
  ranked AS (
    SELECT name, score, ROW_NUMBER() OVER (ORDER BY score DESC) AS rank
    FROM high_scores
  )
SELECT name, score, rank FROM ranked WHERE rank <= 10
```

### Column renaming

An optional column list renames the CTE's output columns:

```sql
WITH summary(category, total) AS (
  SELECT department, COUNT(*) FROM employees GROUP BY department
)
SELECT category, total FROM summary
```

### Materialization hints

By default, the planner auto-materializes CTEs referenced more than once. Override this with `MATERIALIZED` or `NOT MATERIALIZED`:

```sql
-- Force materialization (compute once, buffer results)
WITH stats AS MATERIALIZED (
  SELECT AVG(score) AS mean, COUNT(*) AS n FROM data
)
SELECT * FROM stats

-- Force inlining (re-execute per reference, no buffer)
WITH latest AS NOT MATERIALIZED (
  SELECT * FROM data ORDER BY timestamp DESC LIMIT 100
)
SELECT * FROM latest
```

Materialized CTEs buffer their results in memory. When a memory budget is configured and the buffer exceeds it, rows spill to temporary files on disk. Materialization adds no Query Units (0 QU) but enforces per-query QU budgets during buffering.

### Recursive CTEs

`WITH RECURSIVE` enables iterative queries. The CTE body must contain a `UNION ALL` separating the anchor member (base case) from the recursive member (which references the CTE name):

```sql
-- Generate a sequence 1..10
WITH RECURSIVE seq AS (
  SELECT 1 AS n
  UNION ALL
  SELECT n + 1 FROM seq WHERE n < 10
)
SELECT n FROM seq
```

The recursive member executes iteratively: each iteration reads the previous iteration's output as the working table. Iteration stops when the working table is empty. A safety limit (`MaxRecursionDepth`, default 1,000) prevents infinite loops — exceeding it raises an error.

```sql
-- Hierarchical traversal
WITH RECURSIVE tree AS (
  SELECT id, parent_id, name, 0 AS depth
  FROM nodes WHERE parent_id IS NULL
  UNION ALL
  SELECT n.id, n.parent_id, n.name, t.depth + 1
  FROM nodes AS n INNER JOIN tree AS t ON n.parent_id = t.id
)
SELECT id, name, depth FROM tree ORDER BY depth, name
```

### Execution strategy

| Scenario | Behavior |
|----------|----------|
| Single reference, no hint | Inlined (streaming, no buffer) |
| Multiple references, no hint | Auto-materialized |
| `MATERIALIZED` hint | Always materialized |
| `NOT MATERIALIZED` hint | Always inlined |
| `WITH RECURSIVE` | Materialized per iteration |

## Subqueries

### Derived tables (FROM subqueries)

A subquery in the FROM clause produces an inline table. The subquery must be aliased.

```sql
SELECT id, name FROM (
  SELECT id, name, value FROM data WHERE value > 100
) AS filtered
WHERE name LIKE 'item_%'
```

### Scalar subqueries

A subquery that returns a single value can appear anywhere an expression is valid — in the SELECT list, WHERE clause, CASE expressions, and function arguments. The subquery must return exactly one column and at most one row.

```sql
-- Uncorrelated: constant-folded at plan time
SELECT name, score - (SELECT AVG(score) FROM data) AS deviation
FROM data

-- Correlated: re-evaluated per outer row
SELECT name, (SELECT MAX(value) FROM details WHERE details.id = data.id) AS max_val
FROM data
```

Uncorrelated scalar subqueries are executed once during planning and replaced with a literal value. Correlated scalar subqueries reference columns from the outer query and are executed once per outer row.

### IN / NOT IN subqueries

Filter rows based on whether a value appears in the result of a subquery. The subquery must return exactly one column.

```sql
-- Uncorrelated IN: constant-folded to a literal value list at plan time
SELECT name FROM employees
WHERE department_id IN (SELECT id FROM active_departments)

-- Correlated IN: decorrelated into a semi-join
SELECT name FROM employees
WHERE department_id IN (
  SELECT id FROM departments WHERE departments.region = employees.region
)

-- NOT IN: anti-semi-join with SQL-standard NULL semantics
SELECT name FROM employees
WHERE department_id NOT IN (SELECT id FROM excluded_departments)
```

`NOT IN` follows SQL three-valued logic: if the subquery result contains any NULL value, no rows pass the filter (because `x NOT IN (..., NULL, ...)` evaluates to UNKNOWN for every `x`).

### EXISTS / NOT EXISTS subqueries

Test whether a subquery produces any rows. The subquery's column list is irrelevant — only row existence matters. `SELECT 1` is conventional.

```sql
-- Uncorrelated EXISTS: boolean gate evaluated at plan time
SELECT name FROM data
WHERE EXISTS (SELECT 1 FROM feature_flags WHERE flag = 'enabled')

-- Correlated EXISTS: decorrelated into a semi-join
SELECT name FROM customers
WHERE EXISTS (
  SELECT 1 FROM orders WHERE orders.customer_id = customers.id
)

-- Correlated NOT EXISTS: decorrelated into an anti-semi-join
SELECT name FROM customers
WHERE NOT EXISTS (
  SELECT 1 FROM orders WHERE orders.customer_id = customers.id
)
```

### Execution strategy

| Form | Uncorrelated | Correlated |
|------|-------------|------------|
| Scalar `(SELECT ...)` | Constant-folded at plan time | `ScalarSubqueryOperator` per outer row |
| `IN (SELECT ...)` | Constant-folded to `IN (values)` | Left semi-join (hash join) |
| `NOT IN (SELECT ...)` | Constant-folded to `NOT IN (values)` | Left anti-semi-join (null-sensitive) |
| `EXISTS (SELECT ...)` | Boolean gate at plan time | Left semi-join (hash join) |
| `NOT EXISTS (SELECT ...)` | Boolean gate at plan time | Left anti-semi-join |

Correlated subqueries are decorrelated by the query planner: correlation predicates in the inner WHERE are extracted and become the join's ON condition, while non-correlated predicates remain as filters on the inner plan.

## Set Operations

Set operations combine the results of two or more SELECT statements. All six variants are supported:

```sql
-- UNION ALL: concatenate both result sets, keeping duplicates
SELECT name, category FROM train
UNION ALL
SELECT name, category FROM test

-- UNION (distinct): concatenate and deduplicate
SELECT category FROM train
UNION
SELECT category FROM test

-- INTERSECT: rows that appear in both
SELECT id FROM train
INTERSECT
SELECT id FROM test

-- INTERSECT ALL: rows that appear in both, preserving minimum occurrence count
SELECT category FROM train
INTERSECT ALL
SELECT category FROM test

-- EXCEPT: rows in the left that do not appear in the right
SELECT id FROM train
EXCEPT
SELECT id FROM test

-- EXCEPT ALL: subtract right-side counts from left-side counts
SELECT category FROM train
EXCEPT ALL
SELECT category FROM test
```

### Chaining

Multiple set operations can be chained. INTERSECT binds tighter than UNION and EXCEPT, following SQL standard precedence:

```sql
-- INTERSECT is evaluated first, then UNION
SELECT id FROM a
UNION
SELECT id FROM b
INTERSECT
SELECT id FROM c

-- Equivalent to:
SELECT id FROM a
UNION
(SELECT id FROM b INTERSECT SELECT id FROM c)
```

UNION and EXCEPT have equal precedence and associate left to right.

### ORDER BY, LIMIT, and OFFSET

Trailing ORDER BY, LIMIT, and OFFSET clauses apply to the entire compound result, not to individual branches:

```sql
-- Sort the combined result
SELECT name, score FROM train
UNION ALL
SELECT name, score FROM test
ORDER BY score DESC
LIMIT 100
```

To apply ORDER BY or LIMIT to an individual branch, use a Common Table Expression or subquery:

```sql
WITH top_train AS (
    SELECT name, score FROM train ORDER BY score DESC LIMIT 50
)
SELECT * FROM top_train
UNION ALL
SELECT name, score FROM test ORDER BY score DESC LIMIT 50
```

### Execution model

| Operation | Strategy |
|-----------|----------|
| UNION ALL | Zero-overhead stream concatenation |
| UNION (distinct) | Streaming hash deduplication with spill-to-disk |
| INTERSECT | Materialise right branch into hash set, probe with left; spill-to-disk via grace hash partitioning |
| INTERSECT ALL | Materialise right branch into counted multiset, emit up to count; spill-to-disk via grace hash partitioning |
| EXCEPT | Materialise right branch into hash set, exclude from left; spill-to-disk via grace hash partitioning |
| EXCEPT ALL | Materialise right branch into counted multiset, subtract counts; spill-to-disk via grace hash partitioning |

For single-column results, `HashSet<DataValue>` is used directly. For multi-column results, a `CompositeKey` wrapper provides structural equality and hashing.

UNION DISTINCT supports **spill-to-disk** when a memory budget is configured: when the in-memory hash set exceeds the budget (tracked by `MemoryEstimator`), unseen rows are spilled to 64 hash-partitioned temporary files and deduplicated in a drain phase. INTERSECT and EXCEPT (all four variants) also support **spill-to-disk**: when the right-branch materialisation exceeds the memory budget, remaining right rows are hash-partitioned to spill files; left rows whose partitions were spilled are buffered to corresponding left-side spill files and processed partition-by-partition in a drain phase. This ensures arbitrarily large set operations complete without out-of-memory failures.

Set operations add no Query Units (0 QU).

## Parameterized Queries

Named parameters use PostgreSQL-style `$name` syntax. Parameters can appear anywhere an expression is valid — WHERE, SELECT, JOIN ON, ORDER BY, HAVING, CASE, and function arguments.

```sql
-- Filter with a parameter
SELECT * FROM data WHERE score > $threshold

-- Multiple parameters
SELECT * FROM data WHERE category = $category AND score > $min_score

-- In expressions
SELECT name, score * $weight AS weighted FROM data

-- In function arguments
SELECT normalize(score, $min, $max) AS norm FROM data
```

### CLI usage

Pass values with repeatable `--param key=value` flags:

```bash
datum-ingest query "SELECT * FROM data WHERE score > $threshold" \
  --source "data=./data.csv" \
  --param threshold=0.5

datum-ingest explore "SELECT * FROM data WHERE category = $cat" \
  --source "data=./data.csv" \
  --param cat=electronics
```

### Value type inference

Parameter values are parsed from strings with automatic type inference:

| Value | Inferred type |
|-------|---------------|
| `42`, `3.14`, `-1.5` | Float32 |
| `true`, `false` | Boolean |
| `null` | Null |
| Everything else | String |

### Binding model

Parameters use **early binding** — `$name` placeholders are parsed into `ParameterExpression` AST nodes, then substituted with `LiteralExpression` values before the query planner runs. This preserves all existing optimizations (predicate pushdown, statistics pruning, bloom filter acceleration, index seek) without modification.

If a query references a parameter that was not supplied, parsing succeeds but binding fails with a diagnostic listing the missing parameter names. Supplying parameters that are not referenced in the query also produces an error.

### gRPC usage

The `QueryRequest` message accepts a `parameters` map:

```protobuf
map<string, DataValueMessage> parameters = 3;
```

See [Compute Backend — Query](compute.md#query-server-streaming) for details.

## Type System

### DataKind values

| DataKind | Description | Internal representation |
|----------|-------------|------------------------|
| `Boolean` | True or false | `bool` |
| `UInt8` | Unsigned 8-bit integer (0–255) | `byte` |
| `Int8` | Signed 8-bit integer (−128–127) | `sbyte` |
| `Int16` | Signed 16-bit integer | `short` |
| `UInt16` | Unsigned 16-bit integer | `ushort` |
| `Int32` | Signed 32-bit integer | `int` |
| `UInt32` | Unsigned 32-bit integer | `uint` |
| `Int64` | Signed 64-bit integer | `long` |
| `UInt64` | Unsigned 64-bit integer | `ulong` |
| `Float32` | 32-bit IEEE 754 float | `float` |
| `Float64` | 64-bit IEEE 754 double | `double` |
| `String` | Variable-length UTF-8 text | `string` |
| `Date` | Calendar date (no time component) | `DateOnly` |
| `DateTime` | Date and time with UTC offset | `DateTimeOffset` |
| `Time` | Time of day (no date component) | `TimeOnly` |
| `Duration` | Elapsed time span | `TimeSpan` |
| `Uuid` | 128-bit UUID (RFC 9562) | `Guid` |
| `JsonValue` | Raw JSON string for deferred parsing | `string` |
| `Vector` | Rank-1 float array | `float[]` |
| `Matrix` | Rank-2 float array | `float[]` + shape `[rows, cols]` |
| `Tensor` | N-dimensional float array | `float[]` + `int[]` shape |
| `UInt8Array` | Raw byte array | `byte[]` |
| `Image` | Encoded image bytes | `byte[]` |
| `Array` | Ordered sequence of same-typed values | `DataValue[]` |
| `Struct` | Named, ordered collection of heterogeneous fields | `DataValue[]` (field names in `ColumnInfo.Fields`) |
| `Type` | A type tag describing another DataKind | `DataKind` enum value (stored as byte) |

### Type Literals and typeof()

Type names (`Int32`, `Float64`, `String`, `Boolean`, `Date`, `DateTime`, etc.)
are reserved keywords in expression position. They produce a `Type` value — a
first-class type tag rather than a string. This enables type-oriented
comparisons without string matching.

#### typeof()

`typeof(expr)` returns the runtime `DataKind` of its argument as a `Type` value.
The result can be projected, compared, and used in any expression context:

```sql
-- Project the runtime type as a column (displays "Float64", "String", etc.)
SELECT name, typeof(value) AS value_type FROM data

-- Filter rows by type
SELECT * FROM mixed_data WHERE typeof(value) = Int32

-- Multiple type check with IN
SELECT * FROM t WHERE typeof(col) IN (Int32, Int64, Float32, Float64)
```

#### can_cast()

`can_cast(expr, Type)` returns whether a value can be safely cast to the target
type without data loss, overflow, or parse failure. Unlike `typeof()` which
checks type identity, `can_cast` checks value representability:

```sql
-- typeof checks identity: is x already an Int32?
SELECT * FROM t WHERE typeof(x) = Int32

-- can_cast checks castability: will CAST succeed without error?
SELECT * FROM t WHERE can_cast(x, UInt8)       -- false for 5000, true for 200
SELECT * FROM t WHERE can_cast(x, Int32)       -- true for 3.14 (truncates to 3)
SELECT * FROM t WHERE can_cast(name, Date)     -- false for "abc", true for "2024-06-15"
```

`can_cast` matches CAST semantics: truncation of fractional parts is allowed
(it's not data loss — it's expected CAST behavior). Only overflow (value outside
the target range) and parse failures return false. Widening conversions (e.g.,
Int32 → Float64) always return true. Unsupported pairs (e.g., Vector → Int32)
return false.

#### try_cast()

`try_cast(expr, Type)` attempts the same conversion as `CAST`, but returns NULL
on failure instead of throwing. It follows CAST semantics on success — including
numeric truncation (e.g., `try_cast(3.99, Int32)` returns `3`).

```sql
-- Returns NULL for unparseable strings instead of erroring
SELECT try_cast(raw_value, Int32) AS parsed_int FROM raw_data

-- Combine with COALESCE for defaults
SELECT COALESCE(try_cast(score, Float64), 0.0) AS safe_score FROM t

-- Filter rows that can be converted (similar to can_cast but gives you the value)
SELECT try_cast(x, Date) AS parsed_date FROM t WHERE try_cast(x, Date) IS NOT NULL
```

**can_cast vs try_cast:** Both follow CAST semantics — truncation is allowed
(e.g., `can_cast(3.14, Int32)` returns true because `CAST(3.14 AS Int32)`
succeeds with value 3). Only overflow and parse failures return false/NULL.
`can_cast` returns Boolean; `try_cast` returns the converted value or NULL.

#### IS [NOT] Type

The `IS` predicate provides a concise shorthand for type checks. It desugars to
a `typeof()` comparison — no new semantics, just cleaner syntax:

```sql
-- These pairs are equivalent:
SELECT * FROM t WHERE x IS Int32
SELECT * FROM t WHERE typeof(x) = Int32

SELECT * FROM t WHERE x IS NOT String
SELECT * FROM t WHERE typeof(x) != String
```

`IS NULL` / `IS NOT NULL` continue to work unchanged — `NULL` is a distinct
keyword, so there is no ambiguity.

#### Type-narrowing bind

When you need to both check a type and use the narrowed value, the
`expr AS Type name AND ...` syntax combines both in a single expression:

```sql
-- Check that x can be safely cast to Int32, then use the typed value
WHERE x AS Int32 y AND y > 0

-- Desugars to:
WHERE can_cast(x, Int32) AND CAST(x AS Int32) > 0
```

The guard uses `can_cast()`, not `typeof()` — it checks *value representability*,
not just type identity. For example, `5000 AS UInt8 y` fails because 5000 doesn't
fit in a UInt8, even though 5000 is a valid Int32. This prevents silent data loss
from overflow and truncation.

The binding name (`y`) is scoped to the right side of the same `AND` — it is
replaced with `CAST(source AS Type)` during parsing. The name does not exist in
SELECT, ORDER BY, or any other clause.

```sql
-- Compound conditions — y is substituted in all right-side predicates
WHERE x AS Int32 y AND y > 0 AND y < 100

-- OR branches — each bind is independent
WHERE (x AS Int32 y AND y > 0) OR (x AS String z AND len(z) > 3)

-- Complex source expressions — avoids repeating the expression
WHERE json_value(data, '$.score') AS Float64 score AND score > 0.5
```

The pattern is restricted: `AS Type name` must appear as the left operand of
`AND`. Using it standalone, in `OR` directly, or referencing the name outside
the AND body is a parse error.

#### CASE on type

Simple CASE with `typeof()` enables type-driven branching:

```sql
SELECT CASE typeof(x)
    WHEN Int32   THEN round(x, 0)
    WHEN Float64 THEN round(x, 2)
    WHEN String  THEN len(x)
    ELSE NULL
END AS result
FROM t
```

#### Type literal rules

All `DataKind` names (`Boolean`, `Int8`, `Int16`, `Int32`, `Int64`, `UInt8`,
`UInt16`, `UInt32`, `UInt64`, `Float32`, `Float64`, `String`, `Date`,
`DateTime`, `Time`, `Duration`, `Uuid`, `JsonValue`, `Vector`, `Matrix`,
`Tensor`, `UInt8Array`, `Image`, `Array`, `Struct`, `Type`) are reserved in
expression position. They produce a `Type` value that can be compared with
`typeof()` results using `=`, `!=`, `IN`, `CASE`, and `IS`.

To use a type name as a column alias or table name, double-quote it:

```sql
SELECT 1 AS "Int32"
CREATE TEMP TABLE "String" (id Int32, value String)
```

Type names in non-expression contexts (column names in DDL, aliases after `AS`,
table names after `FROM`) are accepted without quoting.

### Type conversions

#### Implicit widening

When an operator or function receives mixed types, the engine automatically
widens both sides to the narrowest common type. The widening chain is:

```
Boolean → UInt8 ─→ Int16 ─→ Int32 ─→ Int64 ─→ Float64 → Vector → Tensor
           Int8 ↗    UInt16 ↗   UInt32 ↗   UInt64 ↗
                                             Float32 ↗
                                            Duration ↗
                                      Matrix ──────────→ Tensor
```

Widening is transitive — `UInt8` can reach `Float64` by following the chain
through `Int16 → Int32 → Int64 → Float64`. Same-kind is always a no-op.

#### Explicit conversion (CAST)

`CAST` converts between any supported type pair. Two equivalent syntaxes:

```sql
CAST(x AS Int32)     -- SQL-standard syntax
cast(x, Int32)       -- function-call syntax with type literal
```

Supported conversions include:

- **Any numeric ↔ any numeric** — truncates fractional parts, wraps on integer
  overflow (UInt8 saturates at 0–255 instead of wrapping).
- **Any numeric ↔ Boolean** — zero = false, non-zero = true.
- **Any numeric/Boolean/Date/DateTime/Time/Duration/Uuid ↔ String** — formats or parses.
- **Date ↔ DateTime** — midnight UTC or drop time component.
- **DateTime → Time** — extract time-of-day.
- **Date/DateTime → numeric** — epoch days or epoch seconds.
- **Time/Duration ↔ numeric** — seconds since midnight or total seconds.
- **String ↔ JsonValue** — text reinterpretation.
- **UInt8Array ↔ Image** — byte reinterpretation.

Use `can_cast(x, Type)` to check if a conversion is lossless before casting, or
`try_cast(x, Type)` to get NULL on failure instead of an error. The function-call
syntax composes naturally with both:

```sql
-- Safe conversion pipeline
SELECT try_cast(raw_value, Float64) AS parsed FROM t
SELECT * FROM t WHERE can_cast(score, Int32) AND cast(score, Int32) > 0
```

### EXTRACT

PostgreSQL-standard syntax for extracting date/time fields. Desugars to `date_part()` at parse time.

```sql
EXTRACT(field FROM source)
```

`field` is a bare keyword (not a string) — any field supported by `date_part()`. `source` is a Date, DateTime, or Time expression.

```sql
-- Extract year and month
SELECT EXTRACT(YEAR FROM order_date) AS y,
       EXTRACT(MONTH FROM order_date) AS m
FROM orders

-- ISO day of week (1=Monday, 7=Sunday)
SELECT EXTRACT(ISODOW FROM event_date) AS dow FROM events

-- Unix epoch seconds
SELECT EXTRACT(EPOCH FROM created_at) AS epoch_secs FROM logs

-- Century and millennium
SELECT EXTRACT(CENTURY FROM birth_date) AS century FROM people

-- Works with Time values
SELECT EXTRACT(HOUR FROM start_time) AS h FROM schedule
```

`EXTRACT(YEAR FROM x)` is exactly equivalent to `date_part('year', x)` — use whichever reads better in context. See the [full field list](functions.md#date_part--extract-supported-parts) in the functions reference.

### AT TIME ZONE

Converts a `DateTime` value to a specific timezone. The instant in time is preserved — only the UTC offset (and therefore the displayed local time) changes. Uses IANA timezone names.

```sql
expr AT TIME ZONE 'timezone_name'
```

```sql
-- UTC pickup time → local New York time
SELECT pickup_datetime AT TIME ZONE 'America/New_York' AS local_time
FROM trips

-- Compare timestamps across zones (no parentheses needed)
SELECT * FROM events
WHERE created_at AT TIME ZONE 'America/New_York' = updated_at AT TIME ZONE 'UTC'

-- Extract local hour after converting
SELECT date_part('hour', pickup_datetime AT TIME ZONE 'America/New_York') AS local_hour
FROM trips

-- Extract UTC offset in seconds (e.g. -18000 for EST, -14400 for EDT)
SELECT date_part('timezone', pickup_datetime AT TIME ZONE 'America/New_York') AS tz_offset
FROM trips
```

Timezone names follow the [IANA tz database](https://www.iana.org/time-zones) (e.g. `America/New_York`, `Europe/London`, `Asia/Kolkata`, `UTC`). DST transitions are handled automatically.

### Vector, Matrix, and Tensor relationship

All three store a flat `float[]` buffer internally:
- **Vector**: `float[]` with implicit shape `[length]`
- **Matrix**: `float[]` with shape `[rows, cols]`
- **Tensor**: `float[]` with arbitrary `int[]` shape

Conversion between them is zero-copy when ranks match. Use `reshape()` to reinterpret shape without copying (element count must match).

## EXPLAIN

The `explain` command shows the query execution plan as a tree. Two modes are supported:

### Static EXPLAIN

Shows the operator tree structure, join strategies, filter predicates, and warnings — without executing the query:

```bash
datum-ingest explain "SELECT x, y FROM data WHERE x > 0 ORDER BY x LIMIT 100" --source csv:data=measurements.csv
```

```
Limit (limit: 100)
└─ Sort (x ASC)
    → bounded top-N sort (N=100)
    └─ Project (x, y)
        └─ Filter (predicate: x > 0)
            └─ Scan (table: data, provider: csv, columns: [*])
```

When a WHERE predicate is pushed down to a Parquet scan, the plan shows the advisory filter hint:

```
Filter (predicate: id > 1000)
└─ Scan (table: events, provider: parquet, columns: [*], statistics filter: id > 1000)
```

### EXPLAIN ANALYZE

Add `--analyze` to actually execute the query and report runtime metrics — row counts, filter selectivity, self time, and total time per operator:

```bash
datum-ingest explain "SELECT x FROM data WHERE x > 0.5" --source csv:data=measurements.csv --analyze
```

```
Filter (predicate: x > 0.5)  |  rows in: 10,000 → out: 4,987 (49.9%)  |  self: 1.2 ms  |  total: 8.7 ms
└─ Scan (table: data, provider: csv, columns: [*])  |  rows: 10,000  |  self: 7.5 ms  |  total: 7.5 ms
```

For Parquet scans with statistics-based pruning, EXPLAIN ANALYZE reports how many row groups were skipped:

```
Filter (predicate: id > 1000)  |  rows in: 50,000 → out: 12,345 (24.7%)  |  self: 0.8 ms  |  total: 15.2 ms
└─ Scan (table: events, provider: parquet, columns: [*], statistics filter: id > 1000)  |  rows: 50,000  |  row groups: 10 total, 7 pruned (70%)  |  self: 14.4 ms  |  total: 14.4 ms
```

### Warnings

The explain plan emits warnings about potential performance issues:

| Warning | Trigger |
|---------|---------|
| ORDER BY materializes all input rows | ORDER BY without LIMIT |
| CROSS JOIN produces a cartesian product | CROSS JOIN |
| FULL OUTER JOIN materializes both sides | FULL OUTER JOIN |
| Pattern matching predicate requires full scan | LIKE / ILIKE / REGEXP in WHERE |
| GroupBy materializes all groups in memory | GROUP BY |

## Schema Introspection

The `schema` command resolves column metadata from all table sources in a query's FROM and JOIN clauses without executing the query. This is designed for editor integration (Monaco, VS Code) where column names, types, and source tables are needed for autocomplete. Table names in the query can use any of the quoted identifier styles described in the [FROM](#from) section.

### CLI usage

```bash
# Single table
datum-ingest schema "SELECT * FROM data" --source csv:data=measurements.csv
```

```
Column                         Type         Nullable   Source
----------------------------------------------------------------------
name                           String       YES        data
age                            Float32      YES        data
score                          Float32      YES        data

(3 column(s) from 1 source(s))
```

```bash
# JOIN — columns from both sides, with LEFT JOIN marking the right side nullable
datum-ingest schema "SELECT * FROM images AS img LEFT JOIN captions AS cap ON img.id = cap.image_id" \
  --source "zip:images=./train2017.zip" \
  --source "json:captions=./captions.json"
```

```
Column                         Type         Nullable   Source
----------------------------------------------------------------------
file_name                      String       NO         img
file_bytes                     UInt8Array   NO         img
image_id                       Float32      YES        cap
caption                        String       YES        cap

(4 column(s) from 2 source(s))
```

```bash
# Table-valued function
datum-ingest schema "SELECT * FROM RANGE(0, 360) AS r" --source csv:dummy=placeholder.csv
```

```
Column                         Type         Nullable   Source
----------------------------------------------------------------------
Value                          Float32      NO         r

(1 column(s) from 1 source(s))
```

Supported table sources:
- Named tables (resolved via catalog/provider `GetSchemaAsync`)
- Aliased tables (`FROM table AS alias`)
- JOINs (INNER, LEFT, RIGHT, FULL OUTER, CROSS — outer joins mark the outer side nullable)
- Subqueries (`FROM (SELECT ...) AS sub` — recursively infers output column types)
- Table-valued functions (`RANGE`, `UNNEST` — via `ISchemaAwareTableFunction`)
- Virtual schema tables (`information_schema.tables`, `datum_catalog.functions`, etc.)

## Virtual Schemas

DatumIngest supports **schema-qualified table references** using `schema_name.table_name` syntax. Two virtual schemas are built in: `information_schema` (PostgreSQL-compatible metadata) and `datum_catalog` (DatumIngest-specific catalog views). Virtual schemas are read-only — DDL/DML statements against them are rejected.

### Schema-qualified syntax

```sql
-- Query a virtual schema table
SELECT * FROM information_schema.tables

-- With an alias
SELECT t.table_name, t.table_type FROM information_schema.tables AS t

-- Join virtual schema with real data
SELECT c.column_name, c.data_type
FROM information_schema.columns AS c
WHERE c.table_name = 'orders_csv'
ORDER BY c.ordinal_position
```

Schema and table names are case-insensitive.

### `information_schema`

PostgreSQL-compatible metadata views reflecting all tables visible in the current catalog. Temp tables appear with `table_schema = 'temp'` and `table_type = 'TEMPORARY TABLE'`; all other sources appear under `schema = 'public'` as `'BASE TABLE'`.

#### `information_schema.tables`

| Column | Type | Description |
|--------|------|-------------|
| `table_catalog` | String | Always `'datum'` |
| `table_schema` | String | `'public'` or `'temp'` |
| `table_name` | String | Table name as registered in the catalog |
| `table_type` | String | `'BASE TABLE'` or `'TEMPORARY TABLE'` |

```sql
-- List all tables and their types
SELECT table_schema, table_name, table_type
FROM information_schema.tables
ORDER BY table_schema, table_name
```

#### `information_schema.columns`

| Column | Type | Description |
|--------|------|-------------|
| `table_catalog` | String | Always `'datum'` |
| `table_schema` | String | `'public'` or `'temp'` |
| `table_name` | String | Parent table name |
| `column_name` | String | Column name |
| `ordinal_position` | Int32 | 1-based column position |
| `data_type` | String | DatumIngest `DataKind` name (e.g. `'Float32'`, `'String'`) |
| `is_nullable` | String | `'YES'` or `'NO'` |

```sql
-- Show columns for a specific table
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name = 'orders_csv'
ORDER BY ordinal_position
```

#### `information_schema.schemata`

| Column | Type | Description |
|--------|------|-------------|
| `catalog_name` | String | Always `'datum'` |
| `schema_name` | String | `'public'`, `'temp'`, `'information_schema'`, or `'datum_catalog'` |

```sql
SELECT schema_name FROM information_schema.schemata
```

### `datum_catalog`

DatumIngest-specific metadata views exposing providers, functions, per-column statistics, indexes, and column interactions from manifests.

#### `datum_catalog.providers`

| Column | Type | Description |
|--------|------|-------------|
| `provider_name` | String | Format provider name (e.g. `'csv'`, `'parquet'`, `'hdf5'`) |

```sql
SELECT provider_name FROM datum_catalog.providers
```

#### `datum_catalog.functions`

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `function_name` | String | NO | Function name |
| `function_type` | String | NO | `'SCALAR'`, `'AGGREGATE'`, `'TABLE_VALUED'`, or `'WINDOW'` |
| `category` | String | YES | Functional category (e.g. `'String'`, `'Numeric'`, `'Temporal'`) |
| `return_type` | String | YES | Return data kind, or null if context-dependent |
| `description` | String | YES | Human-readable description of the function |
| `parameter_count` | Int32 | YES | Number of parameters |
| `query_unit_cost` | Int32 | YES | Base query-unit cost per invocation |

```sql
-- List all aggregate functions with their descriptions
SELECT function_name, description, parameter_count
FROM datum_catalog.functions
WHERE function_type = 'AGGREGATE'
ORDER BY function_name
```

#### `datum_catalog.function_parameters`

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `function_name` | String | NO | Parent function name |
| `ordinal_position` | Int32 | NO | 1-based parameter position |
| `parameter_name` | String | NO | Parameter name |
| `data_type` | String | NO | Expected data kind (or `'Any'`) |
| `is_optional` | String | NO | `'YES'` or `'NO'` |

```sql
-- Show parameters for the SUBSTR function
SELECT ordinal_position, parameter_name, data_type, is_optional
FROM datum_catalog.function_parameters
WHERE function_name = 'SUBSTR'
ORDER BY ordinal_position
```

#### `datum_catalog.statistics`

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `table_name` | String | NO | Source table name |
| `column_name` | String | NO | Column name |
| `data_type` | String | NO | DatumIngest `DataKind` name |
| `row_count` | Int64 | NO | Total row count from manifest |
| `distinct_count` | Int64 | NO | Estimated distinct values (HyperLogLog) |
| `null_ratio` | Float64 | YES | Fraction of null values |
| `min_value` | String | YES | Minimum value (as string, where available) |
| `max_value` | String | YES | Maximum value (as string, where available) |
| `entropy` | Float64 | YES | Shannon entropy in bits |
| `dominant_value_ratio` | Float64 | YES | Ratio of most frequent value to total rows |
| `is_constant` | String | YES | `'YES'` if column has ≤ 1 distinct value |
| `column_role` | String | YES | Inferred role (`Identifier`, `ForeignKey`, `Categorical`, `Measure`, `Temporal`, `Text`) |
| `top_value` | String | YES | Most frequent value |
| `top_value_frequency` | Int64 | YES | Count of the most frequent value |
| `mean` | Float64 | YES | Arithmetic mean (numeric columns only) |
| `standard_deviation` | Float64 | YES | Population standard deviation (numeric only) |
| `skewness` | Float64 | YES | Distribution skewness (numeric only) |
| `kurtosis` | Float64 | YES | Distribution kurtosis (numeric only) |
| `p25` | Float64 | YES | 25th percentile (numeric only) |
| `p50` | Float64 | YES | 50th percentile / median (numeric only) |
| `p75` | Float64 | YES | 75th percentile (numeric only) |
| `zero_ratio` | Float64 | YES | Fraction of zero values (numeric only) |
| `outlier_ratio` | Float64 | YES | Fraction of Z-score > 3 outliers (numeric only) |
| `integer_valued` | String | YES | `'YES'` if all values are integers (numeric only) |
| `min_length` | Int32 | YES | Minimum string length (string columns only) |
| `max_length` | Int32 | YES | Maximum string length (string columns only) |
| `true_ratio` | Float64 | YES | Fraction of true values (boolean columns only) |

Only tables with a `.datum-manifest` sidecar or session-generated statistics appear in this view.

```sql
-- Show distribution statistics for numeric columns
SELECT column_name, mean, standard_deviation, skewness, p25, p50, p75
FROM datum_catalog.statistics
WHERE table_name = 'orders_csv' AND mean IS NOT NULL
ORDER BY column_name
```

#### `datum_catalog.indexes`

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `table_name` | String | NO | Source table name |
| `column_name` | String | NO | Indexed column name |
| `index_type` | String | NO | `'SORTED'`, `'BTREE'`, `'BITMAP'`, `'BLOOM'`, or `'MAPPED_SORTED'` |
| `entry_count` | Int64 | YES | Number of indexed entries (null for bitmap/bloom) |
| `chunk_count` | Int32 | NO | Number of data chunks |
| `total_row_count` | Int64 | NO | Total rows in the source file |

```sql
-- Show all indexes for a table
SELECT column_name, index_type, entry_count
FROM datum_catalog.indexes
WHERE table_name = 'orders_csv'
ORDER BY column_name, index_type
```

#### `datum_catalog.interactions`

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `table_name` | String | NO | Source table name |
| `column_a` | String | NO | First column name |
| `column_b` | String | NO | Second column name |
| `pearson` | Float64 | YES | Pearson correlation (numeric × numeric) |
| `spearman` | Float64 | YES | Spearman rank correlation (numeric × numeric) |
| `cramer_v` | Float64 | YES | Cramér's V (categorical × categorical) |
| `anova_f` | Float64 | YES | ANOVA F-statistic (categorical × numeric) |
| `mutual_information` | Float64 | YES | Mutual information in bits |
| `theil_u_ab` | Float64 | YES | Theil's U(A|B) — B reduces uncertainty about A |
| `theil_u_ba` | Float64 | YES | Theil's U(B|A) — A reduces uncertainty about B |
| `missingness_correlation` | Float64 | YES | Pearson correlation between null masks |

Only tables with computed interactions in their manifest appear in this view.

```sql
-- Find strongly correlated column pairs
SELECT column_a, column_b, pearson, mutual_information
FROM datum_catalog.interactions
WHERE table_name = 'orders_csv' AND ABS(pearson) > 0.7
ORDER BY ABS(pearson) DESC
```

## DDL / DML

DatumIngest supports session-scoped temp tables with full DDL/DML. Temp tables are stored as `.datum` files in a per-session directory; source indexes and column statistics manifests are auto-generated so the query planner has accurate cardinality estimates and can apply chunk pruning.

### Table Mutability

Every table registered in the catalog has a mutability level:

| Level | Description |
|-------|-------------|
| `ReadOnly` | Default for all data sources. DDL/DML statements return an error. |
| `SessionOwned` | Temp tables created within a session. All DDL/DML permitted. |
| `Writable` | Reserved for future use (external mutable sources). |

Attempting `INSERT`, `UPDATE`, `DELETE`, `ALTER TABLE`, or `DROP TABLE` on a read-only table returns an error.

### CREATE TEMP TABLE

Creates a session-scoped table with an explicit column definition:

```sql
CREATE TEMP TABLE features (
    customer_id   INT PRIMARY KEY,
    tenure_months INT NOT NULL,
    monthly_spend FLOAT64,
    label         STRING
)
```

Composite primary keys are declared with a table-level constraint:

```sql
CREATE TEMP TABLE order_products (
    user_id    INT,
    product_id INT,
    quantity   INT,
    PRIMARY KEY (user_id, product_id)
)
```

Supported column modifiers:

| Modifier | Behavior |
|----------|----------|
| `NOT NULL` | Column rejects NULL values on INSERT (both VALUES and SELECT sources). |
| `PRIMARY KEY` | Implies `NOT NULL`. Enforces uniqueness on INSERT — duplicate key values are rejected. UPDATE on a PRIMARY KEY column is prohibited. |

Both inline `col INT PRIMARY KEY` and table-level `PRIMARY KEY (col1, col2)` syntax are supported.
When a table has a primary key, each `INSERT` validates that no new rows duplicate an existing or
in-batch key value. Violations return an error and the entire batch is rejected.

`CREATE TEMP TABLE IF NOT EXISTS` silently succeeds when the table already exists.

### CREATE TEMP TABLE AS SELECT

Creates and populates a temp table from a query in a single statement:

```sql
CREATE TEMP TABLE features AS
SELECT customer_id, tenure_months, total_charges / NULLIF(tenure_months, 0) AS avg_spend
FROM customers
```

The schema is inferred from the query output. A source index and column statistics manifest are auto-generated after materialization when the table contains rows.

### INSERT INTO

Appends rows to an existing table:

```sql
-- Literal values
INSERT INTO features VALUES (1, 'Alice', 0.95), (2, 'Bob', 0.42)

-- Column list (unmapped columns filled with NULL)
INSERT INTO features (customer_id, label) VALUES (3, 'churn')

-- From a query
INSERT INTO features SELECT id, name, score FROM raw_data WHERE score IS NOT NULL
```

NOT NULL columns are validated before rows are appended — a NULL value in any non-nullable column
rejects the entire batch with an error. A source index and column statistics manifest are auto-rebuilt
after each INSERT into a session-owned table.

### UPDATE

Replaces column values in a table. Supports constant literals, arbitrary expressions
(referencing the same row), and WHERE predicates:

```sql
UPDATE features SET label = 'retain'
UPDATE features SET score = score * 1.1 WHERE status = 'active'
UPDATE features SET label = category, score = score + 0.05
```

#### UPDATE...FROM (join-based enrichment)

Follows PostgreSQL semantics. The target table is **not** repeated in the FROM clause;
the WHERE clause provides both the join condition and any additional row filters:

```sql
-- Enrich a feature table from a raw-scores source
UPDATE features SET score = raw.value
FROM raw
WHERE features.id = raw.id

-- With an explicit target alias
UPDATE features AS f SET score = raw.value * 1.1
FROM raw
WHERE f.id = raw.id

-- Multi-table join: features ← raw ← model
UPDATE features SET score = raw.value * m.weight
FROM raw
JOIN model AS m ON raw.model_id = m.id
WHERE features.id = raw.id
```

SET column names are always unqualified. SET expressions can reference columns from both
the target table and source tables using qualified form (`alias.column`).

When multiple source rows match the same target row, the last match wins (indeterminate
order, matching PostgreSQL documented behavior).

UPDATE on a PRIMARY KEY column is not permitted. To change a row's key, DELETE the row
and re-INSERT with the new key values.

### DELETE

Removes rows using tombstone bitmaps:

```sql
DELETE FROM features WHERE score IS NULL
```

Tombstoned rows are excluded from subsequent queries. The underlying storage is not compacted.

### ALTER TABLE ADD COLUMN

Adds a column to an existing table:

```sql
ALTER TABLE features ADD COLUMN risk_tier STRING
ALTER TABLE features ADD COLUMN flag INT NOT NULL DEFAULT 0
```

Existing rows receive the `DEFAULT` value (or NULL when no default is specified).

#### Computed Columns

Use `AS expr` to derive a column from existing columns. The expression is evaluated
against every existing row and the result is persisted (materialized):

```sql
ALTER TABLE features ADD COLUMN total FLOAT64 AS price * quantity
ALTER TABLE features ADD COLUMN upper_name STRING AS UPPER(name)
ALTER TABLE features ADD COLUMN flag BOOLEAN NOT NULL AS score > 0.5
```

`DEFAULT` and `AS` are mutually exclusive — a column is either constant-filled or computed, not both.
The expression can reference any column that exists at the time of the ALTER, and supports
arithmetic, function calls, CASE, CAST, and all operators available in SELECT expressions.

### DROP TABLE

Removes a table from the catalog and deletes its backing file:

```sql
DROP TABLE features
DROP TABLE IF EXISTS features
```

### ANALYZE

Rebuilds the source index and column statistics manifest for a table:

```sql
ANALYZE features
```

Use `ANALYZE` after a series of mutations (`UPDATE`, `DELETE`, `ALTER TABLE ADD COLUMN`) to refresh statistics for the query planner. `INSERT` and `CREATE TEMP TABLE AS SELECT` auto-generate sidecars, so `ANALYZE` is only needed after other DDL/DML operations.

Follows the PostgreSQL convention — no `TABLE` keyword required.

### Batch Execution

Multiple statements can be separated by semicolons:

```sql
DROP TABLE IF EXISTS features;
CREATE TEMP TABLE features AS SELECT * FROM raw_data;
ALTER TABLE features ADD COLUMN risk FLOAT64 DEFAULT 0.0;
ALTER TABLE features ADD COLUMN total FLOAT64 AS price * quantity;
UPDATE features SET risk = 0.9 WHERE churn_score > 0.8;
ANALYZE features
```

Statements execute sequentially. On failure, execution stops and no further statements run.
