# Source Indexes

[← Back to README](../README.md) · [SQL Reference](sql/select.md) · [Functions](functions/string.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Architecture](architecture.md) · [Star Schema](star-schema.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest builds `.datum-index` sidecar files that accelerate queries by enabling chunk-level pruning without reading source data. Any provider and format can benefit — unlike Parquet's built-in row group statistics, source indexes are format-independent and support bloom filters, B+Tree indexes, bitmap indexes, and cached schema inference.

A separate `.datum-pkindex` sidecar holds a maintained mutable B+Tree backing a table's `PRIMARY KEY` constraint. See [Mutable B+Tree (PRIMARY KEY index)](#mutable-btree-primary-key-index) at the end of this page.

## Binary format

A `.datum-index` file uses a directory-at-start layout, enabling sequential writing and random-access reading. The whole file is mmap-friendly — readers locate any section by offset without a full scan, and fixed-width payloads (sorted index keys, B+Tree pages, bitmap bitsets) can be binary-searched directly through a `MemoryMappedViewAccessor`.

```
┌─────────────────────────────────────────────┐
│  Header (24 bytes)                          │
│    Magic: DXIX (4 bytes ASCII)              │
│    Version: int32 (currently 6)             │
│    Flags: int32 (reserved, 0)               │
│    SectionCount: int32                      │
│    FileLength: int64                        │
├─────────────────────────────────────────────┤
│  Section Directory (SectionCount × 18 B)    │
│    Per entry:                               │
│      SectionType: byte                      │
│      TableIndex:  byte (0xFF = shared)      │
│      Offset:      int64                     │
│      Length:      int64                     │
├─────────────────────────────────────────────┤
│  Section payloads (contiguous)              │
│    Section: Fingerprint                     │
│    Section: TableDirectory                  │
│    Per table:                               │
│      Section: Schema                        │
│      Section: ChunkDirectory                │
│      Section: BloomFilters       (opt)      │
│      Section: SortedIndexes      (opt)      │
│      Section: BTreePages         (opt)      │
│      Section: BitmapIndexes      (opt)      │
└─────────────────────────────────────────────┘
```

Each section is identified by a `UnifiedIndexSectionType`:

| Value | Section | Purpose |
|-------|---------|---------|
| 0 | Fingerprint | Source file staleness detection |
| 1 | TableDirectory | Maps table indexes to table names within a multi-table file |
| 2 | Schema | Cached column schema and total row count |
| 3 | ChunkDirectory | Chunk boundaries and per-column min/max/null/cardinality statistics |
| 4 | BloomFilters | Per-column, per-chunk probabilistic membership filters |
| 5 | SortedIndexes | Per-column fixed-width sorted key arrays + locators for binary-search lookup |
| 6 | BTreePages | Per-column B+Tree indexes as contiguous 8 KiB pages for demand-paged lookup |
| 7 | BitmapIndexes | Per-column bitmap indexes for low-cardinality columns |

Each directory entry tags its section with a `TableIndex` byte. Fingerprint and TableDirectory use the reserved value `0xFF` to indicate they are shared across all tables; per-table sections carry the table's zero-based index.

Fingerprint, TableDirectory, Schema, and ChunkDirectory are always present. BloomFilters, SortedIndexes, BTreePages, and BitmapIndexes are written only when the column is eligible for that index type.

### Format versions

| Version | Changes |
|---------|--------|
| 1–5 | Earlier layouts; not produced by current writers. |
| 6 | Current unified layout. Single `.datum-index` file with `DXIX` magic, directory-at-start, fixed-width mmap-friendly sorted-index keys, B+Tree pages, and bitmap sections all in-line. |

## Staleness detection

Every index stores a `SourceFingerprint` combining the source file's byte size with a striped SHA-256 hash. The hashing algorithm samples 64 KiB of data at 10 MiB intervals throughout the file, concatenates the samples, and computes a single 32-byte SHA-256 digest. This detects content changes anywhere in the file without requiring a full sequential read — critical for multi-gigabyte files or HTTP range-read access patterns.

At query time, the engine recomputes the fingerprint and compares it against the stored value. A mismatch means the source has changed since the index was built, and the index is discarded.

## Schema caching

The Schema section stores the full column schema (`ColumnInfo[]` with data kinds and nullability) plus the total row count. This eliminates runtime schema inference — the engine can resolve column metadata instantly from the index rather than sampling the first 100 rows of a CSV or JSONL file.

## Chunk directory

The source is logically divided into fixed-size row chunks (default: 10,000 rows; see `IndexConstants.DefaultChunkSize`). Each chunk records:

| Field | Type | Description |
|-------|------|-------------|
| RowOffset | int64 | Zero-based starting row index in the source |
| RowCount | int64 | Number of rows in this chunk |
| ColumnStatistics | dictionary | Per-column minimum, maximum, null count, row count, and HyperLogLog cardinality estimate |

Per-chunk statistics are encoded as a fixed-width zone map in the `ChunkDirectory` section, enabling random-access lookup by chunk index without decoding earlier entries. See [`MappedChunkDirectory`](../src/DatumIngest/Indexing/MappedChunkDirectory.cs) for the on-disk layout.

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

## Sorted value indexes (legacy)

Sorted value indexes are a legacy `.datum-index` section. New index builds emit B+Tree indexes instead — sorted indexes can't be incrementally maintained, and B+Tree covers the same point and range query surface. Existing `.datum-index` files containing sorted-index sections still load and serve queries; the on-disk format is preserved for backward compatibility but no current writer emits new sorted-index sections.

The remainder of this section documents the on-disk format for read-side compatibility.

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

When no explicit `--index-columns` are provided, the indexing pipeline automatically selects columns for sorted indexing based on their data kind. Compact types are indexed; wide types are not. The eligibility rule is in [`SourceIndexBuilder.IsAutoIndexableKind`](../src/DatumIngest/Indexing/SourceIndexBuilder.cs):

| Eligible (auto-indexed) | Skipped |
|------------------------|---------|
| Float32, Float64 | Vector |
| Int8, Int16, Int32, Int64 | Matrix |
| UInt8, UInt16, UInt32, UInt64 | Tensor |
| Boolean | Image |
| Date, DateTime, Time, Duration | JsonValue |
| Uuid | Array, Struct |
| String (tentative — dropped if values exceed 16 chars) | UInt8Array |

Auto-indexing is controlled by the `autoIndexColumns` constructor flag on `SourceIndexBuilder` (or `IndexOptions.Columns = IndexColumnSelection.Auto` on the high-level `Indexer` API). Setting `indexAllColumns: true` overrides the eligibility filter and indexes every column.

### On-disk encoding

Sorted indexes are stored inline within the unified `.datum-index` file as fixed-width key arrays plus parallel locators. The layout is mmap-friendly: query operators binary-search directly through a `MemoryMappedViewAccessor` without decompression, deserialization, or per-column heap allocation.

**Keys array**: `entryCount × keyWidth` bytes in sort-preserving binary encoding. For numeric and temporal kinds, `SequenceCompareTo` on raw bytes gives the correct ordering — no decoding needed for comparison. Key encodings:

| Kind | Width | Encoding |
|------|-------|----------|
| Boolean | 1 | `0x00` = false, `0x01` = true |
| UInt8 | 1 | Raw byte |
| Int8 | 1 | XOR sign bit (`0x80`) |
| UInt16 | 2 | Big-endian |
| Int16 | 2 | Big-endian + sign flip |
| UInt32 | 4 | Big-endian |
| Int32 | 4 | Big-endian + sign flip |
| Float32 | 4 | IEEE-to-sortable (sign flip + conditional complement) |
| Date | 4 | DayNumber as Int32, big-endian + sign flip |
| UInt64 | 8 | Big-endian |
| Int64 | 8 | Big-endian + sign flip |
| Float64 | 8 | IEEE-to-sortable |
| DateTime | 8 | UTC ticks as Int64, big-endian + sign flip |
| Time | 8 | Ticks as Int64, big-endian + sign flip |
| Duration | 8 | Ticks as Int64, big-endian + sign flip |
| String | 8 | `(int32 offset, int32 length)` into string table |
| Uuid | 16 | Raw 16 bytes (RFC 4122 byte order) |

**Locators array**: `entryCount × 12` bytes. Fixed-width `[int32 ChunkIndex | int64 RowOffsetInChunk]`, parallel to the keys array.

**String table** (string columns only): Packed UTF-8 bytes with deduplication. String keys store `(offset, length)` references into this region. Binary search for string keys dereferences the string table for comparison rather than relying on byte ordering.

### Multi-tenant memory profile

The fixed-width mmap layout means the OS pages in only the regions touched by each binary search (typically 3–5 pages per point lookup). Multiple sessions reading the same file share physical memory pages at the OS level — ten concurrent sessions querying the same table do not multiply memory 10×. On-disk size is larger than a Zstd-compressed encoding (~8× for typical columns) but acceptable for local ML ETL workloads where source datasets already occupy hundreds of megabytes. Compression is a transport concern: blob storage can compress the file with Zstd; after download, the decompressed file is mmap'd directly.

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

Each bitmap is Zstd-compressed and stored under `UnifiedIndexSectionType.BitmapIndexes` (section type 7). At a chunk size of 10,000 rows, each uncompressed bitmap is 1.25 KB; Zstd typically achieves 5–20× compression for sparse bitmaps.

### Manifest index hints

When a manifest is generated (via `ManifestBuilder`), per-column `ColumnIndexHint` records are included. These hints record whether a column was observed to be bitmap-eligible (≤ 256 distinct values), sort-eligible, or B+Tree-eligible. On subsequent ingestion runs, `SourceIndexBuilder` can consult these hints to override auto-detection.

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

Currently, the `.datum`, Parquet, and HDF5 providers implement `ISeekableTableProvider`. Line-oriented formats (CSV, JSONL) do not — pruned chunks are streamed through and discarded by row index.

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

### Build an index from a `.datum` file

The `Indexer` class is the single-call public entry point. It reads a `.datum` file, accumulates per-chunk statistics and acceleration structures (bloom filters, bitmap indexes, sorted or B+Tree column indexes), and writes a `.datum-index` sidecar.

```csharp
Indexer indexer = new(pool);

DatumFileDescriptor source = new("data.datum");
OutputDescriptor destination = new("data.datum-index");

IndexResult result = await indexer.IndexAsync(source, destination, cancellationToken);

Console.WriteLine($"{result.RowCount} rows, {result.ChunkCount} chunks, " +
                  $"{result.IndexedColumns.Count} indexed columns " +
                  $"({result.BytesWritten} bytes, {result.Elapsed})");
```

### Tune column selection and memory profile

`IndexOptions` controls per-column index policy and the build-time memory profile. The default (`IndexOptions.Default`) auto-selects compact columns; `IndexOptions.MultiTenantServer` lowers the per-build working set for processes that share memory with concurrent query workloads.

```csharp
IndexOptions options = IndexOptions.Default with
{
    Columns = new IndexColumnSelection.Explicit(["user_id", "category"]),
    ChunkSize = 50_000,
};

IndexResult result = await indexer.IndexAsync(source, destination, options, cancellationToken);
```

`IndexColumnSelection` variants:

| Variant | Behavior |
|---------|----------|
| `Auto` | Select compact columns via `IsAutoIndexableKind` (default). |
| `All` | Index every column in the schema, including reference types. |
| `Explicit(columns)` | Index only the named columns; others receive no index. |
| `None` | Build no column indexes. Zone maps and chunk directory are still produced. |

### Streaming construction (lower-level)

When indexing must happen alongside another stream of `RowBatch`es — for example during `INSERT INTO` from a query plan — call `SourceIndexBuilder.CreateIncrementalBuilder` directly:

```csharp
using FileStream sourceStream = File.OpenRead("data.csv");
SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(
    sourceStream, cancellationToken);

SourceIndexBuilder builder = new(
    bloomAllColumns: false,
    indexAllColumns: false,
    autoIndexColumns: true);

using IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

await foreach (RowBatch batch in plan.ExecuteAsync(context))
{
    incremental.AddBatch(batch);
    pool.ReturnRowBatch(batch);
}

SourceIndex index = incremental.Finalize();
SourceIndexSet indexSet = SourceIndexSet.Create("data", index);
```

`AddBatch` is the batch-aware streaming entry point; `AddRow(row, arena)` is also available for per-row callers.

> **Note:** Persisting a `SourceIndexSet` to disk goes through `Indexer.IndexAsync`. The lower-level `SourceIndexBuilder` path is intended for callers that consume the resulting `SourceIndex` in-process (e.g. registering it with a temp-table catalog).

### Reading and registering indexes

Reading is handled internally by the table provider chain — there is no public reader class. Indexes registered in the table catalog are picked up automatically by `ScanOperator` for chunk pruning, `IndexScanOperator` for ORDER BY elimination, and join planning for bloom-filter probing. See [Programmatic API](api.md#sidecar-auto-discovery) for the catalog-level registration surface.

## Temp Table Auto-Indexing

When a session-owned temp table is populated via `CREATE TEMP TABLE AS SELECT` or `INSERT INTO`, DatumIngest automatically builds a source index using `IncrementalIndexBuilder` with disk-based spill (via `SortedIndexSpillWriter`). The index is written as a `.datum-index` sidecar alongside the `.datum` file and registered on the catalog — no manual `index` command required.

Auto-indexing uses `autoIndexColumns: true`, which selects columns for sorted indexes based on compact type heuristics (the same logic used by the CLI `--with-index` flag). Bloom filters are not enabled by default for temp tables to minimize I/O overhead.

When a `.datum` file is mutated via `INSERT` / `DELETE` / `ALTER TABLE ADD COLUMN` / `ALTER TABLE DROP COLUMN`, the `.datum-index` sidecar's stored fingerprint no longer matches the data file's fingerprint. The provider detects the mismatch at open time and discards the stale index — queries fall back to scan-based execution until the index is rebuilt by re-running the indexer.

## Mutable B+Tree (PRIMARY KEY index)

When a table has a single-column `PRIMARY KEY`, the catalog maintains a separate `.datum-pkindex` sidecar — a mutable B+Tree updated on every `INSERT` rather than rebuilt on mutation. Distinct from the bulk-loaded B+Tree sections in `.datum-index`:

| Property | `.datum-index` B+Tree | `.datum-pkindex` |
|----------|----------------------|------------------|
| Build | Bulk load on `index` build pass | Created at `CREATE TABLE`, maintained on every `INSERT` |
| Leaf compression | Zstd | Uncompressed |
| Crash safety | Whole-file fingerprint; rebuild on mismatch | Dual-slot CRC32 header; torn writes recover to previous commit |
| Allocation | Bottom-up bulk pack | Bump-only (free-list reserved for future) |
| Lifetime | Invalidated on data mutation | Updated atomically with each `INSERT` commit |
| Query surface | Point + range + chunk pruning | Point lookup only (PK uniqueness check) |
| Composite keys | Yes | Single column only |

The PK index is owned by the provider, not the indexer. `CREATE TABLE` with a single-column PK creates the file via `MutableBPlusTree.Create`; `DatumFileTableProviderV2` opens it at provider construction; each `INSERT` extracts PK keys at `WriteAsync` and flushes them into the tree at `CommitAsync` after the data commit succeeds. `DROP TABLE` deletes it alongside the other sidecars.

`InsertExecutor`'s PK uniqueness check probes the tree directly when the provider exposes one, turning per-INSERT enforcement from `O(table_size)` (preload all existing PK values into a `HashSet`) into `O(insert_size × log table_size)` (per-row tree probe).

See [.datum format — `.datum-pkindex`](datum-format.md#optional-sidecar-datum-pkindex) for the on-disk layout.
