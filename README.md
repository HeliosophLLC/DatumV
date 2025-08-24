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

- **ML-native type system** — Vector, Matrix, Tensor, and Image are first-class column types alongside Float32, String, Date, DateTime, Time, Duration, JsonValue, UUID, and Boolean
- **Fused image pipeline** — `resize`, `crop`, `grayscale`, `elastic_deform`, `perspective_warp`, and 10+ more transforms, with automatic decode/encode fusion across chains
- **200+ built-in functions** — `softmax`, `cosine_similarity`, `normalize`, `cyclical_encode`, vector reductions, distance metrics, ML activations, UUID generation, hashing, and more
- **Six data providers** — CSV, JSON, JSONL, ZIP (lazy decompression), HDF5, Parquet
- **Three output formats** — CSV, Parquet, HDF5 with `SHARD ON` support
- **SQL interface** — SELECT, SELECT DISTINCT, FROM, JOIN, WHERE, INTO, ORDER BY, LIMIT, CASE/WHEN, GROUP BY, DISTINCT aggregates (`COUNT(DISTINCT x)`), window functions (OVER/PARTITION BY), subqueries (scalar, IN, NOT IN, EXISTS, NOT EXISTS), Common Table Expressions (WITH / WITH RECURSIVE), set operations (UNION, INTERSECT, EXCEPT with ALL variants), quoted identifiers, parameterized queries (`$name`)
- **Dataset statistics** — HyperLogLog cardinality, Welford's online stats, histograms, quantiles, image metadata
- **JSON manifest** — structured feature manifest with column interactions (Pearson, Spearman, Cramér's V, ANOVA F, MI), auto-discovered as `.datum-manifest` sidecars for cost-model integration
- **Streaming execution** — `IAsyncEnumerable<Row>` pipeline with projection pushdown, predicate pushdown, and Parquet row group pruning via min/max statistics
- **Source indexes** — `.datum-index` sidecar files with chunk-level statistics pruning, bloom filter join acceleration, sorted value indexes for equality/range predicates, B+Tree indexes for large columns (≥5M entries, demand-paged 8 KiB pages with Zstd-compressed leaves), bitmap indexes for low-cardinality columns (≤256 distinct values, bitwise AND/OR/NOT composition), ORDER BY elimination via index scan, automatic column selection for compact types, per-column Zstd compression (5–10× size reduction), and configurable column caps
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
| [docs/sql.md](docs/sql.md) | SQL dialect reference, type system, EXPLAIN, schema introspection |
| [docs/functions.md](docs/functions.md) | All 200+ functions: math, string, JSON, temporal, image, vector, UUID, hashing, window |
| [docs/providers.md](docs/providers.md) | Data provider details, options, catalog file format |
| [docs/statistics.md](docs/statistics.md) | Statistics accumulators, manifest schema, column interactions |
| [docs/datum-format.md](docs/datum-format.md) | `.datum` binary columnar format: physical layout, encoding strategies, zone maps, compression |
| [docs/indexes.md](docs/indexes.md) | Source indexes: `.datum-index` format, bloom filters, sorted values, bitmap indexes, CLI usage |
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
| `DatumFileBenchmarks` | `.datum` file write (with and without index+stats) and parallel MMF read at 50K rows |
| `PivotBenchmarks` | PIVOT and UNPIVOT reshaping at 5K and 20K rows, explicit vs auto-discover, multiple aggregates |
| `StatisticsBenchmarks` | Statistics collection overhead and merge performance |
| `OutputBenchmarks` | CSV write throughput, with and without sharding |

Run a specific suite:

```bash
dotnet run -c Release --project benchmarks/DatumIngest.Benchmarks -- --filter "*Parsing*"
```

### Results

**TL;DR** — Parse a query in **45 μs**, read 10K CSV rows in **5.8 ms** (allocating **5.5 MB** — down from 8.5 MB), execute a full scan in **7.3 ms**, JOIN 10K × 1K rows in **10.4 ms**, SELECT DISTINCT (low cardinality) in **6.4 ms**, SELECT DISTINCT (high cardinality) in **11.8 ms**, COUNT(DISTINCT) per group in **10.5 ms**, EXISTS semi-join in **8.9 ms**, NOT EXISTS in **10.9 ms**, CTE inlined in **9.9 ms**, recursive CTE 1K iterations in **2.6 ms**, UNION ALL (10K + 10K) in **14.5 ms**, UNION DISTINCT (10K + 10K) in **37.5 ms**, INTERSECT in **11.7 ms**, EXCEPT in **18.7 ms**, UNPIVOT 1250 wide rows in **4.0 ms**, collect all statistics in **23 ms** (down from 53 ms), write 10K rows to CSV in **10 ms**. Memory stays under **5.9 MB** for a 10K-row single-table pipeline (down from 8.6 MB following `DataValue` struct migration).

> BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8117)
> Intel Core i9-10900X CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
> .NET SDK 10.0.103, .NET 10.0.3 (10.0.326.7603), X64 RyuJIT AVX-512F+CD+BW+DQ+VL
> IterationCount=3, WarmupCount=1 (error bars are wider than default; re-run with default settings for publication-quality numbers)

