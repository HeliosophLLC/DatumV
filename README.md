# DatumIngest™

A high-performance ML dataset query engine for .NET 10. Use SQL to extract, transform, and load data from CSV, JSON, JSONL, ZIP, HDF5, Parquet, and IDX files into ML-ready output formats with optional sharding.

## The Problem

Preparing datasets for machine learning means writing custom scripts to parse files, join sources, transform images, and normalize features — all fragile, all difficult to maintain.

## What It Does

DatumIngest™ replaces those scripts with SQL. Point it at all your sources simultaneously — CSV, JSON, JSONL, ZIP, HDF5, Parquet, IDX — and join, filter, and transform across them in a single query. It handles streaming execution, lazy evaluation, image transform fusion, and sharded output, so you focus on *what* to extract rather than *how*.

## Mental Model

```
┌──────────────┐   ┌──────────────┐   ┌──────────────────┐
│  CSV / JSON  │   │  ZIP / IDX   │   │  HDF5 / Parquet  │
│  (tabular)   │   │  (images)    │   │  (tensors)       │
└──────┬───────┘   └──────┬───────┘   └──────┬───────────┘
       │                  │                  │
       └──────────┬───────┴──────────────────┘
                  ▼
         ┌─────────────────┐
         │   SQL Query     │  JOIN, WHERE, SELECT
         │   ─────────     │  normalize(), resize(),
         │   Operators     │  softmax(), cosine_similarity()
         └────────┬────────┘
                  ▼
       ┌────────────────────────┐
       │   ML-Ready Output      │
       │   CSV · Parquet · HDF5 │
       │   (optional sharding)  │
       └────────────────────────┘
```

**Sources** are files — any mix of CSV, JSON, JSONL, ZIP, HDF5, Parquet, and IDX. Each becomes a named table. ZIP entries become rows with lazy-decompressed bytes. IDX files (MNIST, Fashion-MNIST, etc.) become tables with an `index` column for joining images with labels. All sources are available simultaneously for joins.

**Operators** are expressed as SQL. Filter with WHERE, join across formats with JOIN, transform with 200+ functions. Columns can hold scalars, strings, vectors, matrices, tensors, images, UUIDs, booleans, times, or durations — all as first-class types. Image transforms fuse automatically: `resize(grayscale(crop(img)))` decodes once and encodes once.

**Output** is an ML-ready file in CSV, Parquet, or HDF5. Add `SHARD ON` to split into fixed-size shards. Add `--checkpoint` to resume after interruption.

## Key Features

