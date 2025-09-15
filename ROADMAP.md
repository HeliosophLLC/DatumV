# DatumIngest Roadmap

## Open Items

Complexity: **S** = days · **M** ≈ 1 week · **L** = 2–4 weeks · **XL** = multi-week or more

| Feature | Complexity | Justification |
|---------|:----------:|---------------|
| **Pipe mode** | S | Output writers already support CSV/JSON/NDJSON; just needs a CLI flag to target stdout rather than a file |
| **Aggregate finalization surcharge** | S | Add `FinalizationCost(long groupSize)` to `ICostAwareFunction`; implement on MEDIAN, PERCENTILE, and MODE — all other accumulators default to zero |
| ~~**Language server — semantic diagnostics**~~ | S | ✅ Type mismatch detection + unknown table/column warnings both complete |
| **`WithChildren` on `IQueryOperator`** | S | Pure internal refactor — removes the operator-type switch in `InstrumentedOperator.InstrumentRecursive` and enables generic tree rewrites; no user-visible change |
| **Data-dependent function costs** | S | Deprioritized — `ICostAwareFunction` already supports it; flat QU is accurate enough for typical vector dims (≤ 4096); trivially addable via base class when needed |
| **Excel provider** | S–M | `ITableProvider` contract is ready; needs an XLSX library (e.g. ClosedXML) and column type inference from cell values |
| **Adaptive batch sizing** | M | No existing infrastructure; requires row-size estimation from sampling and a feedback loop to adjust `BatchSize` at runtime |
| **Predicate-filtered row count propagation** | M | Planner-only change using manifest `DistinctCount`/min/max to compute selectivity; refines `TryReorderJoins` inputs so filtered subsets of large tables get the right join role — see notes below |
| **Language server — VS Code LSP extension** | M | Wraps the existing `LanguageServer` assembly in an LSP JSON-RPC stdio host; all language intelligence is already implemented |
| **Data validation** | M | `CHECK` constraints / `VALIDATE` clause — new parser tokens, AST nodes, and a diagnostic evaluation pass that reports violations rather than aborting |
| **`DataKind.Decimal` type** | L | `System.Decimal` fits inline in `DataValue` (no GC), but touches all operators, all providers (Parquet `DECIMAL` logical type), serializers, function overloads, and tests — see notes below |
| **Late materialization — Parquet** | L | `IKeyedTableProvider` on `ParquetTableProvider` with synthetic row-number keying and row-group-level seeking via ParquetSharp; defers expensive binary/nested columns past joins |
| **Late materialization — HDF5** | L | Same scope as Parquet; implicit row-index key and per-dataset lazy loading via PureHDF `HyperslabSelection` |
| **P3 — Parallel Hash Join + Aggregate** | XL | Concurrent probe workers, bounded `Channel<Row>`, `IAggregateAccumulator.Merge` on all ~15 accumulators, server-side concurrency governor — see Engine Performance § P3 |
| **P4 — DataValue Struct + Batch Execution** | XL | `DataValue` class → `readonly struct`; hundreds of call sites to migrate; foundational prerequisite for D9 vectorized execution — see Engine Performance § P4 |
| ~~**P5 — Memory-Mapped Sorted Indexes**~~ | XL | ✅ v5 fixed-width on-disk format, mmap infrastructure (`DatumMemoryMappedReader`, `UnifiedIndexWriter/Reader`), `SortedIndexKeyEncoder` per `DataKind`, breaking `.datum-index` change |
| **P6 — ReferenceStore Session Isolation** | M | `AsyncLocal<ReferenceStore?>` per-query scoping with global fallback; `BeginQueryScope`/`EndQueryScope` wired into `ComputeService` and CLI — see Engine Performance § P6 |
| **Primary key persistence** | XL | Four interdependent phases: footer format, shard-aware INSERT with PK validation, compaction operator, uniqueness re-validation — see notes below |
| **Dataset Revision Pipeline** | XL | Four phases: output manifest, two-pass vocabulary collection, `EstimateCost` RPC, gRPC `CreateOutput` with progress streaming — see Dataset Revision Pipeline (V3) |
| **Datum File Compaction** | XL | Three phases: full compaction (tombstone + orphan removal), merge compaction (row group consolidation), automatic background compaction — see Datum File Compaction |

### Under Consideration

| Feature | Complexity | Justification |
|---------|:----------:|---------------|
| **Struct type** (`DataKind.Struct`) | L | User-defined composite values for typed nested output (ML annotation formats: bounding boxes, class labels, masks). Construction via `struct('field', value, ...)` function; field access via `field(struct_col, 'name')` — no parser changes, no `a.b` ambiguity. Payload = `DataValue[]` with a `StructSchema` reference (field names + field kinds). `ColumnInfo` gains `IReadOnlyList<ColumnInfo>? Fields`. Parquet writer maps to nested groups; JSON writer maps to objects. `OutputColumnSpecification` gains a `Struct` encoding for the Dataset Revision Pipeline. `OrderByOperator.CompareDataValues` needs struct comparison (lexicographic by field). Primary motivation: COCO/YOLO annotation output where per-image structs of arrays replace either denormalized columns (lose structure) or JSON (lose schema) |
| **SCAN expression** (ordered fold) | L | First-class stateful fold over ordered rows: `SCAN acc = acc + price INIT 0 OVER (PARTITION BY ... ORDER BY ...) AS running_total`. Covers sessionization, streaks, and sequential state machines — patterns where the current row's result depends on the *derived output* of the previous row, which window functions cannot express. Reuses existing OVER clause syntax. Scalar accumulator only (no structured state); requires injecting feedback state into `ExpressionEvaluator` context and a new `ScanOperator` that buffers per partition. Inherently sequential within a partition — precludes intra-partition parallelism |
| **Enum / Categorical type** (`DataKind.Categorical`) | L | `label_encode` and `hash_encode` cover most ML use cases today; a first-class type adds automatic encoding and domain validation but requires a `DataKind` extension — see Type System Extensions |
| **Index encoding strategies** (dictionary B+Tree, RLE sorted, Roaring bitmaps, FOR/bit-packing) | XL | Breaking `.datum-index` format changes with major payoff for high-cardinality string and low-cardinality numeric columns at scale; no V1 blocker — see Index Encoding Strategies |

