# Architecture

[← Back to README](../README.md) · [SQL Reference](sql/select.md) · [Functions](functions/string.md) · [Value Representation](value-representation.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Star Schema](star-schema.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest executes queries as streaming `IAsyncEnumerable<Row>` pipelines with lazy evaluation and projection pushdown.

## Streaming pipeline

Queries execute as an `IAsyncEnumerable<Row>` pipeline:

```
ScanOperator → FilterOperator → ProjectOperator → OrderByOperator → LimitOperator
```

Each operator pulls rows from its child on demand. No intermediate materialization unless required (ORDER BY, JOIN build side). When ORDER BY is combined with LIMIT, a bounded max-heap retains only the top N rows without materializing the full result set.

When a sorted value index exists for the ORDER BY column and the provider supports seeking (`ISeekableTableProvider`), the planner substitutes an `IndexScanOperator` that walks the index in sorted order and fetches rows via random-access reads. This eliminates both the `ScanOperator` and `OrderByOperator`, producing sorted output without materialization:

```
IndexScanOperator → FilterOperator → ProjectOperator → LimitOperator
```

See [ORDER BY optimization](indexes.md#order-by-optimization) for details.

## Lazy evaluation

`ProjectOperator` wraps all SELECT expression results as `LazyDataValue` thunks. Values materialize only when accessed:

```sql
-- load_image is NOT called here...
SELECT load_image(file_bytes) AS raw_image FROM archive

-- ...it is called here, only for rows passing the WHERE filter
SELECT resize(raw_image, 224, 224) FROM (
  SELECT load_image(file_bytes) AS raw_image, caption FROM archive
) AS inner
WHERE length(caption) > 20
```

## Projection and predicate pushdown

The query planner analyzes column references in SELECT/WHERE/ON and passes required-column sets down to `ScanOperator`, allowing providers to skip unreferenced columns. WHERE predicates referencing only one table are pushed below JoinOperator.

## Partition and chunk pruning

Pushed-down WHERE predicates are forwarded to providers that implement `IFilterableTableProvider` as advisory filter hints. The Parquet provider uses this interface to read each row group's min/max column statistics from the file footer and skip row groups whose statistics prove the predicate unsatisfiable.

For any format, `.datum-index` sidecar files extend this capability to chunk-level pruning — including CSV, JSONL, JSON, and ZIP sources. When a source index is loaded, `ScanOperator` applies up to three pruning levels (statistics, bloom filters, sorted value indexes) to skip entire chunks before reading source data. For equality predicates on sorted-indexed columns, providers that support seeking (`ISeekableTableProvider`) skip chunk-level reading entirely and seek directly to matching rows via exact index lookup. See [Source Indexes](indexes.md) for the full specification.

Supported predicate shapes include comparisons, BETWEEN, IN, IS NULL/IS NOT NULL, and AND/OR compositions. EXPLAIN shows the filter hint on the scan node; EXPLAIN ANALYZE reports how many chunks or row groups were pruned.

## Cardinality estimation

The `QueryExplainer` produces estimated row counts for each operator in the plan tree. Base estimates come from provider capabilities (Parquet, HDF5, IDX report row counts from metadata). When a `.datum-manifest` sidecar file is available, its `RowCount` overrides the provider's estimate — enabling accurate row counts for CSV, JSON, JSONL, and ZIP sources.

Per-column statistics from the manifest (estimated distinct count via HyperLogLog, null ratio) drive data-aware selectivity estimation for equality, IN, IS NULL, and equi-join predicates. Without a manifest, the cost model falls back to fixed heuristics (10% equality, 33% range, etc.). See [Statistics & Manifest — Planning Integration](statistics.md#planning-integration) for the full selectivity table.

## Join implementation

Hash join for INNER, LEFT, RIGHT, and FULL OUTER joins:
- Build side: smaller table materialized into `Dictionary<DataValue, List<Row>>`
- Probe side: larger table streamed row-by-row against the hash table
- NULL keys never match (SQL semantics)
- CROSS JOIN uses nested loop (cartesian product)

### Grace hash join (spill-to-disk)

When `ExecutionContext.MemoryBudgetBytes` is set, equi-joins use a Grace hash join that can spill partitions to temporary files when estimated memory usage exceeds the budget. The operator partitions both build and probe sides into buckets; if a bucket's build side exceeds the memory threshold, it is written to disk and replayed during probing. This allows joining datasets larger than available memory at the cost of additional I/O.

The memory budget is configurable per deployment:
- **CLI**: 2 GB default (`--memory-budget` flag, `--memory-budget 0` disables)
- **Compute backend (gRPC)**: 256 MB default per session
- **Programmatic API**: set via `ExecutionContext` constructor

### Index nested-loop join

For small result sets, the planner can substitute an index nested-loop join (NLJ) instead of a hash join. This strategy is chosen when all of the following conditions are met:

1. A `LIMIT` clause caps output to ≤ 1,000 rows (`RowLimit` hint)
2. The join is `INNER` or `LEFT SEMI` with a single-key equi-predicate
3. The build side has a sorted value index on the join key column
4. The build-side provider supports seeking (`ISeekableTableProvider`)

When eligible, the NLJ streams probe-side rows and performs O(log n) index lookups on the build side for each probe key, avoiding full materialization of the build side. This is substantially faster than a hash join when the probe side is small and most build-side rows are never touched.

The `LimitOperator` propagates the `RowLimit` hint (LIMIT + OFFSET) downstream through the `ExecutionContext`, allowing the `JoinOperator` to see that only a bounded result set is needed and select the NLJ strategy accordingly.

## Project structure

```
DatumIngest/
  src/
    DatumIngest/             # Core library
      Model/                      # DataKind, DataValue, Row, Schema, ColumnInfo, SourceSchema
      Parsing/                    # SQL tokenizer and parser (Superpower)
      Catalog/                    # Table catalog, providers (CSV, JSON, JSONL, ZIP, HDF5, Parquet, .datum), DDL/DML executors, IPrimaryKeyLookup
      Execution/                  # Query planner, operators, expression evaluator
      Functions/                  # Scalar and table-valued functions
      Indexing/                   # Source indexes, bloom filters, B+Tree indexes (bulk + mutable PK), bitmap indexes, binary I/O
      Manifest/                   # Manifest model, serialization, schema matching
      Statistics/                 # Column statistics with pluggable accumulators
      Analysis/                   # SourceAnalyzer: single-pass co-generation of schema, index, manifest
      Output/                     # Output writers (CSV, HDF5, Parquet) with sharding
    DatumIngest.Parsing/     # Extracted tokenizer/parser (shared by core and language server)
    DatumIngest.Server/      # Session engine: SessionManager, CommandDispatcher, IDatasetStore
    DatumIngest.Compute/     # gRPC service library wrapping the server engine (embeddable)
    DatumIngest.LanguageServer/  # SQL language service: completion, diagnostics, hover
    DatumIngest.Editor/      # SignalR hub for server-side language intelligence
    DatumIngest.Wasm/        # Blazor WebAssembly host with JSInvokable interop
    DatumIngest.Cli/         # CLI tool (query, explore, stats, schema, index commands)
  tests/
    DatumIngest.Tests/       # 3,200+ unit tests
  benchmarks/
    DatumIngest.Benchmarks/  # BenchmarkDotNet performance tests
```
