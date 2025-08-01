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
- **SQL interface** — SELECT, SELECT DISTINCT, FROM, JOIN, WHERE, INTO, ORDER BY, LIMIT, CASE/WHEN, GROUP BY, DISTINCT aggregates (`COUNT(DISTINCT x)`), window functions (OVER/PARTITION BY), subqueries (scalar, IN, NOT IN, EXISTS, NOT EXISTS), quoted identifiers, parameterized queries (`$name`)
- **Dataset statistics** — HyperLogLog cardinality, Welford's online stats, histograms, quantiles, image metadata
- **JSON manifest** — structured feature manifest with column interactions (Pearson, Spearman, Cramér's V, ANOVA F, MI), auto-discovered as `.datum-manifest` sidecars for cost-model integration
- **Streaming execution** — `IAsyncEnumerable<Row>` pipeline with projection pushdown, predicate pushdown, and Parquet row group pruning via min/max statistics
- **Source indexes** — `.datum-index` sidecar files with chunk-level statistics pruning, bloom filter join acceleration, sorted value indexes for equality/range predicates, and ORDER BY elimination via index scan
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
| `--source <def>` | Inline source definition. Format: `name=path` (auto-detect) or `provider:name=path[;key=value]` (explicit). Repeatable. |
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
| `ExecutionBenchmarks` | Full query execution: scan, filter, project, join, order+limit, subqueries, DISTINCT |
| `StatisticsBenchmarks` | Statistics collection overhead and merge performance |
| `OutputBenchmarks` | CSV write throughput, with and without sharding |

Run a specific suite:

```bash
dotnet run -c Release --project benchmarks/DatumIngest.Benchmarks -- --filter "*Parsing*"
```

### Results

**TL;DR** — Parse a query in **32 μs**, read 10K CSV rows in **7 ms**, execute a full scan in **8 ms**, JOIN 10K × 1K rows in **9 ms**, SELECT DISTINCT (low cardinality) in **6 ms**, SELECT DISTINCT (high cardinality) in **13 ms**, COUNT(DISTINCT) per group in **8 ms**, IN subquery in **8 ms**, EXISTS semi-join in **8 ms**, NOT EXISTS in **10 ms**, collect all statistics in **53 ms**, and write 10K rows to CSV in **8 ms**. Memory stays under **9 MB** for a 10K-row end-to-end pipeline.

> BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8039)
> Intel Core i9-10900X CPU 3.70GHz, 1 CPU, 20 logical and 10 physical cores
> .NET SDK 10.0.103, .NET 10.0.3 (10.0.326.7603), X64 RyuJIT AVX-512F+CD+BW+DQ+VL

#### Parsing

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| Tokenize simple SELECT | 6.903 μs | 0.0919 μs | 0.0814 μs | 1.94 KB |
| Tokenize with WHERE | 19.181 μs | 0.3513 μs | 0.3286 μs | 6.17 KB |
| Tokenize with JOIN | 41.414 μs | 0.3485 μs | 0.3089 μs | 11.3 KB |
| Tokenize complex query | 136.138 μs | 1.2064 μs | 0.9419 μs | 25.87 KB |
| Parse simple SELECT | 32.080 μs | 0.6109 μs | 0.5416 μs | 26.27 KB |
| Parse with WHERE | 74.613 μs | 0.5943 μs | 0.4963 μs | 56.41 KB |
| Parse with JOIN | 119.343 μs | 0.9524 μs | 0.7435 μs | 77.11 KB |
| Parse complex query | 230.842 μs | 4.0605 μs | 3.7982 μs | 155.5 KB |
| Parse subquery | 117.005 μs | 1.2940 μs | 1.2104 μs | 87.29 KB |
| Parse IN subquery | 80.928 μs | 1.4156 μs | 1.2549 μs | 64.2 KB |
| Parse EXISTS subquery | 100.639 μs | 1.9535 μs | 1.8273 μs | 74.36 KB |
| Parse scalar subquery | 103.304 μs | 1.2003 μs | 1.0640 μs | 78.62 KB |
| Parse DISTINCT aggregate | 95.132 μs | 1.8796 μs | 1.9302 μs | 68.21 KB |

Parsing scales linearly with query complexity. Tokenization is ~3–5× faster than full parsing for the same input. IN/EXISTS subqueries parse faster than derived-table subqueries due to simpler AST structure. DISTINCT aggregate queries (with multiple `COUNT(DISTINCT …)` / `SUM(DISTINCT …)` columns) parse in ~95 μs — comparable to EXISTS subqueries, since the DISTINCT modifier adds minimal AST overhead per aggregate.

#### Providers

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| CSV 1K rows | 1,393.0 μs | 27.2 μs | 37.2 μs | 944.01 KB |
| CSV 10K rows | 6,541.0 μs | 98.7 μs | 92.3 μs | 8,713.28 KB |
| CSV 1K with projection | 1,252.0 μs | 21.8 μs | 19.3 μs | 780.18 KB |
| JSON 1K rows | 1,611.0 μs | 31.2 μs | 34.7 μs | 499.88 KB |
| JSON 10K rows | 11,383.0 μs | 52.3 μs | 48.9 μs | 4,522.26 KB |