- **ML-native type system** — Vector, Matrix, Tensor, and Image are first-class column types alongside Scalar, String, Date, DateTime, Time, Duration, JsonValue, UUID, and Boolean
- **Fused image pipeline** — `resize`, `crop`, `grayscale`, `elastic_deform`, `perspective_warp`, and 10+ more transforms, with automatic decode/encode fusion across chains
- **200+ built-in functions** — `softmax`, `cosine_similarity`, `normalize`, `cyclical_encode`, vector reductions, distance metrics, ML activations, UUID generation, hashing, and more
- **Six data providers** — CSV, JSON, JSONL, ZIP (lazy decompression), HDF5, Parquet
- **Three output formats** — CSV, Parquet, HDF5 with `SHARD ON` support
- **SQL interface** — SELECT, SELECT DISTINCT, FROM, JOIN, WHERE, INTO, ORDER BY, LIMIT, CASE/WHEN, GROUP BY, DISTINCT aggregates (`COUNT(DISTINCT x)`), window functions (OVER/PARTITION BY), subqueries (scalar, IN, NOT IN, EXISTS, NOT EXISTS), Common Table Expressions (WITH / WITH RECURSIVE), set operations (UNION, INTERSECT, EXCEPT with ALL variants), quoted identifiers, parameterized queries (`$name`)
- **Dataset statistics** — HyperLogLog cardinality, Welford's online stats, histograms, quantiles, image metadata
- **JSON manifest** — structured feature manifest with column interactions (Pearson, Spearman, Cramér's V, ANOVA F, MI), auto-discovered as `.datum-manifest` sidecars for cost-model integration
- **Streaming execution** — `IAsyncEnumerable<Row>` pipeline with projection pushdown, predicate pushdown, and Parquet row group pruning via min/max statistics
- **Source indexes** — `.datum-index` sidecar files with chunk-level statistics pruning, bloom filter join acceleration, sorted value indexes for equality/range predicates, ORDER BY elimination via index scan, automatic column selection for compact types, per-column Zstd compression (5–10× size reduction), and configurable column caps
- **Memory-bounded execution** — configurable memory budget with spill-to-disk for datasets larger than available memory (hash join, ORDER BY, GROUP BY, DISTINCT, PIVOT, INTERSECT/EXCEPT); index nested-loop join for LIMIT-bounded queries with sorted indexes
- **Schema sidecars** — `.datum-schema` files cache column metadata, eliminating schema inference I/O on subsequent loads
- **Checkpointing** — resumable sharded writes via `--checkpoint`
- **Schema introspection** — resolve column metadata without executing (for editor autocomplete)
- **EXPLAIN / EXPLAIN ANALYZE** — inspect query plans with manifest-driven cardinality estimation (NDV-based selectivity) and optional runtime metrics
- **Parameterized queries** — named `$parameter` placeholders with early binding (AST-level substitution before planning), preserving all optimizer paths
- **Resource governance** — per-session query deadlines, row budgets, Query Unit metering, and throttle delays for multi-tenant gRPC deployments

## Why Not DuckDB / Pandas?

DuckDB and Pandas are excellent general-purpose tools. DatumIngest is purpose-built for the ML dataset preparation gap they don't cover:

- **Image transforms in SQL** — `resize`, `crop`, `grayscale`, `elastic_deform`, `perspective_warp`, and 10+ more, with automatic decode/encode fusion across chained transforms. DuckDB and Pandas have no image pipeline.
- **ZIP archives as tables** — `FROM images` where `images` is a ZIP file. Each entry becomes a row with lazy decompression. No extract step.
- **ML-native types** — Vector, Matrix, Tensor, and Image are first-class column types with dedicated functions (`softmax`, `cosine_similarity`, `cyclical_encode`, `normalize`).
- **Dataset manifests** — one command generates a JSON manifest with per-column statistics, histograms, quantiles, and pairwise column interactions. Sidecar `.datum-manifest` and `.datum-schema` files are auto-discovered and feed back into the query planner for data-driven cardinality estimation and cached schema resolution.
- **Sharded output with checkpointing** — write to sharded CSV/Parquet/HDF5 and resume from the last completed shard after a crash.

If you need billion-row analytics, use DuckDB. If you need to join a ZIP of images with a JSON annotation file, resize everything to 224×224, and write sharded HDF5 — that's DatumIngest.

## Quick Start

### 1. Query a CSV file

```bash
datum-ingest explore "SELECT name, score FROM data WHERE score > 90" --source "data=./iris.csv"
```

### 2. Join ZIP + JSON, transform images, output to HDF5

```bash
datum-ingest query "
  SELECT resize_and_crop(load_image(img.file_bytes), 224, 224, 'center') AS image,
         cap.caption
  FROM images AS image_files
  INNER JOIN captions AS image_captions ON image_captions.file_name = GET_FILENAME(image_files.file_name)
  INNER JOIN captions AS cap ON img.id = image_captions.image_id
  WHERE len(cap.caption) > 20
  INTO 'dataset.h5'
" --source "images=./train2017.zip" --source "captions=./captions.json"
```

### 3. ETL with sharded output

```bash
datum-ingest query "
  SELECT id, normalize(value) AS norm_value, category
  FROM data
  INTO 'output/result.csv' SHARD ON sample_count 10000
" --source "data=./large_dataset.csv"
```

## Programmatic Usage

```csharp
// Parse SQL and build the execution plan
SelectStatement statement = SqlParser.Parse(
    "SELECT name, normalize(score) AS score FROM data WHERE score > 0.5");

TableCatalog catalog = new();
catalog.Register("data", "./data.csv");

FunctionRegistry functions = FunctionRegistry.CreateDefault();
QueryPlanner planner = new(catalog, functions);
IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

// Stream results to CSV
ExecutionContext context = new(CancellationToken.None, functions, catalog);
using MemoryStream stream = new();
await using CsvOutputWriter writer = new(stream);

bool schemaInitialized = false;
await foreach (Row row in plan.ExecuteAsync(context))
{
    if (!schemaInitialized)
    {
        Schema schema = InferSchema(row);
        await writer.InitializeAsync(schema);
        schemaInitialized = true;
    }

    await writer.WriteRowAsync(row);
}

OutputSummary summary = await writer.FinalizeAsync();
```

See [docs/api.md](docs/api.md) for the full programmatic API (manifest, EXPLAIN, schema, checkpointing).

## CLI Reference

### Commands

| Command | Description |
|---------|-------------|
| `query` | Execute a SQL query. Supports INTO for file output. |
| `explore` | Print the first N rows of a query result to the console. |
| `stats` | Execute a query and print per-column statistics. |
| `explain` | Show the query execution plan without running it. |
| `manifest` | Generate a JSON manifest with per-column feature statistics. |
| `schema` | Show the column schema of a query's FROM/JOIN sources without executing. |
| `index` | Build a `.datum-index` sidecar for one or more source files. |

### Flags

| Flag | Description |
|------|-------------|
| `--catalog <path>` | Path to a JSON catalog file defining table sources. |
| `--source <def>` | Inline source definition. Format: `name=path` (auto-detect), `provider:name=path[;key=value]` (explicit), or a directory path to auto-discover all supported files. Repeatable. |
| `--limit <n>` | Row limit for explore mode (default: 10). |
| `--analyze` | Run EXPLAIN ANALYZE: execute the query and report actual row counts and timing. |
| `--output <path>` | Write manifest output to a file instead of stdout (manifest command). |
| `--checkpoint` | Enable checkpoint-based resume for sharded writes. Requires `SHARD ON`. |
| `--param <key=value>` | Bind a named parameter. Repeatable. Value types are inferred: numeric → Scalar, `true`/`false` → Boolean, `null` → Null, else → String. |
| `--index <path>` | Load a pre-built `.datum-index` file for chunk-level query pruning. Repeatable. |
| `--with-index` | Co-generate a `.datum-index` for each source during query execution. |
| `--chunk-size <n>` | Rows per index chunk (default: 10,000). |
| `--bloom-columns <cols>` | Comma-separated column names to build bloom filters for. |
| `--index-columns <cols>` | Comma-separated column names to build sorted value indexes for. |
| `--memory-budget <bytes>` | Memory budget in bytes for spill-to-disk operators (default: 2 GB; 0 disables). |

### Source definition format

```
provider:name=path[;key=value;...]
```

Examples:
```
csv:data=./data.csv;delimiter=,;header=true
json:annotations=./coco.json
jsonl:records=./data.jsonl
zip:images=./train2017.zip
hdf5:features=./embeddings.h5
parquet:labels=./labels.parquet
```

When `--source` is given a directory path, all supported files in that directory are auto-discovered and registered as tables. Table names are derived from filenames (e.g. `order_products__prior.csv.datum` becomes `order_products__prior.csv`). Sidecars (`.datum-index`, `.datum-manifest`, `.datum-schema`) are matched automatically.

```bash
datum-ingest shell --source ./datasets/my-project
datum-ingest explore "SELECT * FROM [orders.csv] LIMIT 10" --source ./datasets/my-project
```

## Documentation

| Document | Contents |
|----------|----------|
| [docs/sql.md](docs/sql.md) | SQL dialect reference, type system, EXPLAIN, schema introspection |
| [docs/functions.md](docs/functions.md) | All 200+ functions: math, string, JSON, temporal, image, vector, UUID, hashing, window |
| [docs/providers.md](docs/providers.md) | Data provider details, options, catalog file format |
| [docs/statistics.md](docs/statistics.md) | Statistics accumulators, manifest schema, column interactions |
| [docs/indexes.md](docs/indexes.md) | Source indexes: `.datum-index` format, bloom filters, sorted values, CLI usage |
| [docs/architecture.md](docs/architecture.md) | Execution model, lazy evaluation, pushdown, project structure |
| [docs/language-server.md](docs/language-server.md) | SQL language server: autocomplete, diagnostics, hover, WASM integration |
| [docs/api.md](docs/api.md) | Programmatic C# API: manifest, EXPLAIN, schema, checkpointing, streaming output |
| [docs/compute.md](docs/compute.md) | gRPC compute library: embedding, remote sessions, streaming queries, admin operations, resource governance |
| [ROADMAP.md](ROADMAP.md) | Deferred features for future releases |

## Installation

### As a .NET library

```bash
dotnet add package DatumIngest
```

### As a gRPC compute service

```bash
dotnet add package DatumIngest.Compute
```

See [docs/compute.md](docs/compute.md) for embedding in an ASP.NET host.

### As a gRPC client

```bash
dotnet add package DatumIngest.Compute.Client
```

See [docs/compute.md](docs/compute.md#net-client-package) for connecting to a running compute backend.

### As a CLI tool

```bash
dotnet tool install --global DatumIngest.Cli
```

### Build from source

```bash
git clone https://github.com/your-org/DatumIngest.git
cd DatumIngest
dotnet build
dotnet test
```

#### Publish a self-contained single-file binary

```bash
dotnet publish src/DatumIngest.Cli -c Release -r win-x64
dotnet publish src/DatumIngest.Cli -c Release -r linux-x64
dotnet publish src/DatumIngest.Cli -c Release -r osx-arm64
```

## Benchmarks

Run benchmarks with:

```bash
dotnet run -c Release --project benchmarks/DatumIngest.Benchmarks -- --filter "*"
```

Available benchmark suites:

| Suite | Measures |
|-------|----------|
| `ParsingBenchmarks` | SQL tokenization and parsing at various complexity levels |
| `ProviderBenchmarks` | Read throughput for CSV and JSON at 1K and 10K rows |
| `ExecutionBenchmarks` | Full query execution: scan, filter, project, join, order+limit, subqueries, DISTINCT, set operations |
| `PivotBenchmarks` | PIVOT and UNPIVOT reshaping at 5K and 20K rows, explicit vs auto-discover, multiple aggregates |
| `StatisticsBenchmarks` | Statistics collection overhead and merge performance |
| `OutputBenchmarks` | CSV write throughput, with and without sharding |

Run a specific suite:

```bash
dotnet run -c Release --project benchmarks/DatumIngest.Benchmarks -- --filter "*Parsing*"
```

### Results

**TL;DR** — Parse a query in **42 μs**, read 10K CSV rows in **7 ms**, execute a full scan in **7 ms**, JOIN 10K × 1K rows in **9 ms**, SELECT DISTINCT (low cardinality) in **6 ms**, SELECT DISTINCT (high cardinality) in **17 ms**, COUNT(DISTINCT) per group in **9 ms**, IN subquery in **8 ms**, EXISTS semi-join in **8 ms**, NOT EXISTS in **10 ms**, CTE inlined in **9 ms**, CTE materialized multi-ref in **7 ms**, recursive CTE 1K iterations in **2 ms**, UNION ALL (10K + 10K) in **16 ms**, UNION DISTINCT (10K + 10K) in **42 ms**, INTERSECT in **12 ms**, EXCEPT in **24 ms**, PIVOT 5K rows in **10 ms** (explicit) / **11 ms** (auto-discover), UNPIVOT 1250 wide rows in **2 ms**, collect all statistics in **53 ms**, and write 10K rows to CSV in **9 ms**. Memory stays under **10 MB** for a 10K-row single-table pipeline.

> BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8039)
> Intel Core i9-10900X CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
> .NET SDK 10.0.103, .NET 10.0.3 (10.0.326.7603), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

#### Parsing

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| Tokenize simple SELECT | 7.556 μs | 0.0934 μs | 0.1181 μs | 2.13 KB |
| Tokenize with WHERE | 20.044 μs | 0.0805 μs | 0.0672 μs | 6.36 KB |
| Tokenize with JOIN | 45.148 μs | 0.2848 μs | 0.2664 μs | 12.05 KB |
| Tokenize complex query | 83.420 μs | 1.6631 μs | 3.3596 μs | 26.87 KB |
| Parse simple SELECT | 41.544 μs | 0.3153 μs | 0.2949 μs | 31.11 KB |
| Parse with WHERE | 86.334 μs | 1.7021 μs | 2.3298 μs | 61.48 KB |
| Parse with JOIN | 137.238 μs | 0.5440 μs | 0.4823 μs | 82.75 KB |
| Parse complex query | 254.517 μs | 1.0853 μs | 0.9063 μs | 162.09 KB |
| Parse subquery | 130.737 μs | 1.3163 μs | 1.4630 μs | 93.58 KB |
| Parse IN subquery | 96.006 μs | 0.4166 μs | 0.3479 μs | 69.95 KB |
| Parse EXISTS subquery | 116.058 μs | 0.7563 μs | 0.6705 μs | 80.13 KB |
| Parse scalar subquery | 126.574 μs | 0.8453 μs | 0.6600 μs | 89.12 KB |
| Parse DISTINCT aggregate | 115.544 μs | 0.5089 μs | 0.4250 μs | 80.37 KB |
| Parse simple CTE | 123.180 μs | 0.9941 μs | 1.1049 μs | 89.39 KB |
| Parse recursive CTE | 114.135 μs | 0.3712 μs | 0.3099 μs | 81.41 KB |
| Parse multi-CTE | 257.154 μs | 1.1421 μs | 1.0125 μs | 176.99 KB |

Parsing scales linearly with query complexity. Tokenization is ~3–5× faster than full parsing for the same input. IN/EXISTS subqueries parse faster than derived-table subqueries due to simpler AST structure. DISTINCT aggregate queries parse in ~116 μs — comparable to EXISTS subqueries, since the DISTINCT modifier adds minimal AST overhead per aggregate. Simple and recursive CTEs parse in ~114–123 μs, on par with single subqueries, since the WITH clause adds one definition header plus a standard SELECT body. Multi-CTE queries (~257 μs) scale linearly — three chained definitions cost roughly 3× a single CTE, consistent with the additive nature of definition parsing. The addition of compound-query grammar paths (UNION/INTERSECT/EXCEPT) adds ~15% overhead to parse times even for non-compound queries, since the parser now wraps every statement in a `QueryExpression` and attempts compound continuation at the top level.

#### Providers

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| CSV 1K rows | 1,512.0 μs | 29.7 μs | 55.1 μs | 943.96 KB |
| CSV 10K rows | 6,924.0 μs | 62.7 μs | 58.7 μs | 8,713.24 KB |
| CSV 1K with projection | 1,369.0 μs | 25.5 μs | 23.9 μs | 780.17 KB |
| JSON 1K rows | 1,667.0 μs | 32.6 μs | 59.6 μs | 499.84 KB |
| JSON 10K rows | 11,591.0 μs | 67.4 μs | 59.8 μs | 4,522.20 KB |

All providers share a pre-built column name index across rows, eliminating per-row Dictionary allocations at the source. `DataValue.FromScalar` caches the two most common float values (0 and 1), avoiding heap allocation and boxing for those constants. The CSV parser reuses a thread-local field buffer across rows, eliminating a `List<string>` allocation per line. Projection pushdown on CSV saves ~18% of allocated memory by skipping unreferenced columns.

#### Execution

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| SELECT * FROM data (10K) | 7.364 ms | 0.1225 ms | 0.1146 ms | 8.56 MB |
| SELECT with WHERE filter (10K) | 8.481 ms | 0.1212 ms | 0.1012 ms | 8.49 MB |
| SELECT with projection (10K) | 6.911 ms | 0.1243 ms | 0.1102 ms | 7.51 MB |
| INNER JOIN (10K × 1K) | 9.311 ms | 0.0686 ms | 0.0642 ms | 8.65 MB |
| ORDER BY + LIMIT (10K) | 9.324 ms | 0.1282 ms | 0.1070 ms | 8.83 MB |
| Uncorrelated IN subquery (10K × 1K) | 8.297 ms | 0.1288 ms | 0.1141 ms | 7.65 MB |
| Correlated EXISTS semi-join (10K × 1K) | 8.401 ms | 0.1672 ms | 0.1643 ms | 7.70 MB |
| Correlated NOT EXISTS anti-semi-join (10K × 1K) | 10.276 ms | 0.1397 ms | 0.1166 ms | 8.40 MB |
| Correlated scalar subquery (10K × 1K) | 11.641 ms | 0.1794 ms | 0.1678 ms | 9.85 MB |
| SELECT DISTINCT low cardinality (10K) | 6.114 ms | 0.1181 ms | 0.1104 ms | 6.74 MB |
| SELECT DISTINCT high cardinality (10K) | 17.491 ms | 0.3467 ms | 0.7896 ms | 8.79 MB |
| COUNT(DISTINCT) per group (10K) | 9.212 ms | 0.1742 ms | 0.4496 ms | 7.59 MB |
| CTE inlined single ref (10K) | 8.803 ms | 0.1162 ms | 0.1087 ms | 8.92 MB |
| CTE materialized multi-ref (10K) | 7.300 ms | 0.1049 ms | 0.0876 ms | 7.19 MB |
| Multi-CTE chained (10K) | 10.218 ms | 0.1202 ms | 0.1125 ms | 9.38 MB |
| Recursive CTE 100 iterations | 0.322 ms | 0.0028 ms | 0.0025 ms | 0.57 MB |
| Recursive CTE 1000 iterations | 2.008 ms | 0.0260 ms | 0.0244 ms | 4.60 MB |
| UNION ALL two tables (10K + 10K) | 16.008 ms | 0.1147 ms | 0.0958 ms | 16.53 MB |
| UNION DISTINCT two tables (10K + 10K) | 41.769 ms | 0.5214 ms | 0.4071 ms | 19.32 MB |
| INTERSECT DISTINCT two tables (10K × 10K) | 12.488 ms | 0.2355 ms | 0.2203 ms | 13.44 MB |
| EXCEPT DISTINCT two tables (10K \ 10K) | 23.877 ms | 0.4496 ms | 0.4617 ms | 16.65 MB |
| UNION ALL same table filtered (10K) | 19.460 ms | 0.3710 ms | 0.3470 ms | 16.43 MB |
| Chained UNION ALL three-way (10K + 10K + 10K) | 20.682 ms | 0.3159 ms | 0.2800 ms | 22.46 MB |

Full scan of 10K rows allocates 8.56 MB — shared provider schemas and operator schemas eliminated per-row Dictionary and name-array allocations end-to-end. Boolean-valued `DataValue` instances (0 and 1) are cached singletons, eliminating heap allocation and float boxing in every comparison, filter, and join condition evaluation. Qualified column references (`table.column`) cache their pre-computed lookup string on the AST node, avoiding per-row string interpolation. CAST expressions pool their argument arrays and cache target-type `DataValue` wrappers. Function argument arrays are pooled via `ArrayPool`, and LIKE patterns use cached compiled regex. ORDER BY + LIMIT uses a bounded priority queue when LIMIT is present.

All subquery types now execute at JOIN-competitive speeds. Uncorrelated IN subqueries are constant-folded to a literal list at plan time, then evaluated via a lazily-built `HashSet<DataValue>` for O(1) lookups per row (~8 ms, on par with WHERE filter). Correlated EXISTS and NOT EXISTS are rewritten into hash-based semi-joins and anti-semi-joins (~8–10 ms). Correlated scalar subqueries are decorrelated into hash-based lookups (~12 ms, 9.85 MB).

SELECT DISTINCT uses a streaming hash-dedup operator backed by `HashSet<CompositeKey>`. Low-cardinality DISTINCT (5 unique categories from 10K rows) completes in ~6 ms — faster than a full scan because only 5 rows pass through downstream operators. High-cardinality DISTINCT (10K unique combinations) takes ~17 ms, reflecting the cost of hashing and storing every row; the Gen2 GC pressure shows the hash set's large-object overhead. COUNT(DISTINCT) per group runs in ~9 ms via `DistinctAccumulatorDecorator`, which wraps each aggregate accumulator with a per-group `HashSet` filter — comparable to a standard GROUP BY since the dedup cost is amortized across groups.

CTEs execute with minimal overhead relative to equivalent inline queries. A single-reference CTE (~9 ms, 8.92 MB) performs on par with a filtered scan, since the planner inlines it directly into the query plan. Multi-reference CTEs (~7 ms, 7.19 MB) benefit from materialization — the CTE result is computed once and served from an in-memory cache on subsequent reads, avoiding redundant scans. Chained multi-CTE queries (~10 ms) scale linearly with the number of definitions. Recursive CTEs demonstrate efficient iteration: 100 iterations complete in ~0.3 ms and 1,000 iterations in ~2 ms, with memory scaling linearly at ~4.6 KB per iteration. The recursive operator uses a queue-based breadth-first expansion with automatic cycle termination.

Set operations use hash-based strategies. **UNION ALL** is a zero-overhead concatenation of both streams (~16 ms for 10K + 10K, allocating 16.53 MB — exactly 2× a single scan). **UNION DISTINCT** concatenates with streaming hash deduplication (~42 ms, 19.32 MB), with spill-to-disk support when the memory budget is exceeded. **INTERSECT DISTINCT** materialises the right branch into a hash set, then probes with the left (~12 ms) — fast because the low-cardinality category column produces only 5 unique rows. **EXCEPT DISTINCT** follows the same materialise-right/probe-left pattern but must also track emitted rows for deduplication (~24 ms, 16.65 MB). All four INTERSECT/EXCEPT variants support spill-to-disk when the memory budget is exceeded during right-branch materialisation, using grace-hash partitioning. Three-way chained UNION ALL (~21 ms) scales linearly — each additional branch adds roughly one scan's worth of time and memory.

#### Pivot

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| PIVOT explicit IN list, AVG(revenue) (5K rows → 1250 output) | 10.212 ms | 0.2024 ms | 0.2560 ms | 5.98 MB |
| PIVOT explicit IN list, AVG(revenue) (20K rows → 5000 output) | 58.515 ms | 1.1296 ms | 2.4556 ms | 23.62 MB |
| PIVOT auto-discover, AVG(revenue) (5K rows → 1250 output) | 10.733 ms | 0.1028 ms | 0.0858 ms | 5.95 MB |
| PIVOT auto-discover, AVG(revenue) (20K rows → 5000 output) | 48.055 ms | 0.8086 ms | 1.8252 ms | 23.58 MB |
| PIVOT two aggregates SUM+COUNT (5K rows → 1250 output, 8 value cols) | 12.695 ms | 0.2349 ms | 0.2197 ms | 6.92 MB |
| UNPIVOT 4 columns (1250 wide rows → 5000 output) | 2.278 ms | 0.0437 ms | 0.0467 ms | 1.86 MB |
| UNPIVOT 4 columns (5000 wide rows → 20K output) | 6.108 ms | 0.0589 ms | 0.0551 ms | 6.99 MB |
| UNPIVOT INCLUDE NULLS 4 columns (1250 wide rows) | 2.276 ms | 0.0318 ms | 0.0282 ms | 1.86 MB |

PIVOT is a blocking operator that must buffer all input rows before emitting any output; memory scales linearly at ~1.2 MB per 1K output rows. Auto-discover mode performs comparably to explicit `IN` lists for small datasets and slightly faster at 20K rows, since the value-index dictionary is built lazily from actual data rather than pre-allocated from the literal list. UNPIVOT is a streaming operator and is ~4–5× faster than PIVOT at equivalent input sizes, allocating only what the output rows require. `INCLUDE NULLS` adds no measurable overhead — the flag is a single branch per cell. Multiple aggregates in one PIVOT clause increase memory ~15% (6.92 MB vs 5.98 MB at 5K rows) due to the proportionally wider output schema.

#### Statistics

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| Collect stats 1K rows | 3.072 ms | 0.0324 ms | 0.0270 ms | 2.36 MB |
| Collect stats 10K rows | 53.420 ms | 1.0670 ms | 1.1860 ms | 20.79 MB |
| Merge two 5K collectors | 62.402 ms | 1.2610 ms | 3.7182 ms | 24.95 MB |

Statistics collection runs all accumulators (numeric, string, vector, image, cardinality, entropy, histogram, quantile, top-K) in a single pass. Merge cost is comparable to a fresh 10K collection because both collectors' reservoir samples and HyperLogLog registers must be combined.

#### Output

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| CSV write 1K rows | 1.413 ms | 0.0260 ms | 0.0396 ms | 124.47 KB |
| CSV write 10K rows | 9.213 ms | 0.1271 ms | 0.1189 ms | 1,145.65 KB |
| CSV write 10K rows with sharding (1000/shard) | 15.660 ms | 0.2413 ms | 0.2015 ms | 1,256.36 KB |

CSV writes with minimal allocation overhead (~115 bytes/row). Vector columns use a direct `StringBuilder` loop instead of LINQ enumerable formatting. Sharding adds overhead due to repeated file creation and header writes, but memory usage increases only ~10% since each shard flushes independently.

## Building & Testing

```bash
# Build everything
dotnet build

# Run all tests
dotnet test

# Run benchmarks
dotnet run -c Release --project benchmarks/DatumIngest.Benchmarks -- --filter "*"
```

## License

MIT

---

DatumIngest™ is a trademark of Heliosoph LLC.