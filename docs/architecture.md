# Architecture

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Programmatic API](api.md)

DatumIngest executes queries as streaming `IAsyncEnumerable<Row>` pipelines with lazy evaluation and projection pushdown.

## Streaming pipeline

Queries execute as an `IAsyncEnumerable<Row>` pipeline:

```
ScanOperator → FilterOperator → ProjectOperator → OrderByOperator → LimitOperator
```

Each operator pulls rows from its child on demand. No intermediate materialization unless required (ORDER BY, JOIN build side).

## Lazy evaluation

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

## Projection and predicate pushdown

The query planner analyzes column references in SELECT/WHERE/ON and passes required-column sets down to `ScanOperator`, allowing providers to skip unreferenced columns. WHERE predicates referencing only one table are pushed below JoinOperator.

## Join implementation

Hash join for INNER, LEFT, RIGHT, and FULL OUTER joins:
- Build side: smaller table materialized into `Dictionary<DataValue, List<Row>>`
- Probe side: larger table streamed row-by-row against the hash table
- NULL keys never match (SQL semantics)
- CROSS JOIN uses nested loop (cartesian product)

## Project structure

```
DatumIngest/
  src/
    DatumIngest/             # Core library
      Model/                      # DataKind, DataValue, Row, Schema, ColumnInfo
      Parsing/                    # SQL tokenizer and parser (Superpower)
      Catalog/                    # Table catalog, providers (CSV, JSON, JSONL, ZIP, HDF5, Parquet)
      Execution/                  # Query planner, operators, expression evaluator
      Functions/                  # Scalar and table-valued functions
      Statistics/                 # Column statistics with pluggable accumulators
      Output/                     # Output writers (CSV, HDF5, Parquet) with sharding
    DatumIngest.Cli/         # CLI tool (query, explore, stats, schema commands)
  tests/
    DatumIngest.Tests/       # 1,600+ unit tests
  benchmarks/
    DatumIngest.Benchmarks/  # BenchmarkDotNet performance tests
```
