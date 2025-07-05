# Axon.QueryEngine

A high-performance ML dataset query engine for .NET 10. Use SQL to extract, transform, and load data from CSV, JSON, ZIP, HDF5, and Parquet files into ML-ready output formats with optional sharding.

## Overview

Axon.QueryEngine provides a SQL-based ETL pipeline designed for machine learning dataset preparation. It parses a subset of SQL (SELECT, FROM, JOIN, WHERE, INTO, ORDER BY, LIMIT), executes queries against file-based data sources, and writes results to HDF5, Parquet, or CSV output with configurable sharding.

Key features:
- **SQL interface** — familiar syntax for data selection, filtering, joining, and projection
- **Five data providers** — CSV, JSON, ZIP, HDF5, Parquet
- **Three output formats** — HDF5, Parquet, CSV with SHARD ON support
- **Lazy evaluation** — expressions in SELECT are deferred until values are needed
- **Rich type system** — Scalar, Vector, Matrix, Tensor, UInt8, Image, String, Date, DateTime, JSON
- **Built-in statistics** — HyperLogLog cardinality, Welford's online mean/variance, top-K frequency
- **Streaming execution** — IAsyncEnumerable pipeline with projection and predicate pushdown

## Installation

### As a .NET library

```bash
dotnet add package Axon.QueryEngine
```

### As a CLI tool

```bash
dotnet tool install --global Axon.QueryEngine.Cli
```

### Build from source

```bash
git clone https://github.com/your-org/Axon.QueryEngine.git
cd Axon.QueryEngine
dotnet build
dotnet test
```

#### Publish a self-contained single-file binary

```bash
dotnet publish src/Axon.QueryEngine.Cli -c Release -r win-x64
dotnet publish src/Axon.QueryEngine.Cli -c Release -r linux-x64
dotnet publish src/Axon.QueryEngine.Cli -c Release -r osx-arm64
```

## Quick Start

### 1. Query a CSV file

```bash
axon explore "SELECT name, score FROM data WHERE score > 90" --source "csv:data=./iris.csv"
```

### 2. Join ZIP + JSON (COCO-like pipeline)

```bash
axon query "
  SELECT img.file_name, cap.caption
  FROM images AS img
  INNER JOIN captions AS cap ON img.id = cap.image_id
  WHERE len(cap.caption) > 20
  LIMIT 100
" --source "zip:images=./train2017.zip" --source "json:captions=./captions.json"
```

### 3. ETL with sharded output

```bash
axon query "
  SELECT id, normalize(value) AS norm_value, category
  FROM data
  INTO CSV 'output/result.csv' SHARD ON sample_count 10000
" --source "csv:data=./large_dataset.csv"
```

## CLI Reference

### Commands

| Command | Description |
|---------|-------------|
| `query` | Execute a SQL query. Supports INTO for file output. |
| `explore` | Print the first N rows of a query result to the console. |
| `stats` | Execute a query and print per-column statistics. |
| `explain` | Show the query execution plan without running it. |
| `manifest` | Generate a JSON manifest with per-column feature statistics. |

### Flags

| Flag | Description |
|------|-------------|
| `--catalog <path>` | Path to a JSON catalog file defining table sources. |
| `--source <def>` | Inline source definition. Format: `provider:name=path[;key=value]`. Repeatable. |
| `--limit <n>` | Row limit for explore mode (default: 10). |
| `--analyze` | Run EXPLAIN ANALYZE: execute the query and report actual row counts and timing. |
| `--output <path>` | Write manifest output to a file instead of stdout (manifest command). |

At least one of `--catalog` or `--source` is required. Both can be mixed; `--source` entries override same-named catalog entries.

### Source definition format

```
provider:name=path[;key=value;...]
```

Examples:
```
csv:data=./data.csv;delimiter=,;header=true
json:annotations=./coco.json
zip:images=./train2017.zip
hdf5:features=./embeddings.h5
parquet:labels=./labels.parquet
```

### Catalog file format

JSON array of table descriptors:

```json
[
  {
    "Provider": "csv",
    "Name": "iris",
    "FilePath": "./datasets/iris.csv",
    "Options": { "delimiter": ",", "header": "true" }
  },
  {
    "Provider": "json",
    "Name": "annotations",
    "FilePath": "./datasets/coco.json",
    "Options": {}
  }
]
```

## SQL Reference

### SELECT

