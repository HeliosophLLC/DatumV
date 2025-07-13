# Source Indexes

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md)

DatumIngest can build `.datum-index` sidecar files that accelerate queries by enabling chunk-level pruning without reading source data. Any provider and format can benefit — unlike Parquet's built-in row group statistics, source indexes are format-independent and support bloom filters, sorted value indexes, and cached schema inference.

## Binary format

A `.datum-index` file uses a TOC-at-end layout (like ZIP), enabling sequential writing and random-access reading:

```
┌─────────────────────────────────┐
│  Header (16 bytes)              │
│    Magic: DTIX (4 bytes ASCII)  │
│    Version: uint16 (currently 1)│
│    Flags: uint16 (reserved)     │
│    TOC offset: int64            │
├─────────────────────────────────┤
│  Section: Fingerprint           │
│  Section: Schema                │
│  Section: ChunkDirectory        │
│  Section: BloomFilters     (opt)│
│  Section: SortedIndexes    (opt)│
│  Section: ZipDirectory     (opt)│
├─────────────────────────────────┤
│  Table of Contents              │
│    Count: int32                 │
│    Entries: (type, offset, len) │
└─────────────────────────────────┘
```

Each section is identified by an `IndexSectionType`:

| Value | Section | Purpose |
|-------|---------|---------|
| 0 | Fingerprint | Source file staleness detection |
| 1 | Schema | Cached column schema and total row count |
| 2 | ChunkDirectory | Chunk boundaries and per-column statistics |
| 3 | BloomFilters | Per-column, per-chunk probabilistic membership filters |
| 4 | SortedIndexes | Sorted distinct values for binary search lookups |
| 5 | ZipDirectory | Cached ZIP archive central directory |

Fingerprint, Schema, and ChunkDirectory are always present. BloomFilters, SortedIndexes, and ZipDirectory are written only when applicable.

## Staleness detection

Every index stores a `SourceFingerprint` combining the source file's byte size with a striped SHA-256 hash. The hashing algorithm samples 64 KiB of data at 10 MiB intervals throughout the file, concatenates the samples, and computes a single 32-byte SHA-256 digest. This detects content changes anywhere in the file without requiring a full sequential read — critical for multi-gigabyte files or HTTP range-read access patterns.

At query time, the engine recomputes the fingerprint and compares it against the stored value. A mismatch means the source has changed since the index was built, and the index is discarded.

## Schema caching

The Schema section stores the full column schema (`ColumnInfo[]` with data kinds and nullability) plus the total row count. This eliminates runtime schema inference — the engine can resolve column metadata instantly from the index rather than sampling the first 100 rows of a CSV or JSONL file.

## Chunk directory

The source is logically divided into fixed-size row chunks (default: 10,000 rows). Each chunk records:

| Field | Type | Description |
|-------|------|-------------|
| RowOffset | int64 | Zero-based starting row index in the source |
| RowCount | int64 | Number of rows in this chunk |
| SourceByteOffset | int64 | Byte position of the chunk's first row in the source file (−1 if unavailable) |
| SourceByteLength | int64 | Byte span of the chunk in the source file (−1 if unavailable) |
| ColumnStatistics | dictionary | Per-column minimum, maximum, null count, row count, and HyperLogLog cardinality estimate |