---

### Notes

**Predicate-filtered row count propagation**: The greedy join reorderer currently uses raw `EstimatedRowCount` from the manifest. This refines it by multiplying each table's count by pushed-down WHERE predicate selectivity — equality predicates on high-NDV columns get `1/NDV`; range predicates use a fraction of the min/max domain; multi-predicate selectivities multiply (independence assumption). Captures most cost-based optimizer benefit without a full cardinality model. Motivating case: a 32M-row embedding table filtered to ~100 rows by a high-NDV predicate should be a build side, not the probe side — but the current heuristic sees 32M and gets it wrong.

**`DataKind.Decimal` type**: `System.Decimal` is exactly 16 bytes and fits inline in the existing `DataValue` struct (`_numericBits` + `_bits1`) with no GC cost or layout change. Natural Parquet mapping via the `DECIMAL` logical type (fixed-length byte array with precision/scale annotations). Primary use case: exact-arithmetic aggregation (SUM on financial columns where `Float64` precision loss is unacceptable) and ingesting Parquet sources that carry `DECIMAL` columns today. `BigInteger` is explicitly out of scope — variable-length, heap-allocated, GC pressure in tight loops.

**Primary key persistence**: PRIMARY KEY column metadata is currently session-only (`TableDescriptor`) and not persisted in the `.datum` file footer — acceptable for V1 temp tables, which are session-scoped. In a future durable ingestion model, clients send batches (shards) with externally-derived composite keys; the engine writes each batch, creates tombstone records for superseded rows, and later runs compaction. PK metadata in the footer enables compaction to validate uniqueness across shards without external coordination. Phases: (1) extend `DatumFileSchema`/footer with `PrimaryKeyColumns`; (2) read PK metadata on file open; (3) shard-aware INSERT validating PK across existing row groups; (4) compaction operator merging row groups, applying tombstones, re-validating uniqueness.

---

## Completed

- ~~**GROUP BY / Aggregation**: COUNT, SUM, AVG, MIN, MAX, GROUP BY, HAVING~~ ✅
- ~~**Spill-to-disk joins**: Grace hash join for datasets too large for memory~~ ✅
- ~~**Parameterized queries**: Named `$parameter` syntax with early binding~~ ✅
- ~~**Scalar subqueries**: Uncorrelated constant-fold at plan time; correlated via `ScalarSubqueryOperator` + `OuterRow` context threading~~ ✅
- ~~**Scalar subquery decorrelation**: Rewrite to `GROUP BY` + `LEFT JOIN`, reducing O(N×M) per-row execution to O(N+M)~~ ✅
- ~~**IN / NOT IN / EXISTS / NOT EXISTS subqueries**: `SemiJoinRewriter` → LEFT SEMI / LEFT ANTI-SEMI hash joins; NOT IN with three-valued NULL semantics~~ ✅
- ~~**UNION / INTERSECT / EXCEPT**: All six variants with SQL-standard precedence; spill-to-disk for UNION DISTINCT~~ ✅
- ~~**Window functions**: ROW_NUMBER, RANK, LAG, LEAD with OVER/PARTITION BY~~ ✅
- ~~**GROUP BY ALL**: Projection-derived grouping — infers group keys from non-aggregate columns in the SELECT list~~ ✅
- ~~**Dataset splitting**: `hash_split(key, seed)` for reproducible train/val/test splits~~ ✅
- ~~**Top-N bounded sort**: Bounded priority queue in `OrderByOperator` for ORDER BY + LIMIT~~ ✅
- ~~**WHERE index seek**: Point lookups via `ISeekableTableProvider`~~ ✅
- ~~**Range predicate index pruning**: `<`, `<=`, `>`, `>=`, `BETWEEN`, `IN` chunk pruning~~ ✅
- ~~**Seekable Parquet provider**: `ISeekableTableProvider` at row-group level~~ ✅
- ~~**Seekable HDF5 provider**: `ISeekableTableProvider` via PureHDF `HyperslabSelection`~~ ✅
- ~~**Index + manifest co-generation**: Single-pass `index-manifest` command~~ ✅
- ~~**Greedy join reordering**: Largest table on probe side; smaller tables as build sides~~ ✅
- ~~**Multi-level bloom pruning**: Build-side key propagation through nested join operators~~ ✅
- ~~**Hybrid Grace hash join**: Streaming probe for in-memory partitions; LIMIT short-circuits after N rows~~ ✅
- ~~**Default memory budgets**: CLI 2 GB / server 256 MB; activates `GraceHashJoinExecutor` automatically~~ ✅
- ~~**Statistics-based partition pruning**: Skip row groups whose min/max proves a predicate unsatisfiable~~ ✅
- ~~**Bloom filter acceleration**: Parquet bloom filters for equality predicate partition skipping~~ ✅
- ~~**Schema caching**: Skip re-inference on repeated queries~~ ✅
- ~~**Language server (WASM)**: SQL autocomplete, diagnostics, hover via Blazor WebAssembly~~ ✅
- ~~**Language server (SignalR)**: SignalR hub for server-side language intelligence~~ ✅
- ~~**Language server — multi-error diagnostics**: Error-recovering parser for multiple parse errors~~ ✅
- ~~**Language server — WASM size optimization**: Extract `DatumIngest.Parsing` to eliminate heavy transitive dependencies~~ ✅
- ~~**Language server — semantic diagnostics (unknown table/column warnings)**~~ ✅
- ~~**CASE / WHEN expressions**: Searched CASE and simple CASE~~ ✅
- ~~**NULLIF function**: `NULLIF(a, b)` returns NULL when a equals b~~ ✅
- ~~**Query metering system**: `QueryMeter` in `ExecutionContext`, `GetUsage` RPC~~ ✅
- ~~**Resolution-aware image costs**: Post-execution cost reporting from actual decoded resolution~~ ✅
- ~~**Time-of-day type** (`DataKind.Time`)~~ ✅
- ~~**Duration type** (`DataKind.Duration`)~~ ✅
- ~~**Schema Matching**: Star-schema hub detection from manifest statistics~~ ✅
- ~~**Virtual schemas**: `information_schema` (tables/columns/schemata) and `datum_catalog` (providers/functions/statistics) queryable via `schema.table` SQL syntax; language server integration (diagnostics, completions, hover)~~ ✅
- ~~**P1 — Merge Join**: Streaming two-pointer join for sorted equi-joins~~ ✅
- ~~**P2 — Streaming Aggregate + LIMIT Short-Circuit**: `GroupByOperator` dual-mode hash/streaming~~ ✅