```sql
SELECT *
SELECT col1, col2, col3
SELECT a.col1, b.col2
SELECT col1 AS alias, normalize(col2) AS norm_col
SELECT table_alias.*
```

### FROM

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

### JOIN

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

### WHERE

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

### INTO

Write query results to a file:

```sql
SELECT * FROM data INTO CSV 'output.csv'
SELECT * FROM data INTO PARQUET 'output.parquet'
SELECT * FROM data INTO HDF5 'output.h5'
```

With sharding:

```sql
-- New shard every 10,000 rows: output_shard_00000.csv, output_shard_00001.csv, ...
SELECT * FROM data INTO CSV 'output.csv' SHARD ON sample_count 10000

-- New shard every 100MB
SELECT * FROM data INTO PARQUET 'output.parquet' SHARD ON byte_size 104857600
```

### ORDER BY / LIMIT / OFFSET

```sql
SELECT * FROM data ORDER BY score DESC
SELECT * FROM data ORDER BY category ASC, score DESC
SELECT * FROM data LIMIT 100
SELECT * FROM data LIMIT 100 OFFSET 50
SELECT * FROM data ORDER BY score DESC LIMIT 10
```

When ORDER BY + LIMIT are combined, a bounded priority queue (top-N sort) avoids materializing the full result set.

### Subqueries

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

## Functions Reference

### Numeric / Array

| Function | Signature | Description |
|----------|-----------|-------------|
| `normalize` | `normalize(val, [min], [max])` | Normalize to 0–1 range. Byte/byte[]: default 0–255. Scalar/Vector: requires min/max. |
| `clamp` | `clamp(val, min, max)` | Clamp value to [min, max]. Works on Scalar, Vector, Matrix, Tensor. |
| `denormalize` | `denormalize(val, factor)` | Multiply by factor (reverse of normalize). |
| `reshape` | `reshape(tensor, dim1, dim2, ...)` | Reinterpret tensor shape without copying. Element count must match. |

### String

| Function | Signature | Description |
|----------|-----------|-------------|
| `len` | `len(val)` | Length of string or collection. |
| `mid` | `mid(str, start, length)` | Extract substring by position and length (0-based). |
| `substring` | `substring(str, start, [length])` | Extract substring from start position (0-based). |
| `get_filename` | `get_filename(path)` | Return file name with extension from path. |
| `get_file_extension` | `get_file_extension(path)` | Return extension (with dot) from path. |
| `get_path` | `get_path(path)` | Return directory portion of path. |

### JSON Column Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `json_value` | `json_value(col, path)` | Extract scalar from JSON string at path. Returns String, Scalar, or null. |
| `json_query` | `json_query(col, path)` | Extract JSON fragment (array/object). Returns JsonValue or Vector if all-numeric. |
| `json_exists` | `json_exists(col, path)` | Returns 1.0 if path exists in JSON, 0.0 otherwise. |
| `json_array_length` | `json_array_length(col, [path])` | Count elements in JSON array at root or path. |

### Type Conversion

| Function | Signature | Description |
|----------|-----------|-------------|
| `cast` | `cast(val, targetKind)` | Explicit type conversion. Date→Scalar yields epoch days; DateTime→Scalar yields epoch seconds. |
| `to_epoch` | `to_epoch(val)` | Convert Date to epoch days or DateTime to epoch seconds (since 1970-01-01) as Scalar. |

### Temporal Feature Extraction

| Function | Signature | Description |
|----------|-----------|-------------|
| `date_part` | `date_part(part, val)` | Extract a named component from a Date or DateTime as Scalar. |
| `cyclical_encode` | `cyclical_encode(val, period)` | Encode a Scalar as a 2-element Vector `[sin(2π·val/period), cos(2π·val/period)]`. |

#### `date_part` supported parts

| Part Name | Returns | Example |
|-----------|---------|----------|
| `year` | Year number | 2026 |
| `month` | 1–12 | 3 |
| `day` | 1–31 | 16 |
| `day_of_week` | 0 (Sunday) – 6 (Saturday) | 1 (Monday) |
| `hour` | 0–23 (Date returns 0) | 14 |
| `minute` | 0–59 (Date returns 0) | 30 |
| `second` | 0–59 (Date returns 0) | 45 |
| `day_of_year` | 1–366 | 75 |
| `week_of_year` | 1–53 (ISO 8601) | 12 |
| `quarter` | 1–4 | 1 |
| `is_weekend` | 0 or 1 | 0 |

