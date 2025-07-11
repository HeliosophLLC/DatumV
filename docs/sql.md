# SQL Reference

[← Back to README](../README.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Architecture](architecture.md) · [Programmatic API](api.md)

DatumQuery supports a subset of SQL designed for ML dataset ETL: SELECT, FROM, JOIN, WHERE, INTO, ORDER BY, LIMIT, OFFSET, and subqueries.

## SELECT

```sql
SELECT *
SELECT col1, col2, col3
SELECT a.col1, b.col2
SELECT col1 AS alias, normalize(col2) AS norm_col
SELECT table_alias.*
```

## FROM

```sql
FROM table_name
FROM table_name AS alias
FROM (SELECT ... FROM ...) AS subquery
FROM RANGE(0, 360) AS r
```

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
dq query "SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000" \
  --source csv:data=large.csv --checkpoint

# Re-run the same command — resumes from shard 5
dq query "SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000" \
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

## Subqueries

```sql
SELECT id, name FROM (
  SELECT id, name, value FROM data WHERE value > 100
) AS filtered
WHERE name LIKE 'item_%'
```

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
dq explain "SELECT x, y FROM data WHERE x > 0 ORDER BY x LIMIT 100" --source csv:data=measurements.csv
```

```
Limit (limit: 100)
└─ Sort (x ASC)
    ⚠ ORDER BY materializes all input rows for sorting.
    └─ Project (x, y)
        └─ Filter (predicate: x > 0)
            └─ Scan (table: data, provider: csv, columns: [*])
```

### EXPLAIN ANALYZE

Add `--analyze` to actually execute the query and report runtime metrics — row counts, filter selectivity, self time, and total time per operator:

```bash
dq explain "SELECT x FROM data WHERE x > 0.5" --source csv:data=measurements.csv --analyze
```

```
Filter (predicate: x > 0.5)  |  rows in: 10,000 → out: 4,987 (49.9%)  |  self: 1.2 ms  |  total: 8.7 ms
└─ Scan (table: data, provider: csv, columns: [*])  |  rows: 10,000  |  self: 7.5 ms  |  total: 7.5 ms
```

### Warnings

The explain plan emits warnings about potential performance issues:

| Warning | Trigger |
|---------|---------|
| ORDER BY materializes all input rows | Any ORDER BY clause |
| CROSS JOIN produces a cartesian product | CROSS JOIN |
| FULL OUTER JOIN materializes both sides | FULL OUTER JOIN |
| LIKE predicate requires full scan | LIKE in WHERE |

## Schema Introspection

The `schema` command resolves column metadata from all table sources in a query's FROM and JOIN clauses without executing the query. This is designed for editor integration (Monaco, VS Code) where column names, types, and source tables are needed for autocomplete.

### CLI usage

```bash
# Single table
dq schema "SELECT * FROM data" --source csv:data=measurements.csv
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
dq schema "SELECT * FROM images AS img LEFT JOIN captions AS cap ON img.id = cap.image_id" \
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
dq schema "SELECT * FROM RANGE(0, 360) AS r" --source csv:dummy=placeholder.csv
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