---

## Engine Performance

**Status**: Phases 1–2 shipped (merge join, streaming aggregate). Remaining phases implement when measured scenarios demand them. Reference tag **Pn** / **Dn** when a new bottleneck matches.

### ~~P1. Merge Join~~ ✅

Streaming two-pointer join for equi-joins when both sides have sorted indexes. O(n+m) time, O(k) memory (k = max duplicates per key value). Activated by `SortedJoinBenefitsDownstream` only when a downstream operator (ORDER BY, GROUP BY) benefits from sorted output.

### ~~P2. Streaming Aggregate + LIMIT Short-Circuit~~ ✅

`GroupByOperator` dual-mode: hash (default) vs. streaming when input is pre-sorted by GROUP BY keys. Streaming emits groups one at a time → LIMIT cancels upstream after N groups. `GetOutputOrdering` walks the operator tree to propagate sort metadata; `OutputOrderingSatisfiesOrderBy` eliminates redundant ORDER BY.

### P3. Parallel Hash Join Probe + Parallel Hash Aggregate

Parallelize the two dominant CPU-bound operators. Shared read-only hash table with P concurrent probe workers; thread-local partial aggregation with merge. Target 4–8× speedup for large joins + aggregations. Uses `Channel<Row>` + `Task.Run` + `Task.WhenAll` (same pattern as `ZipTableProvider.FetchByKeysAsync`).

- `ExecutionContext.DegreeOfParallelism` property (default 1 = no parallelism)
- Parallel hash join: sequential build (small side, read-only after construction), P workers probe shared dictionary concurrently via bounded `Channel<Row>`, single output channel with backpressure
- `IAggregateAccumulator.Merge(IAggregateAccumulator other)` on all ~15 accumulator types
- Parallel hash aggregate: P thread-local dictionaries → global merge phase combining partial groups
- Activation threshold: `GetEstimatedRowCount > 100K` for the operator's input
- Streaming GROUP BY remains single-threaded (already O(1) memory, LIMIT short-circuits)

**Multi-query concurrency control** (server-side): Workers schedule onto the .NET ThreadPool, which is process-global. Under concurrent gRPC queries, total parallelism must be bounded to avoid oversubscription. Three options, in order of complexity:

1. **Global concurrency semaphore** — `SemaphoreSlim(ProcessorCount)` shared across all queries. Parallel operators acquire slots before spawning workers. Simple, effective, no per-query fairness.
2. **Adaptive degree of parallelism** — each query checks active worker count at planning time and reduces its own parallelism accordingly (e.g., 8 cores, 3 active queries → 2 workers each). Fairer distribution but more bookkeeping.
3. **Per-query TaskScheduler** — `ConcurrentExclusiveSchedulerPair` with max concurrency per query. Strongest isolation, heaviest implementation.

Start with option 1. CLI passes `null` (unlimited — single query, all cores available).

**Implement when**: CPU-bound join or aggregate is the measured bottleneck on multi-core hardware.

### P4. DataValue Struct + Batch Execution

Eliminate per-DataValue heap allocation for scalar types via `[StructLayout(LayoutKind.Explicit)]` union (32 bytes inline, no GC pressure for scalars). Then introduce columnar batch processing (`IBatchOperator`, `DataBatch`).

- `DataValue` class → `readonly struct` with explicit field layout
- `DataValue?` → `.IsNull` flag; hundreds of call sites to migrate
- `Row` holds `DataValue[]` inline (32 bytes each vs 8-byte pointers)
- Columnar batches (future sub-phase): `EvaluateBatch(Expression, DataBatch)`, Scan → Filter → Aggregate in batch mode

**Implement when**: GC pressure or per-row evaluation overhead is the measured bottleneck. Expected 5–10× throughput improvement.

### Deferred Optimizations (implement when a measured scenario demands it)

- **D1. Exchange operators** — HashRepartition, RoundRobin, Gather, OrderedGather for arbitrary plan parallelism. *Use case*: a query where the bottleneck is data movement between operators rather than within a single operator — e.g., parallel subqueries feeding into a UNION, or a pipeline where scan→filter→project all need to run on separate thread pools. Today's P3 parallelism is internal to join/aggregate; exchange operators generalize it to arbitrary plan shapes.

- **D2. RowBatch** — amortize per-row channel overhead by sending batches (64–256 rows). *Use case*: after implementing P3, profiling a parallel query shows >10% of wall time in `Channel<Row>.WriteAsync`/`ReadAsync` overhead rather than in the actual probe or accumulate work. Batching reduces channel operations by 64–256×.