#### Temporal ML encoding examples

```sql
-- Convert date to epoch days for use as a numeric feature
SELECT to_epoch(date_col) AS epoch_days FROM data

-- Equivalent via CAST
SELECT CAST(date_col AS Scalar) AS epoch_days FROM data

-- Extract individual components
SELECT date_part('year', date_col) AS year,
       date_part('month', date_col) AS month,
       date_part('day_of_week', date_col) AS dow
FROM data

-- Cyclical encoding for periodic features (preserves month 12 → 1 proximity)
SELECT cyclical_encode(date_part('month', date_col), 12) AS month_encoded,
       cyclical_encode(date_part('hour', datetime_col), 24) AS hour_encoded
FROM data

-- Full temporal feature vector via concatenation
SELECT vec_concat(
    cyclical_encode(date_part('month', d), 12),
    cyclical_encode(date_part('day_of_week', d), 7),
    cyclical_encode(date_part('hour', d), 24)
) AS temporal_features
FROM data
```

### Table-Valued Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `unnest` | `unnest(array_col)` | Expand array-valued column into separate rows. Works with Vector, UInt8Array, JsonValue arrays. |
| `range` | `range(start, end[, step])` | Generate a sequence of rows with a `Value` column from start to end (inclusive). Default step is 1. |

### Example SQL with functions

```sql
-- Normalize a numeric column
SELECT id, normalize(score, 0, 100) AS norm_score FROM data

-- JSON extraction
SELECT json_value(metadata, '$.category') AS cat FROM records

-- String manipulation
SELECT id, get_filename(file_path) AS name FROM files WHERE len(file_path) > 10

-- Reshape vectors
SELECT reshape(embedding, 16, 16) AS matrix_embed FROM features

-- Type casting
SELECT id, cast(score, 'UInt8') AS byte_score FROM data

-- Math functions
SELECT abs(delta), sqrt(variance), pow(base_val, 2) FROM metrics

-- ML activations on embeddings
SELECT sigmoid(score), relu(raw_output), gelu(activation) FROM model_outputs

-- Vector reductions
SELECT vec_mean(embedding), vec_norm(embedding), vec_std(features) FROM vectors

-- Distance computation
SELECT cosine_similarity(query_vec, doc_vec) AS similarity FROM search_results

-- Softmax normalization
SELECT softmax(logits) AS probabilities FROM predictions

-- Vector manipulation
SELECT vec_slice(embedding, 0, 128) AS half, vec_sort(scores) FROM data

-- Utility
SELECT coalesce(primary_score, fallback_score) AS score FROM results
```

### Math — Basic Arithmetic (8)

| Function | Signature | Description |
|----------|-----------|-------------|
| `abs` | `abs(x)` | Absolute value. Element-wise for vectors/matrices/tensors. |
| `sign` | `sign(x)` | Returns -1, 0, or 1. Element-wise. |
| `negate` | `negate(x)` | Negation (-x). Element-wise. |
| `mod` | `mod(a, b)` | Modulus (a % b). Element-wise with broadcast. |
| `add` | `add(a, b)` | Addition. Element-wise with scalar broadcast. |
| `subtract` | `subtract(a, b)` | Subtraction. Element-wise with scalar broadcast. |
| `multiply` | `multiply(a, b)` | Multiplication. Element-wise with scalar broadcast. |
| `divide` | `divide(a, b)` | Division. Element-wise with scalar broadcast. |

### Math — Powers, Roots & Logarithms (10)

| Function | Signature | Description |
|----------|-----------|-------------|
| `sqrt` | `sqrt(x)` | Square root. |
| `cbrt` | `cbrt(x)` | Cube root. |
| `square` | `square(x)` | Square (x²). |
| `exp` | `exp(x)` | Natural exponential (eˣ). |
| `exp2` | `exp2(x)` | Base-2 exponential (2ˣ). |
| `ln` | `ln(x)` | Natural logarithm. |
| `log2` | `log2(x)` | Base-2 logarithm. |
| `log10` | `log10(x)` | Base-10 logarithm. |
| `pow` | `pow(base, exp)` | Power function. Element-wise with broadcast. |
| `log` | `log(x, base)` | Logarithm with custom base. |

### Math — Trigonometric & Hyperbolic (14)