#### Parsing

| Method | Mean | Error | StdDev | Gen0 | Allocated |
|--------|-----:|------:|-------:|-----:|----------:|
| Tokenize simple SELECT | 7.630 μs | 1.166 μs | 0.064 μs | 0.23 | 2.25 KB |
| Tokenize with WHERE | 20.431 μs | 12.512 μs | 0.686 μs | 0.64 | 6.55 KB |
| Tokenize with JOIN | 46.284 μs | 22.084 μs | 1.211 μs | 1.28 | 13.12 KB |
| Tokenize complex query | 77.436 μs | 25.851 μs | 1.417 μs | 2.81 | 28.37 KB |
| Parse simple SELECT | 44.743 μs | 71.309 μs | 3.909 μs | 3.42 | 34.86 KB |
| Parse with WHERE | 97.203 μs | 269.772 μs | 14.787 μs | 6.84 | 67.4 KB |
| Parse with JOIN | 153.104 μs | 253.697 μs | 13.906 μs | 8.79 | 90.16 KB |
| Parse complex query | 283.730 μs | 374.031 μs | 20.502 μs | 17.58 | 176.73 KB |
| Parse subquery | 170.989 μs | 526.213 μs | 28.844 μs | 9.77 | 104.5 KB |
| Parse IN subquery | 99.823 μs | 52.884 μs | 2.899 μs | 7.81 | 78.47 KB |
| Parse EXISTS subquery | 155.559 μs | 898.988 μs | 49.277 μs | 8.79 | 89.76 KB |
| Parse scalar subquery | 167.203 μs | 811.796 μs | 44.497 μs | 9.77 | 98.67 KB |
| Parse DISTINCT aggregate | 161.802 μs | 828.396 μs | 45.407 μs | 8.79 | 86.97 KB |
| Parse simple CTE | 148.951 μs | 430.623 μs | 23.604 μs | 9.77 | 97.79 KB |
| Parse recursive CTE | 143.219 μs | 324.771 μs | 17.802 μs | 8.79 | 89.53 KB |
| Parse multi-CTE | 359.301 μs | 2,377.993 μs | 130.346 μs | 19.53 | 193.88 KB |

Parsing scales linearly with query complexity. Tokenization is ~3–5× faster than full parsing for the same input. IN/EXISTS subqueries parse faster than derived-table subqueries due to simpler AST structure. DISTINCT aggregate queries parse in ~162 μs — comparable to EXISTS subqueries, since the DISTINCT modifier adds minimal AST overhead per aggregate. Simple and recursive CTEs parse in ~143–149 μs, on par with single subqueries, since the WITH clause adds one definition header plus a standard SELECT body. Multi-CTE queries (~359 μs) scale linearly — three chained definitions cost roughly 3× a single CTE, consistent with the additive nature of definition parsing. The addition of compound-query grammar paths (UNION/INTERSECT/EXCEPT) adds ~15% overhead to parse times even for non-compound queries, since the parser now wraps every statement in a `QueryExpression` and attempts compound continuation at the top level.

