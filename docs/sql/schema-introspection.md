---
title: Schema Introspection
---

## Why Use This

Before you write a query, you need to know what you're working with -- what tables are available, what columns they have, and what types those columns are. Schema introspection answers these questions without trial and error.

## Quick Start

Three questions you will ask constantly:

**"What tables do I have?"**

```sql
SELECT table_name FROM information_schema.tables
```

**"What columns does this table have?"**

```sql
SELECT column_name, data_type
FROM information_schema.columns
WHERE table_name = 'orders'
ORDER BY ordinal_position
```

**"What functions are available for strings?"**

```sql
SELECT function_name, description
FROM datum_catalog.functions
WHERE category = 'String'
ORDER BY function_name
```

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

## See Also

- [EXPLAIN](explain.md)
- [DDL / DML](ddl-dml.md)
- [Type System](type-system.md)