- **D3. Parallel sort** — partition → P local sorts → k-way merge. *Use case*: `SELECT * FROM large_table ORDER BY column` with no LIMIT, where the table has millions of rows and no sorted index on that column. The single-threaded `OrderByOperator` sort dominates wall time. Look for ORDER BY appearing as the top cost in EXPLAIN on unindexed columns.

- **D4. Parallel DISTINCT** — hash-partition by distinct key → per-partition hash set → gather. *Use case*: `SELECT DISTINCT user_id FROM events` where `events` has hundreds of millions of rows and high cardinality. The single-threaded hash set construction in `DistinctOperator` is CPU-bound. Look for DISTINCT queries where the input table is large and the distinct column has high NDV.

- **D5. Parallel scan** — divide IndexChunks among P workers for parallel I/O. *Use case*: scanning a large Parquet dataset on NVMe/SSD where a single-threaded sequential read underutilizes the storage bandwidth. Look for queries where EXPLAIN shows the Scan operator dominates wall time and the storage device has unused IOPS capacity (common with NVMe drives that sustain 3–6 GB/s but a single thread reads at 500 MB/s).

- **D6. Eager aggregation** — push aggregate below join to pre-aggregate fact tables before joining. *Use case*: `SELECT d.name, COUNT(*) FROM facts JOIN dims d ON ... GROUP BY d.name` where `facts` has 32M+ rows but the GROUP BY key comes from the dimension table. Pre-aggregating facts by the join key before joining reduces the join input dramatically (e.g., Instacart: 32.4M → 13M rows). Look for queries where a large fact table is joined then aggregated, and the `GROUP BY` columns come from the smaller dimension side.

- **D7. Semi/Anti merge join** — LEFT SEMI and LEFT ANTI via merge algorithm on sorted inputs. *Use case*: `SELECT * FROM orders WHERE EXISTS (SELECT 1 FROM returns WHERE returns.order_id = orders.order_id)` on large tables with sorted indexes on the join key. Currently uses a hash semi-join; merge avoids building the hash table entirely. Look for EXISTS/NOT EXISTS/IN subqueries on sorted columns where the hash table build is the bottleneck.

- **D8. Multi-key merge join** — composite equi-join keys with tuple comparison. *Use case*: `SELECT * FROM a JOIN b ON a.year = b.year AND a.month = b.month ORDER BY a.year, a.month` where both tables have composite sorted indexes on `(year, month)`. Currently falls back to hash join because merge join only supports single-key equi-joins. Look for multi-column equi-joins where both sides have composite indexes and the query benefits from sorted output.

- **D9. Column-major vectorized execution** — replace the current row-major `Row[]` batch model with a columnar `DataValue[][]` representation where the first index is the column and the second is the row offset within the batch. *Use case*: aggregation-heavy and expression-heavy queries where tight loops over a single column (e.g. `SUM`, `AVG`, predicate evaluation) benefit from sequential memory access and SIMD auto-vectorisation. Requires rewriting the expression evaluator to operate on column vectors, converting all operators to produce and consume columnar batches, and adding a row-to-columnar adapter at provider boundaries. The current row-major RowBatch already amortises async state-machine overhead; this optimisation targets the *inner-loop* compute cost.

### P5. Memory-Mapped Sorted Indexes

Replace the v3 sorted index format (variable-length `WriteDataValue` entries, Zstd-compressed per column, full materialization into `ValueIndexEntry[]` on read) with a fixed-width binary layout that can be memory-mapped for zero-copy binary search.

**On-disk format (v4)**:

- **Column directory**: Per column: column name, `DataKind`, entry count, key width (bytes), absolute file offsets for keys/locators/string table.
- **Keys array**: `entryCount × keyWidth` bytes. Stored in sort-preserving binary encoding (big-endian + sign-flip for signed integers; IEEE-to-sortable transform for floats) so `SequenceCompareTo` on raw bytes gives correct ordering.
- **Locators array**: `entryCount × 12` bytes. Fixed-width `[int32 ChunkIndex | int64 RowOffsetInChunk]`.
- **String table** (string columns only): Packed UTF-8 bytes. Keys store fixed-width `(int32 offset, int32 length)` pairs referencing into this region.

**Storage strategy**: The canonical local `.datum-index` file stores the v4 fixed-width layout directly — no compression layer. The fixed-width format *is* the local storage format, always mmap-ready. On-disk size is larger (~8× for typical columns vs. Zstd-compressed v3), but acceptable for a local ML ETL tool where datasets already occupy hundreds of megabytes. Compression is a **blob storage / transport concern**: when uploading to cloud blob storage (Azure Blob, S3), the file is Zstd-compressed before upload; after download, the compressed blob is decompressed back to the fixed-width layout and then mmap'd. This keeps the format simple (one canonical layout, no dual-state files) while still getting ~8× compression savings on the wire and in blob storage costs.

**Key encoding per kind**: UInt8 = 1 byte raw. Int8 = 1 byte XOR sign bit. Int16/UInt16 = 2 bytes big-endian (± sign flip). Int32/UInt32 = 4 bytes. Int64/UInt64 = 8 bytes. Float32 = 4 bytes IEEE→sortable. Float64 = 8 bytes IEEE→sortable. Date = 4 bytes (DayNumber as Int32). DateTime = 8 bytes (UTC ticks as Int64). String = 8 bytes (offset + length into string table).

**New types**: `SortedIndexKeyEncoder` (encode/decode per kind), `MappedSortedIndex` (implements `IColumnIndex` over `MemoryMappedViewAccessor`), `MappedSortedIndexSet` (replaces `SortedValueIndexSet` for mmap path). All consumers operate through `IColumnIndex` — no changes to `ScanOperator`, `IndexScanOperator`, `JoinOperator`, or `QueryPlanner`.

**Multi-tenancy benefit**: OS-level page sharing — multiple server connections or gRPC compute clients reading the same mmap'd index share physical memory pages. Deterministic memory: the process doesn't "own" the pages; the OS pages in/out on demand. Ten concurrent sessions don't multiply index memory 10×.