| Function | Signature | Description |
|----------|-----------|-------------|
| `sin` | `sin(x)` | Sine (radians). |
| `cos` | `cos(x)` | Cosine (radians). |
| `tan` | `tan(x)` | Tangent (radians). |
| `asin` | `asin(x)` | Arc sine → radians. |
| `acos` | `acos(x)` | Arc cosine → radians. |
| `atan` | `atan(x)` | Arc tangent → radians. |
| `atan2` | `atan2(y, x)` | Two-argument arc tangent. |
| `sinh` | `sinh(x)` | Hyperbolic sine. |
| `cosh` | `cosh(x)` | Hyperbolic cosine. |
| `tanh` | `tanh(x)` | Hyperbolic tangent. |
| `degrees` | `degrees(x)` | Radians → degrees. |
| `radians` | `radians(x)` | Degrees → radians. |
| `pi` | `pi()` | Returns π constant. |
| `euler` | `euler()` | Returns Euler's number e. |

### Math — Rounding & Quantization (7)

| Function | Signature | Description |
|----------|-----------|-------------|
| `ceil` | `ceil(x)` | Round up to nearest integer. |
| `floor` | `floor(x)` | Round down to nearest integer. |
| `truncate` | `truncate(x)` | Remove fractional part toward zero. |
| `round` | `round(x, [decimals])` | Round to nearest integer or specified decimal places. |
| `quantize` | `quantize(x, step)` | Round to nearest multiple of step. |
| `bucketize` | `bucketize(val, boundaries)` | Assign value to bucket index based on sorted boundary vector. |
| `clip` | `clip(x, min, max)` | Clip to range (alias for clamp). |

### Math — ML Activation Functions (12)

| Function | Signature | Description |
|----------|-----------|-------------|
| `sigmoid` | `sigmoid(x)` | Logistic sigmoid σ(x) = 1/(1+e⁻ˣ). |
| `relu` | `relu(x)` | Rectified Linear Unit max(0, x). |
| `selu` | `selu(x)` | Scaled Exponential Linear Unit. |
| `gelu` | `gelu(x)` | Gaussian Error Linear Unit (fast approximation). |
| `swish` | `swish(x)` | Swish activation x·σ(x). |
| `softplus` | `softplus(x)` | Softplus ln(1+eˣ). |
| `softsign` | `softsign(x)` | Softsign x/(1+\|x\|). |
| `mish` | `mish(x)` | Mish activation x·tanh(softplus(x)). |
| `hard_sigmoid` | `hard_sigmoid(x)` | Piecewise linear approximation of sigmoid. |
| `hard_swish` | `hard_swish(x)` | Hard Swish x·hard_sigmoid(x). |
| `leaky_relu` | `leaky_relu(x, [alpha])` | Leaky ReLU with configurable slope (default α=0.01). |
| `elu` | `elu(x, [alpha])` | Exponential Linear Unit (default α=1.0). |

### Math — Softmax & Normalization (3)

| Function | Signature | Description |
|----------|-----------|-------------|
| `softmax` | `softmax(vec)` | Numerically stable softmax → probability vector. |
| `log_softmax` | `log_softmax(vec)` | Log-softmax via log-sum-exp trick. |
| `l2_normalize` | `l2_normalize(vec)` | L2 normalize to unit length. |

### Math — Vector Reductions (14)

| Function | Signature | Description |
|----------|-----------|-------------|
| `vec_sum` | `vec_sum(x)` | Sum of all elements → Scalar. |
| `vec_mean` | `vec_mean(x)` | Mean of all elements → Scalar. |
| `vec_min` | `vec_min(x)` | Minimum element → Scalar. |
| `vec_max` | `vec_max(x)` | Maximum element → Scalar. |
| `vec_std` | `vec_std(x)` | Population standard deviation → Scalar. |
| `vec_var` | `vec_var(x)` | Population variance → Scalar. |
| `vec_median` | `vec_median(x)` | Median → Scalar. |
| `vec_argmin` | `vec_argmin(x)` | Index of minimum element → Scalar. |
| `vec_argmax` | `vec_argmax(x)` | Index of maximum element → Scalar. |
| `vec_norm` | `vec_norm(x, [p])` | Lp norm (default p=2). p=∞ for max-norm. |
| `vec_count_nonzero` | `vec_count_nonzero(x)` | Count of non-zero elements → Scalar. |
| `vec_any` | `vec_any(x)` | 1 if any element is non-zero, else 0. |
| `vec_all` | `vec_all(x)` | 1 if all elements are non-zero, else 0. |
| `vec_product` | `vec_product(x)` | Product of all elements → Scalar. |

