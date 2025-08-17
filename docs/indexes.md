# Source Indexes

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Architecture](architecture.md) · [Star Schema](star-schema.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest can build `.datum-index` sidecar files that accelerate queries by enabling chunk-level pruning without reading source data. Any provider and format can benefit — unlike Parquet's built-in row group statistics, source indexes are format-independent and support bloom filters, sorted value indexes, and cached schema inference.

## Binary format

A `.datum-index` file uses a TOC-at-end layout (like ZIP), enabling sequential writing and random-access reading:

```
┌─────────────────────────────────┐
│  Header (16 bytes)              │
│    Magic: DTIX (4 bytes ASCII)  │
│    Version: uint16 (currently 3)│
│    Flags: uint16 (reserved)     │
│    TOC offset: int64            │
├─────────────────────────────────┤
│  Section: Fingerprint           │
│  Section: Schema                │
│  Section: ChunkDirectory        │
│  Section: BloomFilters     (opt)│
│  Section: SortedIndexes    (opt)│
│  Section: ZipDirectory     (opt)│
│  Section: RowOffsets       (opt)│
│  Section: TableDirectory   (opt)│
│  Section: BTreeIndexes     (opt)│
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
| 6 | RowOffsets | Per-chunk row byte offsets for line-oriented seeking |
| 7 | TableDirectory | Multi-table index mapping (table name → section ranges) |
| 8 | BTreeIndexes | Per-column B+Tree indexes with compressed leaf pages for disk-resident key lookup |

Fingerprint, Schema, and ChunkDirectory are always present. BloomFilters, SortedIndexes, BTreeIndexes, ZipDirectory, RowOffsets, and TableDirectory are written only when applicable.

### Format versions

| Version | Changes |
|---------|--------|
| 1 | Initial format |
| 2 | Added RowOffsets section, multi-table TableDirectory |
| 3 | Added per-column Zstd compression for SortedIndexes section, BTreeIndexes section type |

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

### Automatic column selection

When no explicit `--index-columns` are provided, the programmatic `DatumIngester.BuildIndexAsync` API (and the compute backend) automatically selects columns for sorted indexing based on their data kind. Compact types are indexed; wide types are not:

| Eligible (auto-indexed) | Skipped |
|------------------------|---------|
| Scalar (numeric) | Vector |
| Boolean | Matrix |
| Date, DateTime, Time, Duration | Tensor |
| UUID | Image |
| String (≤ 16 characters) | JsonValue |
| UInt8 | Array |

Auto-indexing is controlled by the `AutoIndexColumns` option (default: `true`). Set `IndexAllColumns = true` to override and index every column regardless of type.

To cap how many columns are indexed (useful in multi-tenant environments where index size must be bounded), set `MaxIndexedColumns` to the desired limit. Only the first N eligible columns in schema order are indexed.

### Sorted index compression

As of format version 3, sorted indexes are compressed per-column using Zstd. This typically achieves 5–10× size reduction with negligible read latency impact (decompression is sub-millisecond).

The compressed envelope format per column is:

| Field | Type | Description |
|-------|------|-------------|
| ColumnName | string | Column name (length-prefixed UTF-8) |
| EntryCount | int32 | Number of index entries |
| UncompressedLength | int32 | Byte length before compression |
| CompressedLength | int32 | Byte length after compression |
| CompressedPayload | byte[] | Zstd-compressed entry data |

Compression is enabled by default in the `DatumIndexerOptions` (`CompressIndexes = true`). The CLI `--with-index` flag writes uncompressed indexes (version 2) for maximum compatibility. The reader accepts both compressed (v3) and uncompressed (v2) indexes transparently.

## B+Tree indexes

For columns with millions of entries, flat sorted value indexes require loading the entire array into memory for binary search — impractical for datasets with tens of millions of rows. B+Tree indexes solve this by storing entries in a disk-resident tree structure with demand-paged 8 KiB pages, enabling O(log n) lookups without materializing the full index.

### When B+Trees are used

B+Tree indexes are selected automatically when a column exceeds 5,000,000 entries. The engine inspects each column's total entry count after scanning all rows and chooses the most efficient representation per column. A single index file may contain both sorted value index sections and B+Tree sections for different columns.

### On-disk format

Each B+Tree column is stored as a contiguous sequence of fixed-size 8 KiB pages within a `BTreeIndexes` section:

```
[BTreeSectionHeader]
[Leaf₀] [Leaf₁] ... [Leafₙ]
[Internal₀] [Internal₁] ... [Internalₘ]
[Root page]
```

Pages are written bottom-up: leaf pages first (in sort order), then internal levels, ending with the root. The section header records the root page index, total entry count, tree height, page size, and page count.

**Section header layout:**

| Field | Type | Description |
|-------|------|-------------|
| ColumnName | string | Indexed column name (length-prefixed UTF-8) |
| KeyKind | byte | `DataKind` of the key values |
| RootPageIndex | uint32 | Zero-based index of the root page within the section |
| EntryCount | int64 | Total key-pointer pairs across all leaves |
| TreeHeight | uint16 | Number of levels (1 = leaf-only, 2 = root + leaves, …) |
| PageSize | uint16 | Always 8,192 bytes |
| PageCount | uint32 | Total pages (leaves + internals) |

**Leaf page layout (8 KiB):**

| Field | Bytes | Description |
|-------|-------|-------------|
| PageType | 1 | `0x01` = leaf |
| KeyCount | 2 | Number of entries in this leaf |
| Reserved | 1 | Padding |
| PreviousLeaf | 4 | Page index of previous leaf (`0xFFFFFFFF` if first) |
| NextLeaf | 4 | Page index of next leaf (`0xFFFFFFFF` if last) |
| UncompressedSize | 4 | Byte length of entries before compression |
| CompressedSize | 4 | Byte length of Zstd-compressed payload |
| CompressedPayload | variable | Zstd-compressed entry data (key + chunk index + row offset per entry) |

Leaf pages form a doubly-linked list for efficient range scans and ordered traversal. Entries within each leaf are Zstd-compressed, typically fitting hundreds of entries per 8 KiB page.

**Internal page layout (8 KiB):**

| Field | Bytes | Description |
|-------|-------|-------------|
| PageType | 1 | `0x02` = internal |
| KeyCount | 2 | Number of separator keys |
| Reserved | 1 | Padding |
| Payload | 8,188 | Alternating child pointers (uint32) and separator keys |

Internal pages store separator keys and child pointers. For *k* separator keys, there are *k + 1* child pointers. The tree is navigated top-down from the root: compare the search key against separators to choose the correct child pointer, descending until a leaf is reached.

### Bulk loading

B+Trees are built using a streaming bulk-load algorithm during index construction. The bulk loader:

1. Consumes sorted entries from the spill writer's k-way merge (no materialization)
2. Packs entries into Zstd-compressed leaf pages, dynamically adjusting the entries-per-leaf target based on actual compression ratios
3. Collects separator keys (first key of each new leaf) for internal node construction
4. Builds internal levels bottom-up from the separator key list

The bulk loader holds at most one leaf page worth of entries plus the separator key list in memory. For a 32-million-row column with ~22,000 leaves, the separator list is a few megabytes.

### Runtime behavior

At query time, B+Tree indexes are accessed through the same `IColumnIndex` interface as sorted value indexes — query operators are polymorphic and work identically with either implementation. The `BPlusTreeReader` decodes pages on demand and caches recently accessed pages in an LRU cache (default capacity: 128 pages = 1 MB).

Supported operations:

| Method | Description |
|--------|-------------|
| `FindExact(key)` | Point lookup — returns all entries matching a key |
| `FindRange(low, high)` | Range scan — returns entries within bounds |
| `FindChunksContaining(key)` | Chunk-level point lookup |
| `FindChunksInRange(low, high)` | Chunk-level range lookup |
| `FindChunksLessThan(key)` | Chunk-level less-than |
| `FindChunksGreaterThan(key)` | Chunk-level greater-than |
| `TraverseForward()` | Full ascending scan (leaf chain traversal) |
| `TraverseBackward()` | Full descending scan (reverse leaf chain) |

All chunk-level methods return the same `IReadOnlySet<int>` as sorted value indexes, so `ScanOperator` pruning works transparently. `IndexScanOperator` uses `TraverseForward` / `TraverseBackward` for ORDER BY elimination, also transparently — B+Tree-backed columns benefit from the same ORDER BY optimization as sorted-indexed columns.

### Memory profile

B+Tree indexes are designed for bounded memory usage regardless of dataset size:

| Component | Memory | Notes |
|-----------|--------|-------|
| Page cache | ~1 MB | 128 × 8 KiB pages (LRU eviction) |
| Separator keys (build) | ~1–5 MB | Per-column, freed after building |
| Bloom filters (build) | ~2.5 KB/chunk/column | Persistent during build if bloom is enabled |
| Spill files | Disk only | Sorted runs flushed per chunk, k-way merged at write time |

The streaming build path peaks at approximately 300–900 MB for a 32-million-row dataset (depending on column count and whether bloom filters are enabled), compared to multiple gigabytes for the flat sorted array approach.

## Bitmap indexes

Bitmap indexes target low-cardinality columns — those with at most 256 distinct values (configurable via `IndexConstants.BitmapAutoThreshold`). Unlike sorted or B+Tree indexes that map values to row positions, bitmap indexes store one bitset per distinct value per chunk: bit *i* is set when the row at offset *i* within the chunk equals that value.

### When bitmaps are used

During index building, every auto-indexable column gets a bitmap accumulator. If the column's observed cardinality stays within the threshold (≤ 256) for every chunk, a `BitmapColumnIndex` is written to the index file. Columns that exceed the threshold are abandoned — their sorted or B+Tree indexes still apply.

Bitmaps do not replace sorted or B+Tree indexes. Both coexist: sorted/B+Tree serve range queries and exact-seek, bitmaps serve equality composition and row-level filtering.

### Composition

Bitmap indexes support bitwise composition across multiple columns:

| Expression | Composition |
|-----------|-------------|
| `col = value` | Single-value bitmap lookup |
| `col != value` | NOT of equality bitmap |
| `col IN (v1, v2, ...)` | OR union of each value's bitmap |
| `a = 'x' AND b = 'y'` | AND of per-column bitmaps |
| `a = 'x' OR b = 'y'` | OR of per-column bitmaps |

### Chunk-level pruning

Before reading any rows, `ScanOperator` checks `BitmapColumnIndex.ChunkContainsValue()` for each equality predicate on a bitmap-indexed column. If a value is absent from the chunk's value set, the entire chunk is skipped.

### Row-level filtering

Within surviving chunks, the engine builds a combined bitmap mask by evaluating the filter expression tree against the chunk's per-value bitsets. Rows where the corresponding bit is 0 are skipped without being materialized. This is precise (no false negatives or false positives) unlike statistics-based pruning which is conservative.

### Request bitmap indexes

Bitmap columns are auto-detected during index building for all auto-indexable kinds. To force specific columns:

```
datum-ingest index --source data.csv --auto-index --bitmap-columns color,status
```

### Storage format

Each bitmap is Zstd-compressed and stored under `IndexSectionType.BitmapIndexes` (section type 9). At a chunk size of 10,000 rows, each uncompressed bitmap is 1.25 KB; Zstd typically achieves 5–20× compression for sparse bitmaps.

### Manifest index hints

When a manifest is generated (via `ManifestBuilder`), per-column `ColumnIndexHint` records are included. These hints record whether a column was observed to be bitmap-eligible (≤ 256 distinct values), sort-eligible, or B+Tree-eligible. On subsequent ingestion runs, `SourceIndexBuilder` can consult these hints to override auto-detection.

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

### Level 4: Bitmap index pruning

For equality and IN predicates on bitmap-indexed columns, `ScanOperator` checks whether the target value exists in the chunk's bitmap value set. A chunk is skipped if the value is absent — this is exact (no false positives), unlike statistics-based pruning. Within surviving chunks, bitmap row-level filtering further eliminates individual rows by composing per-value bitsets.

All four levels are applied in sequence; each subsequent level can only reduce the set of active chunks further. EXPLAIN ANALYZE reports the total and pruned chunk counts.

### Chunk-level seeking

When chunks are pruned, `ScanOperator` must read only the surviving chunks. Providers that implement `ISeekableTableProvider` support random-access row reads — the engine calls `ReadRowRangeAsync` with each surviving chunk's row offset and count, seeking directly to the target rows without streaming through skipped data. Providers without seeking support fall back to streaming all rows and discarding those outside surviving chunks by row index.

Currently, the IDX, Parquet, and HDF5 providers implement `ISeekableTableProvider`. Line-oriented formats (CSV, JSONL) do not — they rely on byte-range measurement via `IChunkMeasuringProvider` instead.

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

### Co-generate an index and manifest

```bash
datum-ingest index-manifest --source "csv:data=./data.csv" \
  --chunk-size 50000 \
  --bloom-columns "id,category" \
  --with-interactions
```

This creates both `data.csv.datum-index` and `data.csv.datum-manifest` in a single pass. Use `--with-interactions` to include pairwise column interaction statistics in the manifest (opt-in due to O(C²) scaling). See [Statistics & Manifest](statistics.md) for details.

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
| `--with-interactions` | Include pairwise column interactions in the manifest (`index-manifest` only). |
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

// Wrap in a SourceIndexSet container (keyed by table name)
SourceIndexSet indexSet = SourceIndexSet.Create("data", index);

// Write to disk
using FileStream output = File.Create("data.csv.datum-index");
IndexWriter writer = new();
writer.Write(indexSet, output);
```

### Build with auto-indexing

```csharp
// Auto-select compact columns, compress sorted indexes, cap at 8 columns
SourceIndexBuilder builder = new(
    bloomAllColumns: false,
    indexAllColumns: false,
    chunkSize: 10_000,
    autoIndexColumns: true,
    maxIndexedColumns: 8);

SourceIndex index = await builder.BuildAsync(
    descriptor, provider, sourceStream, CancellationToken.None);
```

### Force B+Tree indexes

```csharp
// Force B+Tree for all indexed columns (useful for testing or known-large datasets)
DatumIndexerOptions options = new()
{
    AutoIndexColumns = true,
    CompressIndexes = true,
};

await using DatumIndexResult result = await DatumIngester.BuildIndexAsync("data.csv.datum", options);
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

### Using DatumIngester

The `DatumIngester` class provides a two-step API: ingest source files into `.datum` format with statistics, then build indexes separately:

```csharp
// Step 1: Ingest source file → .datum + manifest + sample preview (no index)
await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync("data.csv");

// Access the sample preview (25 representative rows collected via reservoir sampling)
foreach ((string tableName, SamplePreview preview) in ingestion.Samples)
{
    Console.WriteLine($"Table {tableName}: {preview.Features.Count} features, {preview.Samples.Count} samples");
}

// Serialize the sample preview to JSON
string sampleJson = SamplePreviewSerializer.Serialize(ingestion.Samples.Values.First());
await SamplePreviewSerializer.WriteToFileAsync(
    ingestion.Samples.Values.First(), "data.csv.datum-sample");

// Step 2: Build index from the .datum file
DatumIndexerOptions options = new()
{
    ChunkSize = 10_000,
    AutoIndexColumns = true,
    CompressIndexes = true,
    MaxIndexedColumns = 8,
};

await using DatumIndexResult index = await DatumIngester.BuildIndexAsync("data.csv.datum", options);

// Optional: receive progress updates during indexing (callback is invoked synchronously)
await using DatumIndexResult indexWithProgress = await DatumIngester.BuildIndexAsync(
    "data.csv.datum", options, progress: snapshot =>
        Console.WriteLine($"{snapshot.TableName}: {snapshot.PercentComplete}% ({snapshot.RowsProcessed}/{snapshot.TotalRows})"));
```

`DatumIngester.IngestAsync` handles format conversion, statistics collection, and sample preview generation. During ingestion, 25 representative rows are collected via reservoir sampling (Algorithm R), ensuring a uniform random sample regardless of dataset size. Each row is converted to a JSON-friendly representation:

| Data kind | JSON representation |
|-----------|---------------------|
| Scalar, UInt8, Boolean | Number or boolean primitive |
| String, Date, DateTime, Time, Duration, Uuid | String (ISO 8601 for temporal types) |
| Vector | Flat numeric array `[1.0, 2.0, 3.0]` |
| Matrix | Nested array `[[1.0, 2.0], [3.0, 4.0]]` |
| Tensor | Recursively nested arrays following shape dimensions |
| Image | `"base64://…"` — resized to fit 64×64 max (aspect-preserving), re-encoded as PNG |
| UInt8Array | `"[binary data]"` sentinel |
| Array | Recursively converted element array |

`DatumIngester.BuildIndexAsync` handles index building, compression, and sidecar file writing. See [Programmatic API](api.md) for additional usage patterns.

### Read an index

```csharp
using FileStream stream = File.OpenRead("data.csv.datum-index");
IndexReader reader = new();
SourceIndexSet indexSet = reader.Read(stream);

// Register individual table indexes with the catalog
foreach (KeyValuePair<string, SourceIndex> entry in indexSet.Tables)
{
    catalog.RegisterIndex(entry.Key, entry.Value);
}
```

### Sidecar auto-discovery

Instead of manually reading and registering indexes, call `catalog.DiscoverSidecars()` after table registration to auto-discover `.datum-index` (and `.datum-manifest` and `.datum-schema`) sidecar files:

```csharp
TableCatalog catalog = new();
catalog.Register("data", "./data.csv");
catalog.DiscoverSidecars();

// The query planner automatically applies chunk pruning when an index
// is registered for a table name.
IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);
```

See [Programmatic API — Sidecar Auto-Discovery](api.md#sidecar-auto-discovery) for details.

### Register for query-time pruning

```csharp
// The query planner automatically applies chunk pruning when an index
// is registered for a table name.
catalog.RegisterIndex("data", index);

// Plan and execute — ScanOperator will prune chunks automatically
IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);
```