#### Providers

| Method | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------|-----:|------:|-------:|-----:|-----:|-----:|----------:|
| CSV 1K rows | 1.439 ms | 0.384 ms | 0.021 ms | 82.03 | 41.02 | 41.02 | 806.6 KB |
| CSV 10K rows | 5.760 ms | 2.296 ms | 0.126 ms | 562.50 | 156.25 | 39.06 | 5,670.67 KB |
| CSV 1K with projection | 1.217 ms | 1.052 ms | 0.058 ms | 82.03 | 41.02 | 41.02 | 633.54 KB |
| JSON 1K rows | 1.773 ms | 1.278 ms | 0.070 ms | 35.16 | — | — | 351.65 KB |
| JSON 10K rows | 12.252 ms | 5.252 ms | 0.288 ms | 296.88 | — | — | 3,038.08 KB |

Provider allocation is significantly reduced following the `DataValue` struct migration. CSV 10K rows dropped from 8.5 MB to 5.5 MB (−35%) and JSON 10K from 4.4 MB to 3.0 MB (−33%) — each row no longer allocates a heap object per field for numeric and boolean values. All providers share a pre-built column name index across rows, eliminating per-row Dictionary allocations at the source. The CSV parser reuses a thread-local field buffer across rows, eliminating a `List<string>` allocation per line. Projection pushdown on CSV saves ~10% of allocated memory by skipping unreferenced columns.

#### Execution

| Method | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------|-----:|------:|-------:|-----:|-----:|-----:|----------:|
| SELECT * FROM data (10K) | 7.285 ms | 7.939 ms | 0.435 ms | 578.13 | 203.13 | 39.06 | 5,854.5 KB |
| SELECT with WHERE filter (10K) | 7.797 ms | 6.011 ms | 0.329 ms | 578.13 | 203.13 | 39.06 | 5,738.51 KB |
| SELECT with projection (10K) | 6.028 ms | 7.345 ms | 0.403 ms | 578.13 | 195.31 | 39.06 | 5,861.9 KB |
| INNER JOIN (10K × 1K) | 10.426 ms | 19.132 ms | 1.049 ms | 718.75 | 218.75 | 109.38 | 7,454.64 KB |
| ORDER BY + LIMIT (10K) | 9.641 ms | 15.519 ms | 0.851 ms | 703.13 | 234.38 | 31.25 | 7,060.95 KB |
| Uncorrelated IN subquery (10K × 1K) | 70.735 ms | 206.991 ms | 11.346 ms | 444.44 | 111.11 | — | 5,633.39 KB |
| Correlated EXISTS semi-join (10K × 1K) | 8.871 ms | 13.208 ms | 0.724 ms | 578.13 | 406.25 | 78.13 | 5,681.56 KB |
| Correlated NOT EXISTS anti-semi-join (10K × 1K) | 10.926 ms | 17.781 ms | 0.975 ms | 656.25 | 218.75 | 109.38 | 6,898.43 KB |
| Correlated scalar subquery (10K × 1K) | 14.808 ms | 16.134 ms | 0.884 ms | 968.75 | 281.25 | 125.00 | 9,864.64 KB |
| SELECT DISTINCT low cardinality (10K) | 6.429 ms | 4.660 ms | 0.255 ms | 500.00 | 156.25 | 31.25 | 5,086.37 KB |
| SELECT DISTINCT high cardinality (10K) | 11.785 ms | 1.257 ms | 0.069 ms | 734.38 | 359.38 | 234.38 | 7,503.5 KB |
| COUNT(DISTINCT) per group (10K) | 10.508 ms | 11.528 ms | 0.632 ms | 859.38 | 406.25 | 281.25 | 7,488.24 KB |
| CTE inlined single ref (10K) | 9.931 ms | 4.982 ms | 0.273 ms | 640.63 | 203.13 | 31.25 | 6,418.22 KB |
| CTE materialized multi-ref (10K) | — | — | — | — | — | — | — |
| Multi-CTE chained (10K) | — | — | — | — | — | — | — |
| Recursive CTE 100 iterations | 0.513 ms | 1.184 ms | 0.065 ms | 62.50 | 9.77 | — | 619.73 KB |
| Recursive CTE 1000 iterations | 2.639 ms | 9.458 ms | 0.518 ms | 507.81 | 324.22 | — | 5,027.52 KB |
| UNION ALL two tables (10K + 10K) | 14.508 ms | 3.412 ms | 0.187 ms | 1296.88 | 359.38 | 78.13 | 12,958.51 KB |
| UNION DISTINCT two tables (10K + 10K) | 37.468 ms | 63.515 ms | 3.481 ms | 1928.57 | 1000.00 | 500.00 | 17,217.09 KB |
| INTERSECT DISTINCT two tables (10K × 10K) | 11.742 ms | 3.506 ms | 0.192 ms | 1000.00 | 328.13 | 78.13 | 10,136.4 KB |
| EXCEPT DISTINCT two tables (10K \ 10K) | 18.711 ms | 14.811 ms | 0.812 ms | 1375.00 | 656.25 | 406.25 | 14,326.82 KB |
| UNION ALL same table filtered (10K) | 13.597 ms | 3.396 ms | 0.186 ms | 1046.88 | 359.38 | 78.13 | 10,476.41 KB |
| Chained UNION ALL three-way (10K + 10K + 10K) | 18.093 ms | 4.039 ms | 0.221 ms | 1718.75 | 468.75 | 93.75 | 17,508.43 KB |