### Math — Vector Manipulation (10)

| Function | Signature | Description |
|----------|-----------|-------------|
| `vec_slice` | `vec_slice(vec, start, len)` | Extract sub-vector by position and length. |
| `vec_concat` | `vec_concat(v1, v2, ...)` | Concatenate two or more vectors. |
| `vec_reverse` | `vec_reverse(vec)` | Reverse element order. |
| `vec_sort` | `vec_sort(vec)` | Sort ascending (returns copy). |
| `vec_unique` | `vec_unique(vec)` | Unique elements preserving first-occurrence order. |
| `vec_flatten` | `vec_flatten(x)` | Flatten Matrix/Tensor to Vector. |
| `vec_pad` | `vec_pad(vec, len, fill)` | Pad vector to target length with fill value. |
| `vec_repeat` | `vec_repeat(vec, count)` | Repeat vector n times. |
| `linspace` | `linspace(start, stop, n)` | Generate n evenly spaced values from start to stop. |
| `arange` | `arange(start, stop, step)` | Generate values with fixed step (excludes stop). |

### Math — Distance & Similarity (5)

| Function | Signature | Description |
|----------|-----------|-------------|
| `cosine_similarity` | `cosine_similarity(a, b)` | Cosine similarity [-1, 1] between two vectors. |
| `euclidean_distance` | `euclidean_distance(a, b)` | Euclidean (L2) distance between two vectors. |
| `manhattan_distance` | `manhattan_distance(a, b)` | Manhattan (L1) distance between two vectors. |
| `dot` | `dot(a, b)` | Dot product of two vectors. |
| `hamming_distance` | `hamming_distance(a, b)` | Hamming distance between two strings. |

### Math — Utility & Conditional (7)

| Function | Signature | Description |
|----------|-----------|-------------|
| `coalesce` | `coalesce(a, b, ...)` | Returns first non-null argument. |
| `greatest` | `greatest(a, b, ...)` | Returns maximum of scalar arguments. |
| `least` | `least(a, b, ...)` | Returns minimum of scalar arguments. |
| `is_nan` | `is_nan(x)` | Returns 1 if NaN, 0 otherwise. |
| `is_finite` | `is_finite(x)` | Returns 1 if finite, 0 if NaN or infinite. |
| `if_null` | `if_null(x, default)` | Returns x if not null, otherwise default. |
| `random` | `random()` | Random float in [0, 1). |

## Data Providers

### CSV

Reads RFC 4180 CSV files. Auto-detects numeric vs string columns from the first 100 rows.

| Option | Description | Default |
|--------|-------------|---------|
| `delimiter` | Field delimiter character | `,` |
| `header` | Whether first row is header | `true` |

Columns: derived from header row. Numeric values parsed as Scalar, others as String.

### JSON

Reads JSON files using System.Text.Json streaming. Supports root arrays.

Each object in the array becomes a row with properties as columns. Nested objects/arrays become JsonValue columns for extraction via `json_value()` / `json_query()`.

### ZIP

Reads ZIP archives via System.IO.Compression.

Yields rows with two columns:
- `file_name` (String) — eager, always available
- `file_bytes` (UInt8Array) — lazy via `LazyDataValue`, decompressed only on access

### HDF5

Reads HDF5 files via PureHDF (managed .NET).

Each 1-D dataset becomes a column. 2-D datasets yield one vector per row. Grouped datasets use flattened names (e.g., `group/dataset`).

### Parquet

Reads Parquet files via Parquet.Net low-level API.

Maps Parquet types to DataKind: INT32/INT64 → Scalar, FLOAT/DOUBLE → Scalar, BYTE_ARRAY (UTF8) → String, BYTE_ARRAY → UInt8Array.

## Execution Model

### Streaming pipeline

Queries execute as an `IAsyncEnumerable<Row>` pipeline:

```
ScanOperator → FilterOperator → ProjectOperator → OrderByOperator → LimitOperator
```

Each operator pulls rows from its child on demand. No intermediate materialization unless required (ORDER BY, JOIN build side).

### Lazy evaluation

`ProjectOperator` wraps all SELECT expression results as `LazyDataValue` thunks. Values materialize only when accessed:

