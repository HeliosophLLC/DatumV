# SQL Reference

[← Back to README](../README.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest supports a subset of SQL designed for ML dataset ETL: SELECT, SELECT DISTINCT, FROM, JOIN, WHERE, GROUP BY, HAVING, window functions (OVER/PARTITION BY), INTO, ORDER BY, LIMIT, OFFSET, subqueries, Common Table Expressions (WITH / WITH RECURSIVE), and set operations (UNION, INTERSECT, EXCEPT).

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
```

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

## FROM

```sql
FROM table_name
FROM table_name AS alias
FROM (SELECT ... FROM ...) AS subquery
FROM RANGE(0, 360) AS r
```

### Quoted table names

Table names that contain dots, spaces, hyphens, start with a digit, or collide with SQL keywords must be quoted. Three quoting styles are supported:

```sql
-- Bracket-quoted (recommended)
SELECT * FROM [adult.data]

-- Double-quoted
SELECT * FROM "adult.data"

-- Single-quoted
SELECT * FROM 'adult.data'
```

The `.tables` command and tab completion automatically bracket-quote names that need it, so you can copy them directly into your SQL.

Table-valued functions can be used as data sources in FROM and JOIN clauses:

```sql
-- Generate rows with Value 0 through 360 (inclusive)
SELECT r.[Value] FROM RANGE(0, 360) AS r

-- With a custom step
SELECT r.[Value] FROM RANGE(0, 1, 0.1) AS r

-- Compute a sine wave
SELECT r.[Value] AS x, ((SIN(2.0 * PI() * r.[Value]) + 1.0) / 2.0) AS y
FROM RANGE(0, 360) AS r

-- Use in a CROSS JOIN
SELECT t.name, r.[Value] AS angle
FROM data AS t
CROSS JOIN RANGE(0, 360) AS r
```

## JOIN

All five join types are supported:

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

## WHERE

```sql
WHERE col > 10
WHERE col1 = 'value' AND col2 < 100
WHERE col IN ('a', 'b', 'c')
WHERE col BETWEEN 10 AND 50
WHERE col LIKE 'prefix_%'
WHERE col IS NULL
WHERE col IS NOT NULL
WHERE NOT (col1 > 10 OR col2 < 5)
```

Supported operators: `=`, `!=`, `<`, `>`, `<=`, `>=`, `AND`, `OR`, `NOT`, `LIKE`, `IN`, `BETWEEN`, `IS NULL`, `IS NOT NULL`.

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
| `MIN(expr)` | Minimum value. Works on Scalar, UInt8, String, Date, DateTime, Time. |
| `MAX(expr)` | Maximum value. Works on Scalar, UInt8, String, Date, DateTime, Time. |
| `VARIANCE(expr)` | Sample variance (N−1 denominator) of non-null values. Alias for `VAR_SAMP`. |
| `VAR_SAMP(expr)` | Sample variance (N−1). Returns null for fewer than 2 values. |
| `VAR_POP(expr)` | Population variance (N denominator) of non-null values. |
| `STDDEV(expr)` | Sample standard deviation (N−1). Alias for `STDDEV_SAMP`. |
| `STDDEV_SAMP(expr)` | Sample standard deviation (N−1). Returns null for fewer than 2 values. |
| `STDDEV_POP(expr)` | Population standard deviation (N denominator) of non-null values. |
| `MEDIAN(expr)` | Median (50th percentile). Averages two middle values for even counts. |
| `PERCENTILE_CONT(expr, fraction)` | Continuous percentile with linear interpolation. Fraction in [0, 1]. |
| `PERCENTILE_DISC(expr, fraction)` | Discrete percentile (nearest rank). Returns an observed value. Fraction in [0, 1]. |
| `MODE(expr)` | Most frequently occurring value. Ties broken by first occurrence. |
| `CORR(y, x)` | Pearson correlation coefficient. Returns value in [−1, 1]. |
| `COVAR_POP(y, x)` | Population covariance (N denominator). |
| `COVAR_SAMP(y, x)` | Sample covariance (N−1 denominator). Null for fewer than 2 pairs. |
| `APPROX_MEDIAN(expr)` | Approximate median via reservoir sampling. O(1) memory, ~1–5% error. |
| `APPROX_PERCENTILE(expr, fraction)` | Approximate percentile via reservoir sampling. O(1) memory, ~1–5% error. |
| `STRING_AGG(expr, separator [ORDER BY expr [ASC|DESC]])` | Concatenates non-null strings with a separator. Supports intra-aggregate ORDER BY. |

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

-- Concatenate labels per group
SELECT category, STRING_AGG(name, ', ' ORDER BY name ASC) AS items
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

GROUP BY uses hash-based aggregation: all groups are accumulated in memory using a hash table keyed by the GROUP BY expressions. This is a blocking operator — all input rows must be consumed before any output rows are emitted.

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

All single-argument aggregate functions (COUNT, SUM, AVG, MIN, MAX, VARIANCE, STDDEV, MEDIAN, MODE, PERCENTILE_CONT, and their variants) can be used with OVER to compute running or partitioned aggregates. Two-argument aggregates (CORR, COVAR_POP, COVAR_SAMP) and STRING_AGG are not supported as window functions.

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
| `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`, and variants | `agg(...) OVER (...)` | Any single-argument aggregate function used with OVER becomes a window aggregate. Includes VARIANCE, STDDEV, MEDIAN, MODE, PERCENTILE_CONT, PERCENTILE_DISC, and APPROX_ variants. |

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
FROM → JOIN → WHERE → GROUP BY → HAVING → Window → QUALIFY → SELECT → DISTINCT → ORDER BY → LIMIT
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
   standard widening chain (e.g. `UInt8 → Scalar`, `Boolean → Scalar`), that
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
-- The result type is Scalar, not String.
-- The string '0' is parsed to the number 0 at runtime.
SELECT CASE WHEN x > 0 THEN '0' ELSE 1 END AS value
FROM data

-- If 'not_a_number' cannot be parsed, the result is NULL.
SELECT CASE WHEN x > 0 THEN 'not_a_number' ELSE 1 END AS value
FROM data
```

Coercible String targets include: `Scalar`, `UInt8`, `Boolean`, `Date`,
`DateTime`, `Time`, `Duration`, `Uuid`, and `JsonValue`. Types like `Vector`,
`Matrix`, `Tensor`, `Image`, and `UInt8Array` cannot be coerced from String.

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
| INTERSECT | Materialise right branch into hash set, probe with left |
| INTERSECT ALL | Materialise right branch into counted multiset, emit up to count |
| EXCEPT | Materialise right branch into hash set, exclude from left |
| EXCEPT ALL | Materialise right branch into counted multiset, subtract counts |

For single-column results, `HashSet<DataValue>` is used directly. For multi-column results, a `CompositeKey` wrapper provides structural equality and hashing.

UNION DISTINCT supports **spill-to-disk** when a memory budget is configured: when the in-memory hash set exceeds the budget (tracked by `MemoryEstimator`), unseen rows are spilled to 64 hash-partitioned temporary files and deduplicated in a drain phase. This ensures arbitrarily large unions complete without out-of-memory failures.

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
| `42`, `3.14`, `-1.5` | Scalar |
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
| `Scalar` | 32-bit float | `float` |
| `UInt8` | Unsigned 8-bit integer | `byte` |
| `Vector` | Rank-1 float array | `float[]` |
| `Matrix` | Rank-2 float array | `float[]` + shape `[rows, cols]` |
| `Tensor` | N-dimensional float array | `float[]` + `int[]` shape |
| `UInt8Array` | Raw byte array | `byte[]` |
| `Image` | Encoded image bytes | `byte[]` |
| `String` | Unicode text | `string` |
| `Date` | Calendar date | `DateOnly` |
| `DateTime` | Date and time | `DateTime` |
| `JsonValue` | Raw JSON string | `string` |

### Type conversions

Implicit widening (automatic):
- `UInt8` → `Scalar`
- `Scalar` → `Vector[1]`
- `Vector` → `Tensor` (rank 1)
- `Matrix` → `Tensor` (rank 2)

Explicit narrowing via `CAST(value AS type)`:
- `Scalar` → `UInt8` (truncates)
- `Tensor` → `Vector` (requires rank 1)
- `Tensor` → `Matrix` (requires rank 2)
- `Date` → `Scalar` (epoch days since 1970-01-01)
- `DateTime` → `Scalar` (epoch seconds since 1970-01-01T00:00:00Z, float32)

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
| LIKE predicate requires full scan | LIKE in WHERE |
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
age                            Scalar       YES        data
score                          Scalar       YES        data

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
image_id                       Scalar       YES        cap
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
Value                          Scalar       NO         r

(1 column(s) from 1 source(s))
```

Supported table sources:
- Named tables (resolved via catalog/provider `GetSchemaAsync`)
- Aliased tables (`FROM table AS alias`)
- JOINs (INNER, LEFT, RIGHT, FULL OUTER, CROSS — outer joins mark the outer side nullable)
- Subqueries (`FROM (SELECT ...) AS sub` — recursively infers output column types)
- Table-valued functions (`RANGE`, `UNNEST` — via `ISchemaAwareTableFunction`)