> CTE materialized multi-ref and Multi-CTE chained are currently reporting benchmark errors and are excluded. The uncorrelated IN subquery figure (70 ms) carries an extremely wide error band (±207 ms) from the reduced iteration count and should not be compared directly to other runs.

Allocation per 10K-row scan dropped from 8.56 MB to 5.85 MB (−32%) directly from the `DataValue` struct migration — numeric and boolean fields are now value-type inline in `DataValue[]` arrays rather than individual heap objects. Set operations show the clearest improvement: UNION DISTINCT dropped from 19.3 MB to 16.8 MB, EXCEPT from 16.7 MB to 14.0 MB, and UNION ALL from 16.5 MB to 12.6 MB.

All subquery types execute at JOIN-competitive speeds. Correlated EXISTS and NOT EXISTS are rewritten into hash-based semi-joins and anti-semi-joins (~9–11 ms). Correlated scalar subqueries are decorrelated into hash-based lookups (~15 ms). SELECT DISTINCT uses a streaming hash-dedup operator; high-cardinality DISTINCT improved from ~17 ms to ~12 ms. ORDER BY + LIMIT uses a bounded priority queue when LIMIT is present. CTEs inline when referenced once; multi-reference CTEs materialise once and serve from cache. Recursive CTEs use a queue-based breadth-first expansion with automatic cycle termination at ~0.5 ms / 100 iterations.

#### Datum File

| Method | RowCount | Mean | Error | StdDev | Ratio | Gen0 | Gen1 | Gen2 | Allocated | Alloc Ratio |
|--------|----------:|-----:|------:|-------:|------:|-----:|-----:|-----:|----------:|------------:|
| Write datum (no index/stats) | 50,000 | 36.44 ms | 40.968 ms | 2.246 ms | 1.00 | 812.50 | 750.00 | 500.00 | 19.6 MB | 1.00 |
| Write datum fused (index + stats) | 50,000 | 169.99 ms | 116.474 ms | 6.384 ms | 4.68 | 5000.00 | 2250.00 | 1000.00 | 93.69 MB | 4.78 |
| Read columns parallel (MMF) | 50,000 | 16.09 ms | 1.155 ms | 0.063 ms | 0.44 | 718.75 | 687.50 | 312.50 | 14.54 MB | 0.74 |