```sql
-- load_image is NOT called here...
SELECT load_image(file_bytes) AS raw_image FROM archive

-- ...it is called here, only for rows passing the WHERE filter
SELECT resize(raw_image, 224, 224) FROM (
  SELECT load_image(file_bytes) AS raw_image, caption FROM archive
) AS inner
WHERE len(caption) > 20
```

### Projection and predicate pushdown

The query planner analyzes column references in SELECT/WHERE/ON and passes required-column sets down to `ScanOperator`, allowing providers to skip unreferenced columns. WHERE predicates referencing only one table are pushed below JoinOperator.

### Join implementation

Hash join for INNER, LEFT, RIGHT, and FULL OUTER joins:
- Build side: smaller table materialized into `Dictionary<DataValue, List<Row>>`
- Probe side: larger table streamed row-by-row against the hash table
- NULL keys never match (SQL semantics)
- CROSS JOIN uses nested loop (cartesian product)

## Statistics

The `stats` command collects per-column statistics:

| Statistic | Source | Applies To |
|-----------|--------|------------|
| Non-null count | CountAccumulator | All columns |
| Null/empty count | CountAccumulator | All columns |
| Distinct count estimate | CardinalityAccumulator | All columns (HyperLogLog, ±2%) |
| Top-K values | TopKAccumulator | All columns (default K=10) |
| Min, Max, Mean, Variance, StdDev | NumericAccumulator | Scalar, UInt8 |
| Zero count, Zero ratio | NumericAccumulator | Scalar, UInt8 |
| Outlier count, Outlier ratio | NumericAccumulator | Scalar, UInt8 (Z-score > 3) |
| Histogram | HistogramAccumulator | Scalar, UInt8 (reservoir sampling, 50 bins) |
| Percentiles (P1–P99) | QuantileAccumulator | Scalar, UInt8 (reservoir sampling, linear interpolation) |
| Min/Max string length | StringLengthAccumulator | String, JsonValue |
| Element count range, element-wise min/max/mean/var/std | VectorStatsAccumulator | Vector, Matrix, Tensor |
| Rank range (dimensionality) | VectorStatsAccumulator | Vector, Matrix, Tensor |
| Zero element count, Zero element ratio, Zero vector count | VectorStatsAccumulator | Vector, Matrix, Tensor |
| Width/Height range, channel distribution | ImageStatsAccumulator | Image (header-only parsing) |
| File size min/max/mean/var/std | ImageStatsAccumulator | Image |
| Byte-length min/max/mean/var/std | BinarySizeAccumulator | UInt8Array |
| Earliest/Latest date | TemporalRangeAccumulator | Date, DateTime |
| Shannon entropy | EntropyAccumulator | Scalar, UInt8, String, JsonValue, Date, DateTime |
| Top-K coverage ratio | CategoricalDiagnosticsAccumulator | Scalar, UInt8, String, JsonValue, Date, DateTime |
| Rare category ratio | CategoricalDiagnosticsAccumulator | Scalar, UInt8, String, JsonValue, Date, DateTime |

Accumulators support `Merge()` for parallel collection using Chan et al. algorithm for combining Welford's running statistics.

### Rare category threshold

Categories with fewer than 5 observations are classified as rare. This threshold is a fixed heuristic — low enough to avoid flagging moderately infrequent values, high enough to catch singletons and near-singletons that often indicate data entry errors or extreme long-tail categories. The threshold is defined as the `RareThreshold` constant on `CategoricalDiagnosticsAccumulator`.

### Image header parsing

The `ImageStatsAccumulator` extracts dimensions and channel count from image headers without full decoding — no external image library required:

| Format | Detection |
|--------|-----------|
| JPEG | SOF0/SOF2 marker → width, height, components |
| PNG | IHDR chunk → width, height, color type → channels |
| WebP | VP8/VP8L/VP8X → width, height, alpha flag → channels |
### Column interactions

The `manifest` command also computes pairwise interaction statistics between eligible columns (Scalar, UInt8, String, JsonValue, Date, DateTime). Image, binary, and multidimensional columns are excluded.

| Measure | Pair Type | Algorithm |
|---------|-----------|----------|
| Pearson r | Numeric × Numeric | Online co-moment (West 1979), O(1) memory |
| Spearman ρ | Numeric × Numeric | Reservoir sampling (10K pairs) → rank transform → Pearson on ranks |
| Cramér's V | Categorical × Categorical | Bounded contingency table (1K categories), χ² → V |
| ANOVA F | Categorical × Numeric | Per-group Welford (1K groups), F = MS_between / MS_within |
| Mutual Information | All | Reservoir sampling (10K pairs), numeric bins (20), MI in bits |
| Theil's U | All | Asymmetric uncertainty coefficient U(A|B) = MI / H(A), derived from MI reservoir |
## Manifest