All providers share a pre-built column name index across rows, eliminating per-row Dictionary allocations at the source. `DataValue.FromScalar` caches the two most common float values (0 and 1), avoiding heap allocation and boxing for those constants. The CSV parser reuses a thread-local field buffer across rows, eliminating a `List<string>` allocation per line. Projection pushdown on CSV saves ~18% of allocated memory by skipping unreferenced columns.

#### Execution

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| SELECT * FROM data (10K) | 7.727 ms | 0.1535 ms | 0.2769 ms | 8.55 MB |
| SELECT with WHERE filter (10K) | 7.981 ms | 0.1270 ms | 0.1560 ms | 8.48 MB |
| SELECT with projection (10K) | 6.775 ms | 0.1266 ms | 0.1122 ms | 7.5 MB |
| INNER JOIN (10K × 1K) | 8.711 ms | 0.0986 ms | 0.0922 ms | 8.64 MB |
| ORDER BY + LIMIT (10K) | 8.505 ms | 0.0789 ms | 0.0700 ms | 8.83 MB |
| Uncorrelated IN subquery (10K × 1K) | 8.127 ms | 0.1612 ms | 0.1856 ms | 7.64 MB |
| Correlated EXISTS semi-join (10K × 1K) | 7.795 ms | 0.0830 ms | 0.0777 ms | 7.69 MB |
| Correlated NOT EXISTS anti-semi-join (10K × 1K) | 9.575 ms | 0.0701 ms | 0.0622 ms | 8.38 MB |
| Correlated scalar subquery (10K × 1K) | 11.071 ms | 0.2204 ms | 0.2624 ms | 9.82 MB |
| SELECT DISTINCT low cardinality (10K) | 5.797 ms | 0.0311 ms | 0.0276 ms | 6.73 MB |
| SELECT DISTINCT high cardinality (10K) | 13.194 ms | 0.0994 ms | 0.0830 ms | 8.78 MB |
| COUNT(DISTINCT) per group (10K) | 8.028 ms | 0.0954 ms | 0.0797 ms | 7.58 MB |

Full scan of 10K rows allocates 8.55 MB — shared provider schemas and operator schemas eliminated per-row Dictionary and name-array allocations end-to-end. Boolean-valued `DataValue` instances (0 and 1) are cached singletons, eliminating heap allocation and float boxing in every comparison, filter, and join condition evaluation. Qualified column references (`table.column`) cache their pre-computed lookup string on the AST node, avoiding per-row string interpolation. CAST expressions pool their argument arrays and cache target-type `DataValue` wrappers. Function argument arrays are pooled via `ArrayPool`, and LIKE patterns use cached compiled regex. ORDER BY + LIMIT uses a bounded priority queue when LIMIT is present.

All subquery types now execute at JOIN-competitive speeds. Uncorrelated IN subqueries are constant-folded to a literal list at plan time, then evaluated via a lazily-built `HashSet<DataValue>` for O(1) lookups per row (~8 ms, on par with WHERE filter). Correlated EXISTS and NOT EXISTS are rewritten into hash-based semi-joins and anti-semi-joins (~8–10 ms). Correlated scalar subqueries are decorrelated into hash-based lookups (~11 ms, 9.82 MB).

SELECT DISTINCT uses a streaming hash-dedup operator backed by `HashSet<CompositeKey>`. Low-cardinality DISTINCT (5 unique categories from 10K rows) completes in ~6 ms — faster than a full scan because only 5 rows pass through downstream operators. High-cardinality DISTINCT (10K unique combinations) takes ~13 ms, reflecting the cost of hashing and storing every row; the Gen2 GC pressure (234 MB promoted) shows the hash set's large-object overhead. COUNT(DISTINCT) per group runs in ~8 ms via `DistinctAccumulatorDecorator`, which wraps each aggregate accumulator with a per-group `HashSet` filter — comparable to a standard GROUP BY since the dedup cost is amortized across groups.

#### Statistics

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| Collect stats 1K rows | 3.026 ms | 0.0585 ms | 0.0651 ms | 2.36 MB |
| Collect stats 10K rows | 52.949 ms | 1.0530 ms | 1.5434 ms | 20.79 MB |
| Merge two 5K collectors | 59.665 ms | 1.1913 ms | 3.0322 ms | 24.94 MB |

Statistics collection runs all accumulators (numeric, string, vector, image, cardinality, entropy, histogram, quantile, top-K) in a single pass. Merge cost is comparable to a fresh 10K collection because both collectors' reservoir samples and HyperLogLog registers must be combined.

#### Output

| Method | Mean | Error | StdDev | Allocated |
|--------|-----:|------:|-------:|----------:|
| CSV write 1K rows | 1.328 ms | 0.0191 ms | 0.0170 ms | 124.45 KB |
| CSV write 10K rows | 7.870 ms | 0.1306 ms | 0.1222 ms | 1,145.68 KB |
| CSV write 10K rows with sharding (1000/shard) | 15.026 ms | 0.2995 ms | 0.8148 ms | 1,256.27 KB |

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