Writing 50K rows without index or statistics takes ~36 ms (0.73 ms/K rows). The fused path — writing the column data, zone-map index, and full statistics in a single pass — runs ~4.7× slower and allocates ~4.8× more memory, reflecting the cost of running all statistic accumulators (histogram, quantile, HyperLogLog, reservoir sampler, top-K) over every value. Parallel MMF column reads complete in ~16 ms (~0.32 ms/K rows), ~2× faster than the plain write, because each column is read independently via memory-mapped I/O with no shared locking.

#### Pivot

| Method | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------|-----:|------:|-------:|-----:|-----:|-----:|----------:|
| PIVOT explicit IN list, AVG(revenue) (5K rows → 1250 output) | — | — | — | — | — | — | — |
| PIVOT explicit IN list, AVG(revenue) (20K rows → 5000 output) | — | — | — | — | — | — | — |
| PIVOT auto-discover, AVG(revenue) (5K rows → 1250 output) | — | — | — | — | — | — | — |
| PIVOT auto-discover, AVG(revenue) (20K rows → 5000 output) | — | — | — | — | — | — | — |
| PIVOT two aggregates SUM+COUNT (5K rows → 1250 output, 8 value cols) | — | — | — | — | — | — | — |
| UNPIVOT 4 columns (1250 wide rows → 5000 output) | 3.995 ms | 1.431 ms | 0.078 ms | 179.69 | 54.69 | 39.06 | 1.77 MB |
| UNPIVOT 4 columns (5000 wide rows → 20K output) | 6.975 ms | 14.490 ms | 0.794 ms | 578.13 | 195.31 | 39.06 | 5.72 MB |
| UNPIVOT INCLUDE NULLS 4 columns (1250 wide rows) | 4.574 ms | 29.574 ms | 1.621 ms | 179.69 | 54.69 | 39.06 | 1.77 MB |

> All PIVOT benchmarks are currently reporting errors and are excluded from this run. UNPIVOT is a streaming operator and runs ~4–5× faster than PIVOT at equivalent input sizes.

#### Statistics

| Method | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------|-----:|------:|-------:|-----:|-----:|-----:|----------:|
| Collect stats 1K rows | 2.098 ms | 9.118 ms | 0.500 ms | 177.73 | 76.17 | — | 1.72 MB |
| Collect stats 10K rows | 23.250 ms | 38.214 ms | 2.095 ms | 1812.50 | 1312.50 | 1000.00 | 14.62 MB |
| Merge two 5K collectors | 30.211 ms | 39.087 ms | 2.143 ms | 2062.50 | 1750.00 | 1000.00 | 19.46 MB |

Statistics collection improved substantially: 10K-row collection dropped from 53 ms to 23 ms (−56%) and merge from 62 ms to 30 ms (−52%), primarily from the `DataValue` struct migration eliminating per-value heap allocations in accumulator hot paths. All accumulators (numeric, string, vector, image, cardinality, entropy, histogram, quantile, top-K) run in a single pass. Merge cost is comparable to a fresh 10K collection because both collectors' reservoir samples and HyperLogLog registers must be combined.

#### Output

| Method | Mean | Error | StdDev | Gen0 | Allocated |
|--------|-----:|------:|-------:|-----:|----------:|
| CSV write 1K rows | 1.523 ms | 0.690 ms | 0.038 ms | 11.72 | 124.32 KB |
| CSV write 10K rows | 10.064 ms | 8.830 ms | 0.484 ms | 109.38 | 1,145.54 KB |
| CSV write 10K rows with sharding (1000 per shard) | 18.375 ms | 12.727 ms | 0.698 ms | 125.00 | 1,256.29 KB |

CSV writes with minimal allocation overhead (~115 bytes/row). Sharding adds overhead due to repeated file creation and header writes, but memory usage increases only ~10% since each shard flushes independently.

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