The `manifest` command generates a structured JSON manifest describing every column in a query result with type-specific statistics.

### Usage

```bash
# Print to stdout
axon manifest "SELECT * FROM data" --source csv:data=measurements.csv

# Write to file
axon manifest "SELECT * FROM data" --source csv:data=measurements.csv --output manifest.json
```

### Feature types

Each column produces a polymorphic `FeatureManifest` subclass based on its `DataKind`:

| DataKind | Manifest Type | Extra Fields |
|----------|--------------|---------------|
| Scalar, UInt8 | `NumericFeatureManifest` | min, max, mean, variance, stdDev, histogram, quantiles, zeroCount, zeroRatio, outlierCount, outlierRatio |
| String, JsonValue | `StringFeatureManifest` | minLength, maxLength |
| Vector | `VectorFeatureManifest` | minLength, maxLength, elementStats, zeroElementCount, zeroElementRatio, zeroVectorCount |
| Matrix, Tensor | `TensorFeatureManifest` | minRank, maxRank, minElementCount, maxElementCount, elementStats, zeroElementCount, zeroElementRatio, zeroVectorCount |
| Image | `ImageFeatureManifest` | width/height ranges, channelCounts, undecodableCount, fileSizeStats |
| UInt8Array | `BinaryFeatureManifest` | sizeStats (byte-length distribution) |
| Date, DateTime | `TemporalFeatureManifest` | earliest, latest (ISO 8601) |

All feature types share: `name`, `kind`, `count`, `nullCount`, `validCount`, `estimatedDistinctCount`, `isConstant`, `isNearConstant`, `topKValues`, `dominantValueRatio`, `nullRatio`, `missingRuns`, `entropy`, `entropyApproximate`.

| Derived Flag | Definition | Purpose |
|--------------|------------|---------|
| `isConstant` | `estimatedDistinctCount <= 1` | Constant columns carry no information and break many model types. |
| `isNearConstant` | `dominantValueRatio > 0.98` | A single value dominates more than 98 % of rows — the column is likely a useless feature. |

### Example output

```json
{
  "rowCount": 5000,
  "generatedAtUtc": "2026-03-15T12:00:00Z",
  "features": [
    {
      "type": "numeric",
      "name": "image_id",
      "kind": "Scalar",
      "count": 5000,
      "nullCount": 0,
      "validCount": 5000,
      "estimatedDistinctCount": 4998,
      "min": 1.0,
      "max": 581929.0,
      "mean": 291485.3,
      "variance": 28341558.2,
      "standardDeviation": 5323.7,
      "histogram": { "binEdges": [...], "counts": [...] },
      "quantiles": { "p01": 1.0, "p05": 29097.5, "p25": 145371.8, "p50": 291485.3, "p75": 436598.8, "p95": 553881.1, "p99": 581929.0 },
      "entropy": 11.2,
      "entropyApproximate": false,
      "isConstant": false,
      "isNearConstant": false,
      "dominantValueRatio": 0.0002,
      "topKValues": []
    },
    {
      "type": "image",
      "name": "file_bytes",
      "kind": "Image",
      "count": 5000,
      "nullCount": 0,
      "validCount": 5000,
      "estimatedDistinctCount": 5000,
      "minWidth": 128,
      "maxWidth": 4096,
      "minHeight": 96,
      "maxHeight": 3072,
      "channelCounts": { "3": 4950, "4": 50 },
      "undecodableCount": 0,
      "fileSizeStats": { "count": 5000, "min": 5234, "max": 2456789, "mean": 178234.5, ... },
      "topKValues": []
    }
  ],
  "interactions": [
    {
      "columnA": "image_id",
      "columnB": "width",
      "pearson": 0.02,
      "spearman": 0.03,
      "mutualInformation": 0.15,
      "theilUAB": 0.01,
      "theilUBA": 0.02
    }
  ]
}
```

### Programmatic API

```csharp
StatisticsCollector collector = new();
ColumnInteractionCollector interactionCollector = new();
// ... feed rows ...

IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
IReadOnlyList<ColumnInteractionResult> interactions = interactionCollector.GetInteractions();
Dictionary<string, DataKind> kinds = new() { ["id"] = DataKind.Scalar, ["name"] = DataKind.String };

QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, rowCount, interactions);
string json = ManifestSerializer.Serialize(manifest);
await ManifestSerializer.WriteToFileAsync(manifest, "manifest.json");
```