Byte offsets are populated when the provider implements `IChunkMeasuringProvider` (see [Byte-range measurement](#byte-range-measurement) below). Otherwise they default to −1, and the engine falls back to row-counting during execution.

## Bloom filters

Bloom filters are probabilistic membership structures that answer "is this value definitely absent?" with no false negatives and a configurable false positive rate (default: 1%).

### Implementation

Each bloom filter uses Kirsch-Mitzenmacher double hashing: two independent FNV-1a base hashes generate *k* bit positions via the formula *h(i) = h₁ + i · h₂ mod m*. The bit array size is computed from the optimal formula *m = −n · ln(p) / (ln 2)²* where *n* is the expected element count and *p* is the target false positive rate, clamped to [64 bits, 256 MiB].

Filters are built per column per chunk. At query time, bloom filters enable **join key pruning**: if all build-side key values are definitely absent from a chunk's bloom filter, that chunk is skipped entirely — no source data is read.

### Request bloom filters

Specify columns at index build time:

```bash
datum-ingest index --source "csv:data=./data.csv" --bloom-columns "id,category"
```

## Sorted value indexes

Sorted value indexes store every distinct value in a column alongside its chunk index and row offset, sorted for binary search. They support:

- **Exact lookup** — `FindExact(key)` returns all entries matching a value in O(log n)
- **Range lookup** — `FindRange(low, high)` returns entries within bounds
- **Chunk-level lookup** — `FindChunksContaining(key)` returns the set of chunk indexes containing a value
- **Chunk-level range** — `FindChunksInRange(low, high)`, `FindChunksLessThan(key)`, `FindChunksGreaterThan(key)`, and their inclusive variants return chunk sets for range predicates

At query time, sorted indexes enable **equality and range predicate pruning**: the engine extracts literal values from WHERE predicates and uses the sorted index to identify which chunks contain matching values, skipping all others.

### Request sorted indexes

Specify columns at index build time:

```bash
datum-ingest index --source "csv:data=./data.csv" --index-columns "user_id,timestamp"
```

## ZIP directory cache

For ZIP archive sources, the index caches the central directory — file names, compressed/uncompressed sizes, CRC-32 checksums, and local header offsets. This avoids re-parsing the ZIP central directory structure on every query.

## Byte-range measurement

Line-oriented providers (CSV, JSONL) can implement `IChunkMeasuringProvider` to pre-scan the source file and report exact byte boundaries for each row chunk. This enables byte-level seeking during query execution.

- **CSV** — quote-aware byte scanning that correctly handles multi-line quoted fields, escaped quotes, and CRLF line endings
- **JSONL** — detects data rows by looking for `{` at the start of each line, skipping blank lines and non-object content

Binary formats (Parquet, HDF5) use their own internal chunking and do not implement this interface.

For providers that support random-access row reads (`ISeekableTableProvider`), pruned chunks can be read directly by seeking to the target row offset. See [Chunk-level seeking](#chunk-level-seeking) under Query-time pruning.

## Query-time pruning

When a source index is loaded, `ScanOperator` applies three levels of chunk pruning before reading any source data:

### Level 1: Statistics-based pruning

WHERE predicates are evaluated against each chunk's min/max column statistics via `StatisticsPredicateEvaluator`. A chunk is skipped only when the predicate is *provably unsatisfiable* — no false negatives.

| Predicate | Skip condition |
|-----------|----------------|
| `col = value` | value < min or value > max |
| `col != value` | min = max = value |
| `col < value` | min ≥ value |
| `col <= value` | min > value |
| `col > value` | max ≤ value |
| `col >= value` | max < value |
| `col IN (...)` | All values outside [min, max] |
| `col NOT IN (...)` | min = max and that value is in the list |
| `col BETWEEN low AND high` | max < low or min > high |
| `col IS NULL` | null count = 0 |
| `col IS NOT NULL` | null count = row count |

AND predicates skip if *either* operand is unsatisfiable. OR predicates skip only if *both* are unsatisfiable.

### Level 2: Bloom filter pruning

For join operations, the query planner collects build-side key values and probes each chunk's bloom filter. A chunk is skipped if *no* build-side key could possibly be present (bloom filters guarantee no false negatives).

### Level 3: Sorted index pruning

Predicate expressions with literal values are checked against sorted value indexes. The engine identifies exactly which chunks contain matching values and skips all others.

| Predicate | Index method |
|-----------|-------------|
| `col = value` | `FindChunksContaining(value)` |
| `col < value` | `FindChunksLessThan(value)` |
| `col <= value` | `FindChunksLessThanOrEqual(value)` |
| `col > value` | `FindChunksGreaterThan(value)` |
| `col >= value` | `FindChunksGreaterThanOrEqual(value)` |
| `col BETWEEN low AND high` | `FindChunksInRange(low, high)` |
| `col IN (v1, v2, ...)` | Union of `FindChunksContaining` per value |

Reversed operand order (e.g., `5 > col`) is handled by flipping the comparison operator. NOT BETWEEN and NOT IN are not eligible — they are exclusions, not point/range lookups.

All three levels are applied in sequence; each subsequent level can only reduce the set of active chunks further. EXPLAIN ANALYZE reports the total and pruned chunk counts.

### Chunk-level seeking

When chunks are pruned, `ScanOperator` must read only the surviving chunks. Providers that implement `ISeekableTableProvider` support random-access row reads — the engine calls `ReadRowRangeAsync` with each surviving chunk's row offset and count, seeking directly to the target rows without streaming through skipped data. Providers without seeking support fall back to streaming all rows and discarding those outside surviving chunks by row index.

Currently, the IDX and Parquet providers implement `ISeekableTableProvider`. Line-oriented formats (CSV, JSONL) do not — they rely on byte-range measurement via `IChunkMeasuringProvider` instead.

### Exact row seek for equality predicates

When the provider supports seeking and the filter contains top-level AND-chained equality predicates on sorted-indexed columns, the scan operator bypasses chunk-level reading entirely. Instead, it calls `FindExact` on the sorted index to obtain exact `(chunkIndex, rowOffsetInChunk)` entries for matching values, converts them to absolute row positions, filters to only non-pruned chunks, and seeks directly to each matching row.

This reduces I/O from reading entire surviving chunks (potentially thousands of rows per chunk) to reading only the rows that actually match. For high-selectivity predicates like `WHERE user_id = 12345` against a million-row source with a sorted index on `user_id`, this is the difference between reading the entire chunk containing the value and reading exactly one row.

When multiple indexed predicates are present (e.g., `WHERE user_id = 42 AND category = 'train'`), the engine picks the most selective (fewest matching entries) to minimize seeks. The downstream `FilterOperator` handles any remaining predicates.

BEYOND equality, the seek path also handles:

| Predicate | Index method |
|-----------|-------------|
| `col = value` | `FindExact(value)` |
| `col BETWEEN low AND high` | `FindRange(low, high)` |
| `col IN (v1, v2, ...)` | Union of `FindExact` per value |

Range comparisons (`<`, `<=`, `>`, `>=`) are not eligible for row seek — they may match a large fraction of the dataset, where individual row seeking would be slower than a sequential scan. They benefit only from chunk-level pruning.

OR predicates are not eligible for exact seek because the index result set for one branch may miss rows matching the other branch.

## ORDER BY optimization

Sorted value indexes can eliminate the `OrderByOperator` entirely. When all of the following conditions are met, the query planner substitutes an `IndexScanOperator` for the usual `ScanOperator` + `OrderByOperator` combination:

1. The ORDER BY clause references a single column
2. A sorted value index exists for that column
3. The underlying provider implements `ISeekableTableProvider`

The `IndexScanOperator` walks the sorted index entries in order (ascending or descending) and fetches each row via `ReadRowRangeAsync`. Consecutive entries in the same chunk are batched into a single seek call. Because rows emerge already sorted, no materialization or in-memory sort is needed.

This optimization composes with LIMIT — an `ORDER BY col LIMIT N` query reads only the first *N* index entries, avoiding a full table scan entirely.

## CLI usage

### Build a standalone index

```bash
datum-ingest index --source "csv:data=./large_dataset.csv" \
  --chunk-size 50000 \
  --bloom-columns "id,category" \
  --index-columns "id"
```

This creates `large_dataset.csv.datum-index` alongside the source file.

### Co-generate an index during query output

```bash
datum-ingest query "
  SELECT id, normalize(value) AS norm_value
  FROM data
  INTO 'output/result.csv' SHARD ON sample_count 10000
" --source "csv:data=./data.csv" --with-index
```

The `--with-index` flag builds an index for each source as rows flow through the pipeline, at no additional I/O cost.

### Load a pre-built index at query time

```bash
datum-ingest query "SELECT * FROM data WHERE category = 'train'" \
  --source "csv:data=./data.csv" \
  --index "./data.csv.datum-index"
```

The engine validates the index fingerprint against the source, applies chunk-level pruning, and streams only matching rows.

### CLI flags reference

| Flag | Description |
|------|-------------|
| `--index <path>` | Load a pre-built `.datum-index` file. Repeatable for multiple sources. |
| `--with-index` | Co-generate a `.datum-index` for each source during query execution. |
| `--chunk-size <n>` | Rows per index chunk (default: 10,000). |
| `--bloom-columns <cols>` | Comma-separated column names to build bloom filters for. |
| `--index-columns <cols>` | Comma-separated column names to build sorted value indexes for. |

## Programmatic API

### Build an index

```csharp
SourceIndexBuilder builder = new(
    chunkSize: 10_000,
    bloomColumns: new HashSet<string> { "id" },
    indexColumns: new HashSet<string> { "id" });

SourceIndex index = await builder.BuildAsync(
    descriptor, provider, sourceStream, CancellationToken.None);

// Write to disk
using FileStream output = File.Create("data.csv.datum-index");
IndexWriter writer = new();
writer.Write(index, output);
```

### Co-generate during output writing

```csharp
SourceIndexBuilder builder = new(chunkSize: 10_000);
SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(
    sourceStream, CancellationToken.None);

IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

await foreach (Row row in plan.ExecuteAsync(context))
{
    incremental.AddRow(row);
    await writer.WriteRowAsync(row);
}

SourceIndex index = incremental.Finalize();
```

### Read an index

```csharp
using FileStream stream = File.OpenRead("data.csv.datum-index");
IndexReader reader = new();
SourceIndex index = reader.Read(stream);

// Register with the catalog for automatic pruning
catalog.RegisterIndex("data", index);
```

### Register for query-time pruning

```csharp
// The query planner automatically applies chunk pruning when an index
// is registered for a table name.
catalog.RegisterIndex("data", index);

// Plan and execute — ScanOperator will prune chunks automatically
IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);
```