**Deletions**: v2 uncompressed read/write, v3 compressed read/write, `SerializeEntriesToBuffer`, `MemoryStream`-based sorted index serialization. No backward compatibility — existing `.datum-index` files must be regenerated.

**Implement when**: This is the prerequisite for multi-tenant index sharing and temp-table isolation.

### P6. ReferenceStore Session Isolation

**Status**: ✅ Implemented via `AsyncLocal<ReferenceStore?>` per-query scoping.

`ReferenceStore` uses a two-tier design: a process-global `_globalFallback` singleton for code paths that run outside an explicit query scope (tests, one-shot CLI invocations where the process exits after each command), and an `AsyncLocal<ReferenceStore?> _current` that is set to a fresh isolated store by `BeginQueryScope()` at the start of each query. `EndQueryScope()` resets and clears the store when the query finishes, releasing all referenced objects (strings, byte arrays, vectors, images) accumulated during that query's execution.

No `DataValue` struct layout changes were required — all cross-context `DataValue` usage flows through the global fallback. `BeginQueryScope()` must be called from the parent async context so the value propagates to all child continuations (child contexts inherit but cannot write back to the parent).

`BeginQueryScope`/`EndQueryScope` are wired into:
- `ComputeService.Query()` (gRPC)
- `RunQueryAsync`, `RunExploreAsync`, `RunStatsAsync`, `RunExplainAsync`, `RunManifestAsync` (CLI)

`DatumIngest.csproj` exposes `ReferenceStore` internals to `DatumIngest.Compute` via `InternalsVisibleTo`.

---

## Index Encoding Strategies (Deferred)

**Status**: Under consideration. The current index format writes raw `DataValue` keys in B+Tree leaf pages and sorted indexes. For many column profiles, specialised encodings would reduce page counts, improve compression ratios, and enable queryable-while-compressed bitmap operations. Vocabulary files collected during ingestion provide the foundation for dictionary-coded indexes.

### Dictionary-coded B+Tree indexes

Replace materialised `DataValue` keys in B+Tree leaf pages with ordinal dictionary codes. Each column section's header carries the dictionary (sorted distinct values with ordinal assignments); leaf entries store `(code: uint16/uint32, chunkIndex, rowOffset)` instead of `(key: DataValue, chunkIndex, rowOffset)`.

**Prerequisites**: The vocabulary file infrastructure already collects distinct values during ingestion. Two requirements remain: (1) ordinal assignment must preserve the sort order of the original values so B+Tree range scans work on codes directly, and (2) the bulk loader needs a two-pass or deferred-code-assignment strategy since the current streaming build consumes entries in sorted order without a pre-built dictionary.

**Acquisition strategy** (mirrors vocabulary collection):
1. **Manifest/vocab covers full domain** (distinctCount ≤ cardinality threshold): Dictionary known before index build. Assign ordinal codes, emit coded entries in a single pass.
2. **Manifest/vocab insufficient**: Two-pass index build. Pass 1 collects distinct keys from the sorted merge stream. Pass 2 re-reads spill files and emits coded entries. The merge is already spill-based, so the second pass is I/O-bound, not memory-bound.
3. **Cardinality exceeds threshold**: Fall back to raw `DataValue` keys (current behaviour). Dictionary encoding is counterproductive when the dictionary itself is large.

**Candidate columns**: String and JsonValue columns with moderate cardinality (1K–100K distinct values). For fixed-width numeric types (Int32, Float64, etc.), raw keys are already 4–8 bytes — a dictionary code saves nothing. The column statistics already available in `ChunkColumnStatistics.EstimatedCardinality` (HLL) gate the decision.

**Expected impact**: For a String column with 50K distinct values averaging 20 bytes each, dictionary-coded entries shrink from ~32 bytes/entry to ~6 bytes/entry (uint16 code + chunkIndex + rowOffset), fitting ~5× more entries per leaf page → fewer pages → shallower tree → fewer I/O operations during seeks.

### Run-length encoding for sorted indexes

For low-to-medium cardinality columns in sorted indexes (not B+Tree), consecutive entries with the same key can be run-length encoded: `(key, runLength, [(chunkIndex, rowOffset)...])` instead of repeating the key per entry. The sorted merge already produces entries in key order, so runs are naturally contiguous.

**Candidate columns**: Columns where `distinctCount / rowCount < 0.01` (fewer than 1% unique values). The "reordered" Boolean column in Instacart (2 distinct values, 32M rows) is the extreme case — 32M key serialisations reduced to 2.

**Trade-off**: RLE sorted indexes lose O(1) random access into the entry stream. Binary search for a key requires scanning run headers. For point lookups, the B+Tree path is already preferred (all Instacart columns exceed `BPlusTreeAutoThreshold`). RLE is most useful for range scans and full-column iteration where the sequential access pattern naturally follows runs.

**Relationship to bitmap indexes**: For very-low-cardinality columns (≤ ~1000 distinct values), bitmap indexes already serve the same purpose more efficiently — they're column-major and support set operations. RLE sorted indexes occupy the middle ground: too many distinct values for bitmap indexes, too much key repetition for raw sorted indexes. The decision matrix in `CategorizeBPlusTreeColumns` would grow a third branch.

### Roaring bitmap indexes

Replace the current Zstd-compressed opaque bitmaps in `BitmapColumnIndex` with Roaring bitmaps — a hybrid format using three container types per 64K-row range: array (sparse), bitset (dense), and RLE (runs). Roaring bitmaps are queryable while compressed: AND/OR/XOR/NOT/ANDNOT operations execute directly on the compressed representation without decompression.

**Current limitation**: Bitmap index probes in `BitmapScanOperator` decompress the full Zstd-compressed bitset per chunk before testing membership. Multi-predicate queries (e.g., `WHERE color = 'red' AND size = 'large'`) decompress both columns' bitmaps independently, then intersect the decompressed arrays. This is O(n) in chunk size regardless of selectivity.