## EXPLAIN

The `explain` command shows the query execution plan as a tree. Two modes are supported:

### Static EXPLAIN

Shows the operator tree structure, join strategies, filter predicates, and warnings — without executing the query:

```bash
axon explain "SELECT x, y FROM data WHERE x > 0 ORDER BY x LIMIT 100" --source csv:data=measurements.csv
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
axon explain "SELECT x FROM data WHERE x > 0.5" --source csv:data=measurements.csv --analyze
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

### Programmatic API

```csharp
// Static explain
IQueryOperator plan = planner.Plan(statement);
ExplainPlanNode explainPlan = QueryExplainer.Explain(plan);
Console.WriteLine(explainPlan.Render());

// EXPLAIN ANALYZE
InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(plan);
await foreach (Row row in instrumented.ExecuteAsync(context)) { }
InstrumentedOperator.PopulateMetrics(explainPlan, instrumented);
Console.WriteLine(explainPlan.Render());
```

## Benchmarks

Run benchmarks with:

```bash
dotnet run -c Release --project benchmarks/Axon.QueryEngine.Benchmarks -- --filter "*"
```

Available benchmark suites:

| Suite | Measures |
|-------|----------|
| `ParsingBenchmarks` | SQL tokenization and parsing at various complexity levels |
| `ProviderBenchmarks` | Read throughput for CSV and JSON at 1K and 10K rows |
| `ExecutionBenchmarks` | Full query execution: scan, filter, project, join, order+limit |
| `StatisticsBenchmarks` | Statistics collection overhead and merge performance |
| `OutputBenchmarks` | CSV write throughput, with and without sharding |

Run a specific suite:

```bash
dotnet run -c Release --project benchmarks/Axon.QueryEngine.Benchmarks -- --filter "*Parsing*"
```

## Project Structure

```
Axon.QueryEngine/
  src/
    Axon.QueryEngine/             # Core library
      Model/                      # DataKind, DataValue, Row, Schema, ColumnInfo
      Parsing/                    # SQL tokenizer and parser (Superpower)
      Catalog/                    # Table catalog, providers (CSV, JSON, ZIP, HDF5, Parquet)
      Execution/                  # Query planner, operators, expression evaluator
      Functions/                  # Scalar and table-valued functions
      Statistics/                 # Column statistics with pluggable accumulators
      Output/                     # Output writers (CSV, HDF5, Parquet) with sharding
    Axon.QueryEngine.Cli/         # CLI tool (query, explore, stats commands)
  tests/
    Axon.QueryEngine.Tests/       # 600+ unit tests
  benchmarks/
    Axon.QueryEngine.Benchmarks/  # BenchmarkDotNet performance tests
```

## Building & Testing

```bash
# Build everything
dotnet build

# Run all tests
dotnet test

# Run benchmarks
dotnet run -c Release --project benchmarks/Axon.QueryEngine.Benchmarks -- --filter "*"
```

## Roadmap

The following features are architecturally accounted for but deferred from V1:

- **GROUP BY / Aggregation**: COUNT, SUM, AVG, MIN, MAX, GROUP BY, HAVING
- **Spill-to-disk joins**: Grace hash join for datasets too large for memory
- **Adaptive batch sizing**: Auto-tune based on row size estimates and available memory
- **Excel provider**: Read .xlsx files (ITableProvider interface is ready)
- **UNION / INTERSECT / EXCEPT**: Set operations between query results
- **Window functions**: ROW_NUMBER, RANK, LAG, LEAD with OVER/PARTITION BY
- **User-defined functions**: Plugin DLL support via FunctionRegistry
- **Pipe mode**: Stream results to stdout as CSV/JSON/NDJSON
- **Cost-based optimizer**: Replace greedy join heuristic with cost model
- **Index / bloom filter acceleration**: Skip non-matching partitions
- **Remote data sources**: HTTP/S3/Azure Blob providers
- **Schema caching**: Skip re-inference on repeated queries
- **EXPLAIN**: Print operator tree without executing
- **Checkpointing**: Resume failed ETL runs from the last completed shard
- **Data validation**: CHECK constraints / VALIDATE clause for data quality gates

## License

MIT
