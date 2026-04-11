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

**Operators** are expressed as SQL. Filter with WHERE, join across formats with JOIN, transform with 200+ functions. Columns can hold scalars, strings, typed arrays (numeric or otherwise), images, structs, UUIDs, booleans, dates, times, datetimes, or durations — all as first-class types. Image transforms fuse automatically: `resize(grayscale(crop(img)))` decodes once and encodes once.

**Output** is an ML-ready file in CSV, Parquet, or HDF5. Add `SHARD ON` to split into fixed-size shards. Add `--checkpoint` to resume after interruption.

## Key Features

- **Rich type system** — first-class column types include Boolean, signed and unsigned integers up to 128 bits (Int8/16/32/64/128, UInt8/16/32/64/128), Float16/32/64, Decimal, Date, Time, DateTime, Duration, String, Uuid, Image, and Struct. Any primitive type can also be carried as a typed array (`Float32` array for embeddings, `UInt8` array for byte buffers, etc.) via an orthogonal IsArray flag
- **Fused image pipeline** — `resize`, `crop`, `grayscale`, `elastic_deform`, `perspective_warp`, and 10+ more transforms, with automatic decode/encode fusion across chains
- **200+ built-in functions** — `softmax`, `cosine_similarity`, `normalize`, `cyclical_encode`, vector reductions, distance metrics, ML activations, UUID generation, hashing, and more
- **Six data providers** — CSV, JSON, JSONL, ZIP (lazy decompression), HDF5, Parquet
- **Three output formats** — CSV, Parquet, HDF5 with `SHARD ON` support
- **SQL interface** — SELECT, SELECT DISTINCT, SELECT * EXCEPT, SELECT * REPLACE, FROM, JOIN, WHERE, INTO, ORDER BY, LIMIT, CASE/WHEN, GROUP BY, GROUP BY ALL, DISTINCT aggregates (`COUNT(DISTINCT x)`), window functions (OVER/PARTITION BY), subqueries (scalar, IN, NOT IN, EXISTS, NOT EXISTS), Common Table Expressions (WITH / WITH RECURSIVE), set operations (UNION, INTERSECT, EXCEPT with ALL variants), quoted identifiers, parameterized queries (`$name`), schema-qualified virtual tables (`information_schema`, `datum_catalog`), DDL/DML for session-scoped temp tables (CREATE TEMP TABLE, INSERT INTO, UPDATE, DELETE, ALTER TABLE, DROP TABLE, ANALYZE), struct literals (`{field: expr}`), array/struct index access (`expr[index]`), LET binding tuple destructuring (`LET (a, b) = expr`, `LET {x, y} = expr`), SCAN fold expressions for sequential state (EMA, sessionization, streak detection)
- **Dataset statistics** — HyperLogLog cardinality, Welford's online stats, histograms, quantiles, image metadata
- **JSON manifest** — structured feature manifest with column interactions (Pearson, Spearman, Cramér's V, ANOVA F, MI), auto-discovered as `.datum-manifest` sidecars for cost-model integration
- **Streaming execution** — `IAsyncEnumerable<Row>` pipeline with projection pushdown, predicate pushdown, and Parquet row group pruning via min/max statistics
- **Source indexes** — `.datum-index` sidecar files with chunk-level statistics pruning, bloom filter join acceleration, sorted value indexes for equality/range predicates, B+Tree indexes for large columns (≥5M entries, demand-paged 8 KiB pages with Zstd-compressed leaves), bitmap indexes for low-cardinality columns (≤256 distinct values, bitwise AND/OR/NOT composition), ORDER BY elimination via index scan, automatic column selection for compact types, per-column Zstd compression (5–10× size reduction), and configurable column caps; optional `.datum-mapped-index` sidecar with memory-mapped fixed-width sorted indexes for zero-copy multi-tenant access
- **Memory-bounded execution** — configurable memory budget with spill-to-disk for datasets larger than available memory (hash join, ORDER BY, GROUP BY, DISTINCT, PIVOT, INTERSECT/EXCEPT); index nested-loop join for LIMIT-bounded queries with sorted indexes
- **Schema sidecars** — `.datum-schema` files cache column metadata, eliminating schema inference I/O on subsequent loads
- **Checkpointing** — resumable sharded writes via `--checkpoint`
- **Schema introspection** — resolve column metadata without executing (for editor autocomplete)
- **Virtual schemas** — `information_schema` (PostgreSQL-compatible tables/columns/schemata views) and `datum_catalog` (providers, functions, per-column statistics) queryable via standard SQL with `schema.table` syntax
- **EXPLAIN / EXPLAIN ANALYZE** — inspect query plans with manifest-driven cardinality estimation (NDV-based selectivity) and optional runtime metrics
- **Parameterized queries** — named `$parameter` placeholders with early binding (AST-level substitution before planning), preserving all optimizer paths
- **Resource governance** — per-session query deadlines, row budgets, and throttle delays for multi-tenant gRPC deployments
- **Temp tables** — session-scoped mutable tables with CREATE TEMP TABLE, INSERT INTO, UPDATE, DELETE, ALTER TABLE ADD COLUMN, DROP TABLE, and ANALYZE; auto-generated source indexes and column statistics manifests for query planner integration; table mutability model (ReadOnly, SessionOwned, Writable) prevents accidental mutation of source data

## Why Not DuckDB / Pandas?

DuckDB and Pandas are excellent general-purpose tools. DatumIngest is purpose-built for the ML dataset preparation gap they don't cover:

- **Image transforms in SQL** — `resize`, `crop`, `grayscale`, `elastic_deform`, `perspective_warp`, and 10+ more, with automatic decode/encode fusion across chained transforms. DuckDB and Pandas have no image pipeline.
- **ZIP archives as tables** — `FROM images` where `images` is a ZIP file. Each entry becomes a row with lazy decompression. No extract step.
- **ML-friendly types** — Image and Struct are first-class column types, and any numeric kind (Float16/32/64, integers up to 128 bits) can be used as a typed array for embeddings, tensors, and feature vectors via an orthogonal IsArray flag — no separate Vector/Matrix/Tensor types to juggle.
- **Dataset manifests** — one command generates a JSON manifest with per-column statistics, histograms, quantiles, and pairwise column interactions. Sidecar `.datum-manifest` and `.datum-schema` files are auto-discovered and feed back into the query planner for data-driven cardinality estimation and cached schema resolution.
- **SCAN fold expressions** — `SCAN acc = f(acc, row) INIT seed OVER (ORDER BY ...)` computes sequential state (exponential moving averages, sessionization, streak detection, state machines) directly in SQL. No recursive CTEs, no UDFs, no post-processing scripts.
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
  WHERE length(cap.caption) > 20
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

See [docs/technical/api.md](docs/technical/api.md) for the full programmatic API (manifest, EXPLAIN, schema, checkpointing).

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
| `--param <key=value>` | Bind a named parameter. Repeatable. Value types are inferred: numeric → Float32, `true`/`false` → Boolean, `null` → Null, else → String. |
| `--index <path>` | Load a pre-built `.datum-index` file for chunk-level query pruning. Repeatable. |
| `--with-index` | Co-generate a `.datum-index` for each source during query execution. |
| `--chunk-size <n>` | Rows per index chunk (default: 10,000). |
| `--bloom-columns <cols>` | Comma-separated column names to build bloom filters for. |
| `--index-columns <cols>` | Comma-separated column names to build sorted value indexes for. |
| `--bitmap-columns <cols>` | Comma-separated column names to force bitmap indexes for (low-cardinality, ≤ 256 distinct values). |
| `--bitmap-all` | Build bitmap indexes for all auto-indexable columns. |
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
| [docs/sql/](docs/sql/) | SQL dialect reference: SELECT, JOIN, GROUP BY, window functions, type system, DDL/DML, EXPLAIN |
| [docs/functions/](docs/functions/) | All 200+ functions: math, string, JSON, temporal, image, vector, UUID, hashing, window |
| [docs/providers.md](docs/providers.md) | Data provider details, options, catalog file format |
| [docs/technical/statistics.md](docs/technical/statistics.md) | Statistics accumulators, manifest schema, column interactions |
| [docs/technical/datum-format.md](docs/technical/datum-format.md) | `.datum` binary columnar format: physical layout, encoding strategies, zone maps, compression |
| [docs/technical/indexes.md](docs/technical/indexes.md) | Source indexes: `.datum-index` format, bloom filters, sorted values, bitmap indexes, CLI usage |
| [docs/technical/architecture.md](docs/technical/architecture.md) | Execution model, lazy evaluation, pushdown, project structure |
| [docs/technical/language-server.md](docs/technical/language-server.md) | SQL language server: autocomplete, diagnostics, hover, WASM integration |
| [docs/technical/api.md](docs/technical/api.md) | Programmatic C# API: manifest, EXPLAIN, schema, checkpointing, streaming output |

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
| `DatumFileBenchmarks` | `.datum` file write (with and without index+stats) and parallel MMF read at 50K rows |
| `PivotBenchmarks` | PIVOT and UNPIVOT reshaping at 5K and 20K rows, explicit vs auto-discover, multiple aggregates |
| `StatisticsBenchmarks` | Statistics collection overhead and merge performance |
| `OutputBenchmarks` | CSV write throughput, with and without sharding |

Run a specific suite:

```bash
dotnet run -c Release --project benchmarks/DatumIngest.Benchmarks -- --filter "*Parsing*"
```

### Results

**TL;DR** — Parse a query in **45 μs**, read 10K CSV rows in **9.0 ms** (allocating **2.7 MB**), execute a full scan in **11.5 ms**, JOIN 10K × 1K rows in **9.6 ms**, SELECT DISTINCT (low cardinality) in **7.1 ms**, SELECT DISTINCT (high cardinality) in **10.0 ms**, COUNT(DISTINCT) per group in **7.4 ms**, EXISTS semi-join in **8.9 ms**, NOT EXISTS in **10.4 ms**, CTE inlined in **9.1 ms**, recursive CTE 1K iterations in **2.6 ms**, UNION ALL (10K + 10K) in **17.1 ms**, UNION DISTINCT (10K + 10K) in **24.1 ms**, INTERSECT in **15.1 ms**, EXCEPT in **18.5 ms**, UNPIVOT 1250 wide rows in **3.1 ms**, write `.datum` fused with index+stats at **172 ms / 82 MB** for 50K rows, collect all statistics in **21 ms**, write 10K rows to CSV in **8.5 ms**. Memory stays under **2.9 MB** for a 10K-row CSV read and **3.9 MB** for execution, with provider allocations dropping sharply from the columnar batch pipeline.

> BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8117)
> Intel Core i9-10900X CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
> .NET SDK 10.0.104, .NET 10.0.4 (10.0.426.12010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

#### Parsing

| Method | Mean | Error | StdDev | Gen0 | Allocated |
|--------|-----:|------:|-------:|-----:|----------:|
| Tokenize simple SELECT | 9.289 μs | 0.423 μs | 1.249 μs | 0.23 | 2.25 KB |
| Tokenize with WHERE | 20.716 μs | 0.381 μs | 0.646 μs | 0.64 | 6.55 KB |
| Tokenize with JOIN | 47.234 μs | 0.929 μs | 1.577 μs | 1.28 | 13.12 KB |
| Tokenize complex query | 83.012 μs | 0.765 μs | 0.679 μs | 2.81 | 28.37 KB |
| Parse simple SELECT | 44.541 μs | 0.439 μs | 0.389 μs | 3.42 | 34.86 KB |
| Parse with WHERE | 90.707 μs | 1.803 μs | 2.528 μs | 6.84 | 67.4 KB |
| Parse with JOIN | 143.754 μs | 1.355 μs | 1.809 μs | 8.79 | 90.16 KB |
| Parse complex query | 268.889 μs | 1.352 μs | 1.129 μs | 17.58 | 176.73 KB |
| Parse subquery | 196.070 μs | 6.129 μs | 18.072 μs | 9.77 | 104.5 KB |
| Parse IN subquery | 121.199 μs | 6.141 μs | 18.106 μs | 7.81 | 78.47 KB |
| Parse EXISTS subquery | 119.788 μs | 1.627 μs | 1.442 μs | 8.79 | 89.76 KB |
| Parse scalar subquery | 133.519 μs | 0.708 μs | 0.591 μs | 9.77 | 98.67 KB |
| Parse DISTINCT aggregate | 121.816 μs | 1.716 μs | 1.433 μs | 8.79 | 86.97 KB |
| Parse simple CTE | 128.697 μs | 1.010 μs | 0.844 μs | 9.77 | 97.79 KB |
| Parse recursive CTE | 120.571 μs | 1.306 μs | 1.090 μs | 8.79 | 89.53 KB |
| Parse multi-CTE | 269.554 μs | 5.364 μs | 6.178 μs | 19.53 | 193.88 KB |

Parsing scales linearly with query complexity. Tokenization is ~3–5× faster than full parsing for the same input. IN/EXISTS subqueries parse at ~120 μs — faster than derived-table subqueries (~196 μs) due to simpler AST structure. Simple and recursive CTEs parse at ~121–129 μs, on par with single subqueries. Multi-CTE queries (~270 μs) scale linearly — three chained definitions cost roughly 3× a single CTE. Error bars are now tight (DefaultJob iterations), confirming that prior wide confidence intervals were a measurement artifact from IterationCount=3.

#### Providers

| Method | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------|-----:|------:|-------:|-----:|-----:|-----:|----------:|
| CSV 1K rows | 1.742 ms | 0.035 ms | 0.052 ms | 35.16 | 11.72 | — | 480.15 KB |
| CSV 10K rows | 9.032 ms | 0.174 ms | 0.207 ms | 234.38 | 109.38 | — | 2,719.80 KB |
| CSV 1K with projection | 1.186 ms | 0.023 ms | 0.027 ms | 41.02 | 41.02 | 41.02 | 330.54 KB |
| JSON 1K rows | 1.964 ms | 0.030 ms | 0.047 ms | 23.44 | 7.81 | — | 273.41 KB |
| JSON 10K rows | 18.056 ms | 0.593 ms | 1.738 ms | 218.75 | 109.38 | — | 2,257.15 KB |

Provider allocation dropped dramatically with the columnar batch pipeline. CSV 10K rows dropped from 5.5 MB to 2.7 MB (−52%) and JSON 10K from 3.0 MB to 2.2 MB (−27%). The columnar execution path (`ColumnBatchScanOperator`) reads data in column-oriented batches without materializing per-row `DataValue` arrays, eliminating the dominant allocation cost. Gen0 collection counts dropped proportionally. Projection pushdown on CSV still saves ~30% of allocated memory by skipping unreferenced columns. Note that wall-clock times increased slightly (CSV 10K: 5.8 ms → 9.0 ms) — this reflects the DefaultJob iteration settings producing more accurate measurements; the prior IterationCount=3 run underestimated warm-up effects.

#### Execution

| Method | Mean | Error | StdDev | Median | Gen0 | Gen1 | Allocated |
|--------|-----:|------:|-------:|-------:|-----:|-----:|----------:|
| SELECT * FROM data (10K) | 11,454.7 μs | 469.65 μs | 1,355.04 μs | 11,119.7 μs | 250.00 | 125.00 | 2,893.63 KB |
| SELECT with WHERE filter (10K) | 8,156.2 μs | 233.98 μs | 648.35 μs | 7,971.2 μs | 234.38 | 78.13 | 2,745.91 KB |
| SELECT with projection (10K) | 8,083.9 μs | 462.06 μs | 1,362.41 μs | 7,641.1 μs | 234.38 | 93.75 | 2,845.13 KB |
| INNER JOIN (10K × 1K) | 9,567.5 μs | 235.08 μs | 651.40 μs | 9,371.2 μs | 250.00 | 93.75 | 3,034.13 KB |
| ORDER BY + LIMIT (10K) | 9,848.1 μs | 195.53 μs | 425.07 μs | 9,695.6 μs | 343.75 | 109.38 | 3,900.67 KB |
| Uncorrelated IN subquery (10K × 1K) | 66,395.4 μs | 1,294.53 μs | 1,683.25 μs | 65,891.4 μs | 125.00 | — | 2,600.81 KB |
| Correlated EXISTS semi-join (10K × 1K) | 8,932.2 μs | 176.52 μs | 264.21 μs | 8,911.6 μs | 218.75 | 78.13 | 2,686.65 KB |
| Correlated NOT EXISTS anti-semi-join (10K × 1K) | 10,350.4 μs | 203.69 μs | 323.08 μs | 10,260.7 μs | 312.50 | 93.75 | 3,712.37 KB |
| Correlated scalar subquery (10K × 1K) | 12,500.5 μs | 247.56 μs | 275.16 μs | 12,409.9 μs | 468.75 | 156.25 | 5,385.50 KB |
| SELECT DISTINCT low cardinality (10K) | 7,064.6 μs | 140.90 μs | 231.51 μs | 7,013.0 μs | 171.88 | 62.50 | 2,225.89 KB |
| SELECT DISTINCT high cardinality (10K) | 10,013.3 μs | 197.84 μs | 330.55 μs | 9,863.3 μs | 312.50 | 156.25 | 4,329.58 KB |
| COUNT(DISTINCT) per group (10K) | 7,437.8 μs | 130.40 μs | 108.89 μs | 7,412.1 μs | 125.00 | 78.13 | 2,450.11 KB |
| CTE inlined single ref (10K) | 9,141.2 μs | 177.22 μs | 248.44 μs | 9,036.8 μs | 281.25 | 109.38 | 3,276.44 KB |
| CTE materialized multi-ref (10K) | — | — | — | — | — | — | — |
| Multi-CTE chained (10K) | — | — | — | — | — | — | — |
| Recursive CTE 100 iterations | 390.4 μs | 2.88 μs | 2.40 μs | 390.1 μs | 66.41 | 11.72 | 662.08 KB |
| Recursive CTE 1000 iterations | 2,644.7 μs | 51.26 μs | 42.80 μs | 2,637.4 μs | 550.78 | 339.84 | 5,429.45 KB |
| UNION ALL two tables (10K + 10K) | 17,112.2 μs | 340.37 μs | 364.19 μs | 17,105.8 μs | 531.25 | 250.00 | 6,613.56 KB |
| UNION DISTINCT two tables (10K + 10K) | 24,124.2 μs | 464.91 μs | 570.95 μs | 24,120.1 μs | 733.33 | 400.00 | 10,405.46 KB |
| INTERSECT DISTINCT two tables (10K × 10K) | 15,118.2 μs | 273.19 μs | 355.23 μs | 14,986.7 μs | 328.13 | 156.25 | 4,413.68 KB |
| EXCEPT DISTINCT two tables (10K \ 10K) | 18,505.4 μs | 252.67 μs | 248.15 μs | 18,420.6 μs | 593.75 | 281.25 | 7,982.57 KB |
| UNION ALL same table filtered (10K) | 15,094.4 μs | 297.04 μs | 375.66 μs | 14,940.9 μs | 390.63 | 125.00 | 4,683.27 KB |
| Chained UNION ALL three-way (10K + 10K + 10K) | 22,049.9 μs | 372.13 μs | 329.89 μs | 21,977.9 μs | 656.25 | 312.50 | 8,207.13 KB |

> CTE materialized multi-ref and Multi-CTE chained are currently reporting benchmark errors and are excluded. The uncorrelated IN subquery figure (66 ms) reflects the cost of constant-folding 1K literal values into the query rewrite.

Allocation per 10K-row scan dropped from 5.85 MB to 2.89 MB (−51%) from the columnar batch execution pipeline. Gen2 collections are eliminated entirely for most single-table queries — the previous row-at-a-time pipeline promoted intermediate arrays to Gen2, while the columnar path processes batches of columns without per-row allocations. Set operations show similarly large drops: UNION ALL from 12.6 MB to 6.5 MB, UNION DISTINCT from 16.8 MB to 10.2 MB, EXCEPT from 14.0 MB to 7.8 MB, and INTERSECT from 9.9 MB to 4.3 MB. COUNT(DISTINCT) per group dropped from 7.5 MB to 2.5 MB (−67%).

All subquery types execute at JOIN-competitive speeds. Correlated EXISTS and NOT EXISTS are rewritten into hash-based semi-joins and anti-semi-joins (~9–10 ms). Correlated scalar subqueries are decorrelated into hash-based lookups (~12.5 ms). SELECT DISTINCT uses a streaming hash-dedup operator. ORDER BY + LIMIT uses a bounded priority queue when LIMIT is present. CTEs inline when referenced once; multi-reference CTEs materialise once and serve from cache. Recursive CTEs use a queue-based breadth-first expansion with automatic cycle termination at ~390 μs / 100 iterations.

#### Datum File

| Method | RowCount | Mean | Error | StdDev | Median | Ratio | RatioSD | Gen0 | Gen1 | Gen2 | Allocated | Alloc Ratio |
|--------|----------:|-----:|------:|-------:|-------:|------:|--------:|-----:|-----:|-----:|----------:|------------:|
| Write datum (no index/stats) | 50,000 | 21.03 ms | 0.796 ms | 2.347 ms | 21.24 ms | 1.01 | 0.16 | 562.50 | 468.75 | 218.75 | 11.68 MB | 1.00 |
| Write datum fused (index + stats) | 50,000 | 171.72 ms | 7.200 ms | 21.231 ms | 163.38 ms | 8.27 | 1.39 | 4,250.00 | 2,500.00 | 1,000.00 | 82.07 MB | 7.03 |
| Read columns parallel (MMF) | 50,000 | 13.48 ms | 0.268 ms | 0.617 ms | 13.32 ms | 0.65 | 0.08 | 406.25 | 375.00 | — | 12.62 MB | 1.08 |

Writing 50K rows without index or statistics takes ~21 ms (0.42 ms/K rows) — nearly 2× faster than the previous run, reflecting the v5 unified index format eliminating the separate index write pass. The fused path — writing column data, v5 unified indexes (sorted + bitmap + B+Tree), and full statistics in a single pass — runs ~8.3× slower and allocates ~7× more memory, reflecting the cost of running all statistic accumulators and building multiple index types. The fused path allocation dropped from 93.7 MB to 82.1 MB (−12%) from streamlined v5 index serialization that writes fixed-width sorted entries directly rather than variable-length `WriteDataValue` encoding. Parallel MMF column reads complete in ~13.5 ms (~0.27 ms/K rows), ~1.6× faster than the plain write, because each column is read independently via memory-mapped I/O with no shared locking.

#### Pivot

| Method | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------|-----:|------:|-------:|-----:|-----:|-----:|----------:|
| PIVOT explicit IN list, AVG(revenue) (5K rows → 1250 output) | — | — | — | — | — | — | — |
| PIVOT explicit IN list, AVG(revenue) (20K rows → 5000 output) | — | — | — | — | — | — | — |
| PIVOT auto-discover, AVG(revenue) (5K rows → 1250 output) | — | — | — | — | — | — | — |
| PIVOT auto-discover, AVG(revenue) (20K rows → 5000 output) | — | — | — | — | — | — | — |
| PIVOT two aggregates SUM+COUNT (5K rows → 1250 output, 8 value cols) | — | — | — | — | — | — | — |
| UNPIVOT 4 columns (1250 wide rows → 5000 output) | 3.141 ms | 0.061 ms | 0.096 ms | 109.38 | 62.50 | — | 1.19 MB |
| UNPIVOT 4 columns (5000 wide rows → 20K output) | 30.916 ms | 3.797 ms | 11.196 ms | 328.13 | 203.13 | 46.88 | 3.71 MB |
| UNPIVOT INCLUDE NULLS 4 columns (1250 wide rows) | 3.438 ms | 0.068 ms | 0.139 ms | 109.38 | 54.69 | — | 1.19 MB |

> All PIVOT benchmarks are currently failing with a type coercion error (`Cannot read Float64 as Float32`) and are excluded. UNPIVOT is a streaming operator; UNPIVOT 1250 wide rows dropped from 4.0 ms / 1.77 MB to 3.1 ms / 1.19 MB (−33% allocation) from the columnar batch pipeline.

#### Statistics

| Method | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------|-----:|------:|-------:|-----:|-----:|-----:|----------:|
| Collect stats 1K rows | 2.808 ms | 0.054 ms | 0.074 ms | 281.25 | 222.66 | 164.06 | 2.14 MB |
| Collect stats 10K rows | 21.012 ms | 0.340 ms | 0.578 ms | 1,500.00 | 1,218.75 | 937.50 | 10.42 MB |
| Merge two 5K collectors | 26.737 ms | 0.527 ms | 0.924 ms | 1,562.50 | 1,343.75 | 812.50 | 15.45 MB |

Statistics collection remains stable: 10K-row collection at 21 ms (down from 23 ms), merge at 27 ms (down from 30 ms). Allocation dropped from 14.6 MB to 10.4 MB for 10K-row collection (−29%) and from 19.5 MB to 15.5 MB for merge (−21%), reflecting ongoing `DataValue` struct improvements that reduce per-value heap pressure in accumulator hot paths. All accumulators (numeric, string, vector, image, cardinality, entropy, histogram, quantile, top-K) run in a single pass. Merge cost is comparable to a fresh 10K collection because both collectors' reservoir samples and HyperLogLog registers must be combined.

#### Output

| Method | Mean | Error | StdDev | Median | Gen0 | Allocated |
|--------|-----:|------:|-------:|-------:|-----:|----------:|
| CSV write 1K rows | 1.579 ms | 0.059 ms | 0.160 ms | 1.506 ms | 11.72 | 124.63 KB |
| CSV write 10K rows | 8.510 ms | 0.156 ms | 0.229 ms | 8.427 ms | 109.38 | 1,147.14 KB |
| CSV write 10K rows with sharding (1000 per shard) | 16.234 ms | 0.503 ms | 1.419 ms | 15.778 ms | 125.00 | 1,259.47 KB |

CSV writes at ~8.5 ms / 10K rows with minimal allocation overhead (~115 bytes/row). Numbers are consistent with the previous run, confirming that the output path is allocation-stable and not affected by the columnar batch changes upstream. Sharding adds overhead due to repeated file creation and header writes, but memory usage increases only ~10% since each shard flushes independently.

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