**With Roaring**: Multi-predicate intersection operates on compressed containers directly — sparse × sparse = small array intersection; dense × dense = word-level AND. Selectivity-proportional cost: a highly selective predicate produces a small Roaring bitmap that intersects cheaply against any other predicate's bitmap. No decompression step.

**Additional benefits**:
- O(1) `rank` and `select` operations for offset-based access (useful for late materialisation)
- Serialisation format is standardised ([Roaring specification](https://roaringbitmap.org/)) and interoperable with other systems
- Memory-mapped access: Roaring's frozen serialisation format supports zero-copy reads from mmap regions, aligning with the planned mmap-based index access for multi-tenancy

**Implementation path**: Replace `BitmapChunkAccumulator`'s `BitArray` + Zstd compression with Roaring bitmap construction. The accumulator already processes entries in row-offset order (sequential adds), which is Roaring's optimal insertion pattern. The `BitmapColumnIndex` reader switches from Zstd-decompress-then-scan to direct Roaring container operations. Existing `BitmapIndexSet` serialisation format changes (breaking change to `.datum-index` bitmap section).

**Library option**: [RoaringBitmap](https://www.nuget.org/packages/RoaringBitmap) on NuGet, or a minimal hand-rolled implementation covering only the three container types + AND/OR to avoid the dependency.

### Adaptive encoding selection

Unify the per-column encoding decision into a single matrix driven by statistics already collected during chunk accumulation (`ChunkAccumulatorProxy`: HLL cardinality, min/max, null count, row count):

| Cardinality / Row Count | Encoding | Index strategy |
|---|---|---|
| ≤ 2 (Boolean-like) | Bitmap | Bitmap index; skip B+Tree and sorted index |
| 3–1,000 | Bitmap + optional dictionary | Bitmap index (Roaring when available) |
| 1K–100K, String/JSON | Dictionary-coded B+Tree | Ordinal dictionary from vocab file, uint16/uint32 codes |
| 100K+, or fixed-width numeric | Raw B+Tree | Current approach (`DataValue` keys) |
| Any column, sorted index path | RLE if distinctCount/rowCount < 0.01 | Run-length encoded sorted index |

The decision point is `IndexWriter.CategorizeBPlusTreeColumns` (currently a binary B+Tree-vs-sorted split). This becomes a multi-way classifier returning an `IndexEncodingStrategy` enum per column. The vocabulary file collected during ingestion supplies the dictionary for dictionary-coded columns; if no vocabulary is available, the index builder falls back to raw encoding.

### Frame-of-reference (FOR) and bit-packing for integer keys

For integer columns where the key range is narrow relative to the full type range (e.g., `product_id` values between 1 and 50K stored as Int32), frame-of-reference encoding subtracts the minimum value in each leaf page and bit-packs the residuals at the minimum necessary bit width. A page with keys [10000..10255] stores 8-bit residuals instead of 32-bit values — 4× compression before Zstd.

**Synergy with existing compression**: FOR + bit-packing reduces the uncompressed payload fed to Zstd, improving both the compression ratio and the compressed size. More entries fit per leaf page → fewer pages → shallower tree.

**Candidate columns**: Integer types (Int8–Int64, UInt8–UInt64) where `max - min` within a leaf page fits in fewer bits than the native type width. The bulk loader already knows the key range per page (it holds the entries before encoding) — computing `maxKey - minKey` and choosing the bit width is a local decision with no global state.

---

## Type System Extensions (Deferred)

**Status**: Under consideration. These types have clear use cases in OLAP analytics and ML pipelines but are not blocking V1 workflows. The current type system (Float32, UInt8, Vector, Matrix, Tensor, UInt8Array, Image, String, Date, DateTime, Time, Duration, JsonValue, UUID, Boolean) covers the primary ML and analytics needs.

### ~~Time-of-day type (`DataKind.Time`)~~ ✅

~~A standalone time without a date component. Common in OLAP engines (ClickHouse `Time`, DuckDB `TIME`, PostgreSQL `time`). Currently, time components are extractable via `hour()`, `minute()`, `second()` but there is no way to represent a first-class Time column. Would require a new `DataValue` factory (`FromTime(TimeOnly)`), CAST paths (String↔Time, DateTime→Time), and output format mappings.~~

### ~~Interval / Duration type (`DataKind.Duration`)~~ ✅

~~Represents elapsed time spans (days, hours, minutes, seconds). Would allow `date_diff` to return a typed duration instead of a Float32. Useful for temporal aggregation and arithmetic (`date_add` could accept Duration directly). The current Float32 representation for durations works for ML consumption (models see numbers) but loses semantic information. Parquet has no native interval type; HDF5 would need a custom convention.~~

### Enum / Categorical type (`DataKind.Categorical`)

ML-relevant for one-hot and label encoding. Could be represented as String with a fixed domain constraint (known set of valid values). Natural fit with the planned data validation feature (CHECK constraints / VALIDATE clause). Would enable: automatic one-hot encoding, label encoding with stable integer mapping, domain validation on ingest. DuckDB and Polars both support this pattern.

**V1 status**: Explicit-domain encoding functions (`one_hot`, `label_encode`, variants) and stateless feature hashing (`hash_encode`) shipped without a dedicated `DataKind.Categorical`. These cover low-to-high cardinality encoding via SQL functions operating on String columns. A first-class Categorical type remains under consideration for automatic encoding and validation scenarios.

---

## Dataset Revision Pipeline (V3)

**Status**: Design finalized. The revision pipeline transforms raw data into ML-ready shards with vocabulary files, output provenance, and an output manifest. Usable standalone via CLI or via the `CreateOutput` gRPC RPC.

### Architecture

DatumIngest receives a SQL projection plus **output column specifications** — column+encoding pairs that tell the output writer how to handle each column. This is not a SQL-level concern (functions stay stateless); encoding is an output-writer directive.

```
OutputColumnSpecification[] = [
    ("image",  ColumnEncoding.Image),        // raw image bytes → image shard column
    ("label",  ColumnEncoding.Categorical),  // string/uint8 → integer index with vocabulary file
    ("score",  ColumnEncoding.Passthrough),  // default — write as-is
]
```

**Two-pass execution** for categorical columns:

1. **Pass 1 — Vocabulary collection**: Execute the full query but only evaluate categorical column expression subtrees. Non-categorical columns (images, vectors, scalars) are skipped entirely — no decoding, no I/O. Accumulate sorted distinct values per categorical column. Enforce a cardinality cap (default 100K); abort with diagnostic if exceeded (Heliosoph should default to hash encoding for high-cardinality columns).
2. **Pass 2 — Output**: Execute the full query with bound vocabularies. Write shards, emit `.datum-vocabulary` files, collect statistics, produce output manifest and output provenance.

If no categorical columns are specified, pass 1 is skipped and output is single-pass.

### Vocabulary format (`.datum-vocabulary`)

One label per line, deterministically sorted (lexicographic for strings, numeric for integers). Line number = index. Reproducible regardless of data order, shard boundaries, or parallelism.

```
0
1
2
3
4
5
6
7
8
9
```

Naming convention: `{column_name}.datum-vocabulary` alongside output shards.

### Vocabulary acquisition strategy

Three paths, chosen automatically:

1. **Manifest TopK covers full domain** (distinctCount <= TopK): Vocabulary already known from input manifest. Sort and assign indices. No extra pass. Zero additional cost.
2. **Manifest TopK insufficient** (distinctCount > TopK, <= cardinality cap): Two-pass. Pass 1 evaluates only categorical column expressions, accumulates sorted distinct values, skips all non-categorical work (image decode, vector ops, etc.). Cost is dominated by I/O, not compute — typically <2% overhead vs. the output pass.
3. **Cardinality exceeds cap**: Reject categorical encoding for this column. Heliosoph surfaces the diagnostic and suggests hash encoding (irreversible but bounded). User can override with explicit confirmation and higher cap.

### Plan cost estimation

Mandatory before execution. The estimator walks each output column's expression AST, sums `IScalarFunction.QueryUnitCost` (and `ICostAwareFunction` supplemental costs via manifest-derived resolution estimates) per function node, and multiplies by row count. Produces a per-pass QU breakdown:

```
Pass 1 (vocabulary):  categorical columns only     →   180K QU
Pass 2 (output):      all columns                  → 9,065K QU
────────────────────────────────────────────────────
Estimated total:                                    ~ 9,245K QU
```

Image costs estimated from input manifest's mean resolution statistics. If no manifest exists for a source column, the estimator reports the column as "uncosted" and Heliosoph can request manifest generation first or proceed with a conservative default.

**Heliosoph integration**: Receives estimate via `EstimateCost` RPC → checks user balance → auto-approves cheap plans → prompts for expensive plans. During execution, `QueryMeter` budget enforcement acts as the governor: if actual cost exceeds the approved budget (user tried to game the estimate, or the estimate was wrong), execution terminates with `ResourceExhausted`.

### Output artifacts

```
revision_name/
  shard_000000.parquet
  shard_000001.parquet
  label.datum-vocabulary          # one per categorical column
  species.datum-vocabulary
  revision_name.datum-manifest    # output statistics (same format as input manifests)
  revision_name.datum-provenance  # encoding provenance, source query, cost
```

### Output manifest (`.datum-manifest`)

Same `QueryResultsManifest` format as input manifests — pure statistics, no provenance. This means an output manifest is immediately usable as an input manifest for chained pipelines (`raw → transform → manifest → downstream transform`). Contains per-column statistics (min, max, distinctCount, topK, nullCount, etc.) computed from the output data.

### Output provenance (`.datum-provenance`)

Separate file recording *how* the output was produced — source query, encoding decisions, vocabulary references, and cost. This is metadata about the transformation, not statistics about the result. Provenance and manifest evolve independently.

```json
{
  "version": 1,
  "rowCount": 60000,
  "shardCount": 2,
  "costEstimate": 9245000,
  "costActual": 9218400,
  "source": {
    "query": "SELECT image, label FROM 'mnist_train.hdf5'",
    "inputTables": [{ "alias": "mnist_train.hdf5", "rowCount": 60000 }]
  },
  "columns": {
    "image": {
      "storage": "image",
      "semanticType": "image",
      "encoding": {
        "type": "passthrough",
        "sourceColumn": "image",
        "sourceStorage": "image",
        "resolution": [28, 28, 1]
      }
    },
    "label": {
      "storage": "uint8",
      "semanticType": "categorical",
      "encoding": {
        "type": "vocabulary",
        "sourceColumn": "label",
        "sourceStorage": "uint8",
        "vocabularyFile": "label.datum-vocabulary",
        "vocabularySize": 10,
        "sortOrder": "numeric",
        "coverage": 1.0,
        "hasUnknownBucket": false,
        "vocabularySource": "manifest-topk"
      }
    }
  }
}
```

`vocabularySource` records provenance: `"manifest-topk"` (no extra pass), `"vocabulary-pass"` (pass 1 collected it), or `"explicit"` (user-provided vocabulary file).

### Client integration

1. **Client explores dataset** (live gRPC session, queries via `Query` RPC).
2. **Client configures output**: Submits SQL + `OutputColumnSpecification[]` via `CreateOutput` RPC. Input manifest's `EstimatedDistinctCount` informs encoding decisions — high cardinality columns should prefer hash encoding.
3. **DatumIngest estimates cost**: Walks expression ASTs, produces QU estimate, returns to client.
4. **Client approves**: Auto-approve cheap plans, prompt for expensive ones, check balance.
5. **DatumIngest executes**: Two-pass (if categorical) or single-pass, with `QueryMeter` budget governor. Streams progress updates.
6. **Output**: Shards + vocabulary files + output manifest + output provenance.
7. **Client consumes provenance**: Reads `.datum-provenance` for encoding metadata (semanticType, vocabulary references, encoding decisions). Reads `.datum-manifest` for statistics.

### Implementation phases

**Phase 1 — Output manifest generation** (foundation)
- `OutputColumnSpecification` type with `ColumnEncoding` enum (Passthrough, Categorical, Image, Hash)
- Wire `StatisticsCollector` into `IOutputWriter` pipeline to collect statistics during output
- `OutputProvenanceBuilder` — builds `.datum-provenance` with encoding metadata per column
- Output manifest + provenance serialization (AOT-compatible source-generated JSON)
- CLI: `--manifest` flag on `query` command to emit output manifest and provenance alongside data

**Phase 2 — Vocabulary collection**
- `VocabularyCollector` — sorted set accumulator with cardinality cap, implements `IStatisticAccumulator`
- `VocabularyWriter` — writes `.datum-vocabulary` files (one label per line, deterministic sort)
- Two-pass execution orchestration in output writer: detect categorical columns → pass 1 (vocab-only, skip non-categorical expression evaluation) → bind vocabularies → pass 2 (full output)
- String column re-encoding: dictionary build during vocab pass, integer substitution during output pass
- Integer column passthrough: vocabulary file emitted as documentation, data written as-is

**Phase 3 — Plan cost estimation**
- `PlanCostEstimator` — walks expression AST per output column, sums function QU costs, multiplies by manifest row count. Uses `ICostAwareFunction` with manifest-derived resolution for image columns.
- `EstimateCost` gRPC RPC — accepts SQL + output column specs, returns per-pass QU breakdown without executing
- CLI: `--estimate` flag to print cost estimate before execution

**Phase 4 — gRPC output pipeline**
- `CreateOutput` RPC — accepts SQL, output column specs, output format, shard strategy
- Progress streaming — row count, shard count, current pass (vocabulary/output), QU consumed
- Governor integration — `QueryMeter` budget covers both passes combined; abort with `ResourceExhausted` if exceeded

---

## Schema Matching ✅

**Status**: Shipped. Lightweight schema matching discovers star-schema hubs across tables using only manifest statistics.

### Implemented

- **Column matching** — Levenshtein name similarity with suffix bonuses, type compatibility scoring
- **Evidence scoring** — Six signals (name, type, TopK Jaccard, cardinality ratio, range overlap, unique key) combined with configurable weights into composite confidence
- **Join classification** — Automatic 1:1, 1:N, N:1, N:M classification from NDV/RowCount ratios
- **Star schema detection** — Identifies hub tables with ≥2 spoke relationships from OneToMany/ManyToOne candidates
- **Tests** — Column matching, evidence scoring, and star schema detector tests

---

## Datum File Compaction

**Status**: Design phase. The `.datum` format now supports tombstone-based deletion (`HasTombstones` flag, per-row-group tombstone bitmaps). Tombstones mark rows as logically deleted without rewriting column data, creating dead space. Compaction reclaims that space.

### Problem

After DELETE operations, `.datum` files contain:
- **Tombstoned rows**: Column pages still hold data for deleted rows; tombstone bitmaps in the footer mark them as invisible. Readers skip these rows at query time, but the bytes remain on disk.
- **Replaced column pages**: `ReplaceColumns` (used by UPDATE via ALTER TABLE) appends new column pages and rewrites the footer to point at them. The old pages become orphaned — no footer entry references them, but the file is never truncated to remove them.

Over time, repeated DELETE + INSERT cycles and UPDATE operations cause `.datum` files to accumulate dead space proportional to churn.

### Compaction strategy

**Full compaction** rewrites a `.datum` file by:
1. Reading the footer to discover active row groups and their tombstone bitmaps.
2. For each row group with `ActiveRowCount > 0`: reading all column pages, filtering out tombstoned rows, re-encoding the surviving rows into new column pages.
3. Skipping row groups where `ActiveRowCount == 0` entirely (no I/O for fully-deleted groups).
4. Writing a new `.datum` file with fresh row groups (no tombstones, no orphaned pages), then atomically replacing the old file.

**Merge compaction** (future enhancement) combines multiple small row groups into fewer large ones, improving read performance by reducing row-group overhead and enabling better compression.

### Triggering heuristics

- **Dead-space ratio**: `1 - (sum(ActiveRowCount) / sum(RowCount))` — compact when this exceeds a threshold (e.g., 50%).
- **Orphan page ratio**: `(fileSize - liveDataSize) / fileSize` — captures space wasted by replaced column pages.
- **Manual**: CLI `COMPACT TABLE name` command and gRPC `Compact` RPC for explicit compaction.
- **Automatic**: Background compaction in server mode when dead-space exceeds threshold and no active readers hold the file.

### Implementation phases

**Phase 1 — Full compaction command**
- `DatumFileEditor.Compact(Stream source, Stream destination)` — reads source, writes compacted destination
- SQL: `COMPACT TABLE name` parsed and executed in `StatementExecutor`
- CLI: available as a SQL statement in interactive and batch modes

**Phase 2 — Merge compaction**
- Combine undersized row groups (below a configurable minimum, e.g., 1024 rows) into consolidated groups
- Rebalance row group sizes toward the target row group size for optimal read performance

**Phase 3 — Automatic compaction**
- Server-mode background task that monitors `.datum` files for compaction eligibility
- Concurrent reader safety: copy-on-write (write to temp file, atomic rename) ensures active readers see a consistent snapshot
- Compaction statistics surfaced via `GetUsage` RPC
