# `.datum` File Format

[← Back to README](../README.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Providers](providers.md)

The `.datum` format is a binary columnar store designed for high-throughput ML/ETL workloads. It's **uncompressed and mmap-friendly**: data lives in fixed-stride 1024-row pages with three compact encoders, hierarchical zone maps for predicate pruning, and a companion sidecar heap for non-inline payloads. The trade vs a heavily-compressed alternative is ~2–4× larger files; the win is decompress-free reads, simpler decode logic, and bounded peak memory during both write and read.

The current on-disk format version is **`4`**. v4 introduces a footer prologue (commit lineage, file table, chapter tombstone offsets), a `fileId` field on every page descriptor, and a `Tombstoned` column flag. These hooks back: crash-safe append (tail-flip-as-commit + torn-tail recovery), soft-drop column, soft-delete rows (chapter-level tombstone bitmaps with copy-on-write per commit), add column (all-null backfill), and external pages in companion `.datum-pack` files. Catalog-level `ALTER TABLE` / `INSERT` / `DELETE` route to these primitives through `TableCatalog`.

Three optional companion sidecars extend the base format:

- A companion [`.datum-index`](indexes.md) sidecar carries bloom filters, B+Tree indexes, bitmap indexes, and chunk-level statistics for query acceleration. (Older files may also contain sorted value indexes; readers still consume them, but new builds emit B+Tree only.)
- A companion [`.datum-blob`](#optional-sidecar-datum-blob) sidecar carries non-inline payloads (long strings, byte arrays, images, vectors, structs) addressed by 64-bit offsets so the data file itself stays compact. The sidecar is created lazily — only `.datum` files that actually carry non-inline payload produce one.
- A companion [`.datum-pkindex`](#optional-sidecar-datum-pkindex) sidecar holds a mutable B+Tree backing the table's `PRIMARY KEY` constraint. Created at `CREATE TABLE` time when a single-column PK is declared; maintained on every `INSERT`. Distinct from `.datum-index`: the PK index is updated incrementally (no rebuild on mutation), uses a dual-slot crash-safe header, and supports point lookup only.

## Design goals

- **Mmap zero-copy reads.** No decompress allocation, no decode buffer, no per-row indirection. Reading column N row M is `pages[N][M / 1024].offset + (M % 1024) × stride` for fixed-width pages.
- **Three encoders, period.** `FixedWidth` for fixed-stride scalars, `BitPackedBoolean` for booleans, `VariableSlot` for everything else. Each one's logic fits on a screen; the encoder is picked by `DataKind` alone at column-creation time.
- **Hierarchical zone-map pruning.** Page → chapter → volume tiers in the footer let the planner skip 1 K, 64 K, or 1 M-row blocks without touching data pages.
- **Page-aligned with execution batch.** 1024 rows per page = one `RowBatch` = one execution-engine working unit. No aggregation or splitting on read.
- **Streaming writes.** Pages flush directly to disk as they fill. Mid-ingest cancellation preserves the partial bytes; finalize patches the header and writes the footer.
- **Per-cell inline-vs-sidecar.** The `VariableSlot` encoder decides per-row whether a value's payload fits in 16 bytes (inline) or spills to the sidecar (pointer). Mixed-length text columns get the best of both — short rows inline, long rows sidecar.

## Physical layout

```
.datum file:
┌──────────────────────────────────────┐  Offset 0
│  File Header (32 bytes)              │
├──────────────────────────────────────┤  Offset 32
│  Column pages, in flush order        │
│    page 0 col 0   (1024 rows)        │
│    page 0 col 1                      │
│    ...                               │
│    page 0 col N-1                    │
│    page 1 col 0                      │
│    page 1 col 1                      │
│    ...                               │
├──────────────────────────────────────┤  footerOffset
│  Footer:                             │
│    For each column:                  │
│      Descriptor                      │
│      Page directory                  │
│      Chapter zone maps               │
│      (optional) Volume zone maps     │
├──────────────────────────────────────┤
│  Tail (8 bytes)                      │
└──────────────────────────────────────┘  EOF
```

**Layout note:** the format spec (`project_datum_format_v2.md`) calls for column-major page layout (all of column 0's pages, then column 1's, …). The current writer streams pages in flush order — which interleaves columns at the page-batch boundary — to enable incremental visible file growth and partial-output preservation on cancel. Each `PageDescriptorV2` records its absolute file offset, so the on-disk order is invisible to the reader; only sequential per-column scans lose some locality. A future optimization could re-introduce column-major layout via per-column temp files when measurements justify the I/O write amplification.

## File header

32 bytes, little-endian. Written on initialization with placeholder zeros for `TotalRowCount` and `FooterOffset`; both are patched on finalize.

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | Magic | bytes | `DTMF` (ASCII) |
| 4 | 2 | FormatVersion | uint16 | `4` |
| 6 | 2 | Flags | uint16 | `DatumFileFlagsV2` bitmask (see below) |
| 8 | 4 | ColumnCount | int32 | Informational in v4 — the footer prologue's `ColumnCount` is authoritative |
| 12 | 4 | PageSize | int32 | Rows per page (default 1024) |
| 16 | 8 | TotalRowCount | int64 | Patched at finalize |
| 24 | 8 | FooterOffset | int64 | Absolute byte offset of the footer body, patched at finalize |

### File flags (`DatumFileFlagsV2`)

| Flag | Value | Meaning |
|------|-------|---------|
| `None` | 0 | No special flags |
| `HasSidecarReferences` | 0x01 | At least one row spilled to the companion `.datum-blob`; the sidecar must be present at read time |
| `HasVolumeZoneMaps` | 0x02 | Volume-level zone maps were emitted (file row count exceeded the 1 M-row threshold) |
| `HasExternalPages` | 0x04 | At least one page descriptor has a non-zero `FileId` — pages live in external `.datum-pack` files referenced from the footer prologue's file table. Set by compaction-style code paths that move pages out of the primary file. |
| `HasTombstones` | 0x08 | At least one chapter has a populated tombstone bitmap (some rows soft-deleted). Set when `DELETE` has soft-deleted at least one row. |

`HasSidecarReferences` is only set when the sidecar's blob sink actually received an `Append` — files whose variable-slot columns happened to all stay inline leave the flag clear so the reader doesn't try to open a non-existent companion file. Readers ignore unknown flag bits, so future bit allocations are forward-compatible.

## Page layouts

Every page is uncompressed. The encoder kind is recorded once per column in the footer schema block; readers dispatch on `EncoderKind` to pick the decoder.

### `FixedWidth` (Int8/16/32/64, UInt8/16/32/64, Float32/64, Date, Time, Duration, DateTime, Uuid)

```
[null bitmap : ⌈rows / 8⌉ bytes]   (omitted when column non-nullable)
[payload     : rows × stride bytes]   null cells store zero — bitmap is authoritative
```

Stride is determined by `DataKind`:

| Stride | Kinds |
|--------|-------|
| 1 | Int8, UInt8 |
| 2 | Int16, UInt16 |
| 4 | Int32, UInt32, Float32, Date |
| 8 | Int64, UInt64, Float64, Time, Duration |
| 10 | DateTime (int64 ticks + int16 offset minutes, packed) |
| 16 | Uuid |

### `BitPackedBoolean` (Boolean only)

```
[null bitmap   : ⌈rows / 8⌉ bytes]   (omitted when column non-nullable)
[value bitmap  : ⌈rows / 8⌉ bytes]   bit i = the row's boolean value (undefined when null)
```

A 1024-row nullable boolean page is 256 bytes (vs 1024 bytes for raw byte-per-row). The 8× density reduction is the reason booleans get their own encoder rather than a `FixedWidth` special case.

### `VariableSlot` (String, JsonValue, Array, Image, Vector, Matrix, Tensor, Struct, typed arrays)

```
[null bitmap         : ⌈rows / 8⌉ bytes]   (omitted when column non-nullable)
[inline bitmap       : ⌈rows / 8⌉ bytes]   bit i = 1 means row i's slot is inline; 0 = sidecar pointer
[inline-length array : rows × 1 byte ]    per-row inline-payload length 0..16; meaningful only when inline bit is set
[slots               : rows × 16 bytes]
```

**Inline slot** (when inline bit is set): the 16 bytes are the payload itself, sliced to the per-row inline length.

- For `String` / `JsonValue`: the active bytes are UTF-8.
- For typed inline arrays (`UInt8`/`Int32`/`Float32`/… with `IsArray` + `InlineArray` flags): the active bytes are the packed elements.
- For other variable kinds, the value either spills to the sidecar (most common) or fits the inline tier the same way DataValue does.

**The inline-length array is a deviation from the spec.** The format spec called for the 16 inline bytes to be byte-for-byte equal to `DataValue._p0`–`_p3`. But `DataValue._charCount` (which holds the inline byte length / element count) lives in the 4-byte header *outside* the 16-byte payload region — so a strict byte-for-byte copy loses length info for variable-length kinds. The 1 KiB/page array (1 byte × 1024 rows) is negligible overhead; the reader uses it to slice the slot back to the active payload length when reconstructing the DataValue.

**Sidecar-pointer slot** (when inline bit is clear):

| Bytes | Field | Notes |
|-------|-------|-------|
| 0–7 | Offset | int64 absolute byte offset into `.datum-blob` (includes the sidecar's 32-byte header) |
| 8–12 | Length | 5-byte (40-bit) payload length, max ~1 TiB |
| 13–14 | Reserved | Zero today; reserved for future length-field expansion past 1 TiB |
| 15 | Codec | `SidecarBlobCodec` byte: `0=Raw` (only legal value today), `1=Zstd`, `2=Zstd+ByteShuffle` reserved for future use |

Readers reject any non-zero codec byte with a clear error so a future writer's bytes are never silently misinterpreted by a current reader.

## Footer

The footer body begins at the offset stored in the header's `FooterOffset` field and runs until the 8-byte tail. It opens with a **prologue** carrying file-level metadata, followed by one block per column written in schema order. The prologue's `ColumnCount` is authoritative for the per-column block loop — the header's `ColumnCount` field is informational only in v4.

### Footer prologue

```
uint64 : generation                  (commit counter; first write = 1, increments per commit)
uint64 : writerId                    (per-writer process-stable identity stamp; passive single-writer guard today)
uint64 : baseGeneration              (the generation this commit was based on; 0 for initial write)
byte   : tombstoneGranularity        (1 = chapter-level; reserved 0 = page-level)
int32  : columnCount                 (authoritative; matches the per-column block count below)
int32  : fileTableEntryCount         (zero when no external pages; non-zero when compaction has moved pages to .datum-pack files)
For each file-table entry:
  uint16  : fileId                   (>0; 0 is reserved for the primary file)
  string  : relativePath             (length-prefixed UTF-8; resolved against the .datum file's directory)
  byte[16] : fingerprint             (identity stamp of the external file; mismatch = stale manifest)

int32  : chapterTombstoneCount       (zero until first soft-delete; otherwise equals the file's chapter count)
int64[chapterTombstoneCount] : tombstoneOffsets
                                     (one per chapter index; -1 = no tombstones, otherwise the
                                      absolute file offset of an 8 KiB chapter tombstone bitmap)

int32  : columnDefaultCount          (number of columns carrying a SQL DEFAULT literal)
For each column-default entry:
  uint16 : columnIndex               (footer column index)
  string : sqlFragment               (length-prefixed UTF-8; SQL literal text re-parsed at read time
                                      via `SqlParser.Parse("SELECT <fragment>")`)

int16  : identityColumnIndex         (-1 = no IDENTITY column)
int64  : identitySeed                (starting value)
int64  : identityStep                (per-row increment; non-zero, may be negative)
int64  : identityNextValue           (running counter; updated on every commit that reserved values)
byte   : identityAcceptUserValues    (0 = GENERATED ALWAYS — explicit values rejected;
                                      1 = GENERATED BY DEFAULT — user-supplied values accepted,
                                      counter only consulted on omission)

byte   : primaryKeyColumnCount       (0 = no PK)
uint16[primaryKeyColumnCount] : primaryKeyColumnIndices
                                     (footer column indices in PK declaration order)
```

When `chapterTombstoneCount` is non-zero, it equals the file's actual chapter count (`⌈totalRowCount / (PagesPerChapter × pageSize)⌉`). The prologue's chapter tombstone offsets describe the file as a whole — tombstones apply to logical rows, not per-column-page, so a single bitmap per chapter index covers every column's view of that row range.

`columnDefaults`, the `IDENTITY` block, and `primaryKeyColumnIndices` make a `.datum` file self-describing for SQL DDL state — opening the file is enough to reconstruct the table's `DEFAULT` literals, `IDENTITY` spec + counter, and `PRIMARY KEY` columns without consulting `.datum-catalog.json`. Indices stay valid across `ADD COLUMN` and `DROP COLUMN` (drop is soft-tombstone, not index-shifting).

### Per-column block

```
For each column (in schema order, count = prologue.columnCount):
  string  : name                    (length-prefixed UTF-8)
  byte    : DataKind                (enum value)
  byte    : EncoderKind              (0=FixedWidth, 1=BitPackedBoolean, 2=VariableSlot)
  byte    : ColumnFlagsV2            (Nullable | IsArray | HasFixedShape | Tombstoned)

  If HasFixedShape:
    uint16  : shapeRank
    int32[] : dimensions[rank]      (e.g. [256, 512] for a 256×512 matrix)

  int32: pageCount
  For each page:
    uint16 : fileId                 (0 = primary file; >0 = file-table lookup for pages compacted into a .datum-pack)
    int64  : pageOffset             (absolute byte position within the file named by fileId)
    uint32 : pageByteLength
    uint16 : rowCount               (≤ pageSize; last page may be partial)
    bool   : hasZoneMap
    If hasZoneMap: ZoneMap          (per-page min/max/nullCount)

  int32: chapterCount
  ZoneMap[chapterCount]              (one per 64-page chapter; aggregated from page maps)

  If file's HasVolumeZoneMaps flag is set:
    int32: volumeCount
    ZoneMap[volumeCount]             (one per 16-chapter volume; aggregated from chapter maps)
```

The `Tombstoned` column flag (`0x08`) marks a soft-dropped column — its block stays in the footer for compaction-time reclamation, but readers skip it at schema enumeration.

### Zone-map serialization

```
uint32 : nullCount
bool   : hasMinMax                  (false for non-comparable kinds: Vector, Matrix, Tensor, Image, JsonValue, Array, Struct, byte arrays)
If hasMinMax:
  DataValue : minimum
  DataValue : maximum
```

Zone maps are populated for comparable types (numerics, booleans, strings, temporals, UUID). Non-comparable types carry only `nullCount`; min/max are omitted.

### Hierarchical pruning

Each tier aggregates the next finer level:

- **Page** — 1024 rows. Always present (one per page).
- **Chapter** — 64 pages = 64 K rows. Always present (one per chapter).
- **Volume** — 16 chapters = 1 M rows. Only emitted when `TotalRowCount > 1_000_000`; gated by the `HasVolumeZoneMaps` flag.

`DatumFileTableProviderV2.ScanAsync` walks volume → chapter → page when given a filter hint: a volume the predicate provably can't match short-circuits all 16 of its chapters; same for chapters and their 64 pages.

## File tail

8 bytes, enabling reverse-seek opens:

| Offset from EOF | Size | Field | Type | Value |
|-----------------|------|-------|------|-------|
| −8 | 4 | FooterByteLength | uint32 | Size of the footer body in bytes |
| −4 | 4 | TailMagic | bytes | `FMTD` (ASCII, `DTMF` reversed) |

Reader open path:

1. Seek to `fileLength − 8`, read the tail. Validate `FMTD` magic.
2. Compute `footerOffset = fileLength − 8 − footerByteLength`. Cross-check against the header's `FooterOffset` field.
3. Seek to `footerOffset`, deserialize the footer (schema + page directory + zone-map hierarchy per column).

No forward scan is required. Column data is then demand-loaded by seeking to each `PageDescriptorV2.PageOffset`.

## DataValue serialization (zone maps and indexes)

Zone maps in the footer and entries in the `.datum-index` sidecar serialize `DataValue` using the existing `IO.DataValueWriter` / `IO.DataValueReader` wire format, unchanged from v1:

```
byte:  DataKind enum
Then kind-specific payload:
  Boolean:    bool (1 byte)
  Int8/UInt8: 1 byte
  Int16/UInt16: 2 bytes
  Int32/UInt32/Float32: 4 bytes
  Int64/UInt64/Float64: 8 bytes
  Date:      int32 (day number)
  DateTime:  int64 (UTC ticks) + int16 (offset minutes)
  Time:      int64 (ticks of day)
  Duration:  int64 (ticks)
  Uuid:      byte[16]
  String / JsonValue: BinaryWriter length-prefixed UTF-8 string
  Image / byte arrays: int32 length + byte[length]
                       (byte arrays use wire-tag 56 — the legacy
                        UInt8Array enum value, kept as a wire constant
                        independent of the in-memory DataKind enum)
  Vector:    int32 length + float32[length]
  Matrix:    int32 rows + int32 cols + float32[rows × cols]
  Tensor:    int32 rank + int32[rank] dimensions + float32[∏dims]
  Array:     int32 length + recursive DataValue[length]
  Struct:    uint16 fieldCount + recursive DataValue[fieldCount]
```

The same wire format is used to pack `Struct` and legacy `Array` payloads into the sidecar when a `VariableSlot` encoder spills them: `uint16 fieldCount` (or `byte elementKind + uint32 elementCount` for arrays) followed by N field/element records.

## Sidecar index (`.datum-index`)

The `.datum` format is intentionally simple — all acceleration structures live in separate sidecar files. The [`.datum-index`](indexes.md) sidecar carries bloom filters, sorted value indexes, B+Tree indexes, bitmap indexes, and chunk-level statistics. The sidecar's chunk grain is 10 K rows by default (configurable); the v2 chapter (64 K rows) is its natural size if you want chunk boundaries to align with footer-level zone-map chapters.

Sidecar-bound values are skipped at the bloom layer (recall trade for self-contained content addressing); columns with any non-inline string are dropped from sorted / B+Tree / bitmap indexing entirely (the "indexable = self-contained" rule).

The index sidecar provides:

| Section | Purpose |
|---------|---------|
| **Fingerprint** | Staleness detection via striped SHA-256 of the source file |
| **Schema** | Cached column names, kinds, nullability, and total row count |
| **Chunk directory** | Per-chunk row ranges with per-column min/max/null/cardinality statistics |
| **Bloom filters** | Per-column, per-chunk Kirsch–Mitzenmacher double-hashed bloom filters |
| **Sorted value indexes** | Per-column sorted arrays enabling binary-search key lookup |
| **B+Tree indexes** | Persistent paged B+Trees for high-cardinality scalar columns |
| **Bitmap indexes** | Per-distinct-value chunk bitmaps for low-cardinality columns |

See [indexes.md](indexes.md) for the full binary specification.

## Optional sidecar (`.datum-blob`)

A `.datum-blob` companion file is the **heap** for non-inline payloads — long strings, JsonValue, byte arrays, images, vectors, structs, anything where the per-row payload exceeds DataValue's 16-byte inline tier. Bytes are addressed by absolute 64-bit file offset, so a single sidecar can hold terabytes of binary data without per-blob caps. Readers memory-map the entire sidecar and slice it directly — zero copy, zero decompression.

### Lazy materialization

The sidecar file is only created when a `VariableSlot` encoder actually emits a sidecar pointer. `.datum` files containing only inline-sized data leave no orphan `.datum-blob` artifact. The `HasSidecarReferences` file flag in the `.datum` header is set only when at least one `Append` actually fired against the sidecar.

### Header layout (32 bytes)

```
[magic       : 8 bytes  "DATUMBLB" little-endian (0x424C424D55544144)]
[version     : 4 bytes  uint32 = 1]
[reserved1   : 4 bytes  zero]
[fingerprint : 8 bytes  uint64 — random per-write, embedded in both sidecar and .datum]
[payloadHash : 8 bytes  xxHash3-64 over [HeaderSize..EOF), patched on close]
[blob bytes  : append-only payload region — concatenated raw bytes]
```

There is no internal framing between blobs in the payload region. Each blob's location is recorded in its referencing column page's pointer slot (offset + length + codec); the sidecar itself is opaque to anyone not holding the corresponding `.datum`.

### Pointer slot

Per `VariableSlot` row whose inline bit is clear:

```
[offset    : int64  absolute byte position in .datum-blob (includes the 32-byte header)]
[length    : 5 bytes (40-bit) payload size, supports per-blob values up to 1 TiB]
[reserved  : 2 bytes — zero in v1; reserved for length-field expansion past 1 TiB]
[codec     : 1 byte SidecarBlobCodec (0=Raw)]
```

### Codec evolution

The codec byte is reserved for future use; the writer always emits `Raw` today:

| Codec | Value | Notes |
|-------|-------|-------|
| `Raw` | 0 | Stored as-is. Only legal value today. |
| `Zstd` | 1 | Reserved — per-blob Zstd compression. |
| `ZstdShuffle` | 2 | Reserved — byte-shuffle pre-filter + Zstd. Intended for Vector/Matrix/Tensor where shuffle dramatically improves compression. |

Per-blob codec means each value in a column can choose its own compression independently. The `Image` column might mix already-compressed JPEGs (codec=Raw) with uncompressed bitmaps that benefit from Zstd; the writer picks per blob.

## Optional sidecar (`.datum-pkindex`)

A `.datum-pkindex` companion file is a **mutable B+Tree** backing the table's `PRIMARY KEY` constraint when the PK is a single column. Distinct from `.datum-index` (bulk-loaded acceleration structures, invalidated on mutation): the PK index is updated incrementally on every `INSERT` and never goes stale. The same 8 KiB page format is used, but leaves are uncompressed (splits stay simple) and the file carries its own crash-safe header.

The sidecar is created at `CREATE TABLE` time when a single-column PK is declared; tables with no PK or a composite PK don't produce one (composite PK uses a scan-based uniqueness check at the executor layer).

### File layout

```
[ Header Slot A : 256 bytes ]   offset 0
[ Header Slot B : 256 bytes ]   offset 256
[ Page 0       : 8 KiB     ]   offset 512
[ Page 1       : 8 KiB     ]
   ...
```

Each commit writes pages to fresh page ids (copy-on-write), then flips the inactive header slot to point at the new root. A torn write past the page region but before the new header lands leaves the previous slot's commit gen highest — readers pick that slot and see the previous tree.

### Header slot (256 bytes)

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | Magic | bytes | `PKBT` |
| 4 | 4 | Version | uint32 | `1` |
| 8 | 8 | CommitGen | int64 | Monotonic counter, incremented per commit |
| 16 | 4 | RootPageId | uint32 | `0xFFFFFFFF` if tree is empty |
| 20 | 4 | FreeListHead | uint32 | `0xFFFFFFFF` (free-list reuse not yet emitted; bump-only allocation) |
| 24 | 4 | PageCount | uint32 | Total pages allocated in the file |
| 28 | 2 | TreeHeight | uint16 | 0 for empty, 1 if root is a leaf, etc. |
| 30 | 8 | EntryCount | int64 | Total key/pointer pairs across leaves |
| 38 | 2 | KeyDataKind | uint16 | `DataKind` of the indexed column |
| 40 | 212 | Reserved | zero | |
| 252 | 4 | Crc32 | uint32 | CRC32 over bytes [0..251] |

Reader open: read both slots, validate magic + version + CRC on each, pick the slot with the higher commit gen. If both slots fail validation the file is rejected as corrupt.

### Page layouts

The common header (4 bytes) prefixes every page:

```
[ PageType : 1 byte ]  (0=Free, 1=Internal, 2=Leaf)
[ KeyCount : 2 bytes ]
[ Reserved : 1 byte  ]
```

**Leaf page** (8 KiB, uncompressed):

```
[ Common header     : 4 bytes  ]
[ PrevLeaf          : 4 bytes  ]   (uint32, 0xFFFFFFFF if first)
[ NextLeaf          : 4 bytes  ]   (uint32, 0xFFFFFFFF if last)
[ PayloadLength     : 4 bytes  ]   (int32, used bytes in entries region)
[ Entries           : variable ]   (DataValue + int32 chunkIndex + int64 rowOffset, repeated)
[ Zero padding to 8 KiB        ]
```

**Internal page** (8 KiB):

```
[ Common header        : 4 bytes ]
[ Separator keys       : variable ]   (DataValue, KeyCount entries)
[ Child page ids       : (KeyCount + 1) × 4 bytes ]
[ Zero padding to 8 KiB ]
```

**Free page** (8 KiB):

```
[ PageType=0 : 1 byte ]
[ Padding    : 3 bytes ]
[ NextFreeId : 4 bytes ]   (uint32 linked-list pointer; reserved for future free-list reuse)
[ Zero padding to 8 KiB ]
```

### Insert and commit

Insert walks root→leaf recording the path, builds the merged entry list, then COW-rewrites every page on the path with fresh page ids. If a leaf would overflow it splits and the separator key bubbles up; recursive internal splits can grow a new root. After all new pages are written and fsync'd, the inactive header slot is rewritten with the new root id + CommitGen+1 + CRC and fsync'd a second time. Old pages (the path that was just rewritten) are leaked on disk pending a future free-list reuse pass.

The PK index is single-writer per data path. The provider's mutation lock (the same one that gates `INSERT`/`DELETE`/`ALTER`) serialises all access; the file is opened with `FileShare.None`.

## Reading flow

```
1. Open file
   └─ Seek to EOF − 8, read tail, validate FMTD magic
   └─ Compute footerOffset, deserialize footer (schema + page directory + zone-map hierarchy)
   └─ If HasSidecarReferences flag set, mmap the .datum-blob sidecar

2. Plan query
   └─ Identify projected columns from SELECT/WHERE/JOIN clauses
   └─ Walk filter predicate against zone maps:
        Volume → Chapter → Page (skip whole subtrees on provable miss)
   └─ If .datum-index sidecar exists, additionally apply bloom / sorted / B+Tree / bitmap pruning at chunk grain

3. Read surviving pages
   └─ For each surviving page:
      └─ Seek to PageOffset, read PageByteLength bytes (one I/O per page)
      └─ Open the appropriate decoder (FixedWidth / BitPackedBoolean / VariableSlot)
      └─ Materialize DataValues row by row:
            FixedWidth → check null bit, slice stride bytes from payload
            BitPackedBoolean → check null bit, read value bit
            VariableSlot inline → check null + inline bit, slice 16 bytes by per-row length
            VariableSlot pointer → emit sidecar-backed DataValue (offset, length, codec)
```

## Write flow

```
1. Initialize
   └─ Open seekable stream
   └─ Write 32-byte header with placeholder TotalRowCount and FooterOffset
   └─ Build per-column encoders (FixedWidth / BitPackedBoolean / VariableSlot)
   └─ Build per-column zone-map hierarchy builders

2. Per RowBatch
   └─ For each row, for each column:
      └─ encoder.Append(value)
      └─ If encoder.IsFull (1024 rows), flush:
            - Write page bytes directly to the data stream
            - Record (offset, length, rowCount, zoneMap) in the column's page directory
            - Roll the page zone map into the chapter / volume hierarchy
   └─ Stream.Flush() so growing file is visible to readers / cancel preserves bytes

3. Finalize
   └─ Flush trailing partial pages for each column
   └─ For each column: build column footer (descriptor + page directory + chapter zone maps + optional volume zone maps)
   └─ Write footer body, capture offset and byte length
   └─ Write 8-byte tail (footerByteLength + FMTD magic)
   └─ Seek to header offset 0, patch TotalRowCount, FooterOffset, and Flags
```

## Crash recovery

The format is designed so that a process dying mid-write — crash, kill, power loss — never corrupts a previously-committed state. The mechanism is **tail-flip-as-commit**: a write becomes durable only when the new 8-byte tail (`FMTD` magic + footer length) is the file's last 8 bytes. Until that moment the existing tail at the previous EOF is the authoritative boundary, and every byte written past it is invisible to readers.

### What's on disk during a write

Mid-session, after several `WriteRowBatch` calls but before `FinalizeWriter`:

```
[ header ][ committed pages ][ committed footer ][ committed tail │ pre-session EOF ]
                                                                   [ uncommitted pages │ current EOF ]
```

Page bytes are flushed eagerly (`Stream.Flush()` after every batch) so growing files are visible to `ls -l`. But there is no new footer and no new tail. The committed tail at `pre-session EOF` is the durable boundary.

### Crash modes

- **Process kill / power loss before commit.** Trailing bytes past the committed tail are partial pages with no footer pointing at them. They are unreachable garbage.
- **Graceful abort (session disposed without `CommitAsync`).** Same on-disk shape as a crash — the writer just exits without finalizing. The recovery path is identical; abort is essentially free.
- **Crash during `FinalizeWriter` itself.** Footer bytes are streamed before the tail. A partial footer with no tail behind it presents identically to "crash before commit" — recovery scans backward to the previous committed tail and the in-progress footer is treated as garbage.

### Recovery on reopen

Two paths:

**Writer reopens via `DatumFileWriterV2.OpenForAppend`.** `RecoverIfTorn` scans backward from EOF for the last valid `FMTD` tail magic, validates the footer it points to, and **truncates the file to that point** with `SetLength`. After truncation the file ends at the previous good commit; the new writer proceeds normally and uncommitted bytes from the dead session are gone.

**Reader opens via `DatumFileReaderV2.Open`.** The reader runs the same backward `FMTD` scan as a non-destructive recovery: it locates the last valid tail and treats that as its logical EOF, ignoring any trailing partial bytes. The on-disk file isn't modified — readers don't truncate — but the reader's view of the file is the last committed state. This means a crashed write doesn't block reads; the next writer reopen will perform the actual truncation.

### What's preserved vs. dropped

- **Preserved.** Every batch from a previously-committed session. The committed footer/tail combination is the durable boundary.
- **Dropped.** Every batch from the dead session. Page bytes were on disk, but without a footer pointing at them they are unreachable.
- **Sidecar.** `AppendRowsAsync` extends `.datum-blob` for non-inline payloads. The blob writer doesn't update the blob's `payloadHash` until close, so a crash leaves trailing blob bytes that nothing references. They are harmless — the (committed) primary footer's pointers don't reach them — but they are wasted space. The next blob writer either appends past them (a small gap of dead bytes) or a future compaction PR can reclaim them.

### Why this works without a journal

A traditional WAL-based system writes intent records to a separate journal and rolls them back on crash. The `.datum` format avoids that complexity by making the commit **a single 8-byte write at EOF** that is observed only when complete:

- Bytes written past the old tail but before the new tail aren't referenced by any footer, so they are invisible to readers regardless of how much was flushed.
- A single `FMTD` write either lands or doesn't — there is no partial-magic state that confuses the scan-backward logic. (The scan validates the magic AND that the footer-length it points to lands inside the file AND that the footer at that offset deserializes; corrupt magic fails all three checks and the scan continues backward.)
- The previous tail remains physically present in the file and authoritative until the new tail is written. Reopens converge on it deterministically.

The trade is that aborted sessions leave physical bytes on disk that need to be truncated by the next writer (or accepted as a gap). For append-mostly workloads this overhead is negligible; compaction reclaims it eventually.

## Table mutation API

ALTER TABLE / INSERT / DELETE entry points live directly on `ITableProvider` so SQL DDL/DML lowers the same way regardless of whether the underlying table is a `.datum` file, an in-memory fixture, or a system table. Callers resolve a provider through the catalog and invoke the mutation directly:

```csharp
ITableProvider provider = catalog[tableName];
provider.AddColumn(columnInfo);                            // ALTER TABLE … ADD COLUMN …
provider.DropColumn(columnName);                           // ALTER TABLE … DROP COLUMN …
await provider.AppendRowsAsync(batches, ct);               // INSERT INTO …
provider.DeleteRows(rowIndices);                           // DELETE FROM … (linear row indices)
```

### Capability flags + default-throw

`ITableProvider` carries three opt-in flags — `CanAlterColumns`, `CanAppendRows`, `CanDeleteRows` — defaulting to `false`. Each mutation method has a default interface implementation that throws `NotSupportedException`. Mutable providers (the .datum file provider, the in-memory provider) override the flags and the methods. System tables (information_schema, datum_catalog.*, models, udfs, …) leave the defaults alone, so the provider's default `NotSupportedException` surfaces a clear `"Table 'X' does not support <op> (Can<op> is false)"` error.

Read-only semantics are derived: a table is read-only for an operation when its provider's corresponding `Can…` flag is `false`. There is no separate sub-interface — the four mutation methods + three flags all live on `ITableProvider`.

### Datum file provider — snapshot semantics

`DatumFileTableProviderV2` wraps its read-side state (open `DatumFileReaderV2`, sidecar mmap, derived schema, schema→footer index translation, chapter tombstone bitmaps) in a refcounted `Snapshot`. Mutations route through the format's static helpers (`DatumFileWriterV2.AddColumn` / `DropColumn` / `OpenForAppend` / `SoftDeleteRows`), then swap the snapshot atomically. In-flight scans hold a refcount on the snapshot they captured at scan-open; the retired snapshot disposes when its refcount drops to zero — closest analogue is SQL Server's RCSI, but the version chain is the .datum file's footer-LSN sequence rather than a tempdb side-table.

`AppendRowsAsync` is the only mutation that grows the companion `.datum-blob`. After the writer commits, the provider reopens the sidecar mmap (the existing view was sized to the pre-mutation length and can't see the appended bytes) and calls `SidecarRegistry.UpdateAt` on the same `storeId`. The new `IBlobSource`'s mmap is a strict superset of the old one's, so existing storeId-stamped DataValues continue to resolve correctly through the registry.

### Append sessions

For streaming writes — `INSERT … SELECT`, programmatic ingest of unbounded sources — `BeginAppend` returns an `IAppendSession` with explicit `WriteAsync` / `CommitAsync` semantics:

```csharp
await using IAppendSession session = catalog[tableName].BeginAppend();
await foreach (RowBatch batch in selectPipeline.WithCancellation(ct))
{
    await session.WriteAsync(batch, ct);
}
await session.CommitAsync(ct);
```

`AppendRowsAsync` is a convenience wrapper over `BeginAppend` (open, drain, commit) — the session is the primitive. One session is allowed per provider at a time; concurrent `BeginAppend` calls block on a `SemaphoreSlim` so the call ordering across awaits is well-defined. Disposing the session without calling `CommitAsync` aborts cleanly: the writer exits without writing the new tail and the partial bytes get cleaned up by the next writer's torn-tail recovery (see [Crash recovery](#crash-recovery)).

## Source files

| File | Purpose |
|------|---------|
| `DatumFile/V2/DatumFormatV2.cs` | Magic bytes, version, file-flag enum, page/chapter/volume constants, sidecar slot offsets |
| `DatumFile/V2/HeaderV2.cs` | 32-byte file header read/write |
| `DatumFile/V2/FooterV2.cs` | Footer body read/write (per-column blocks) |
| `DatumFile/V2/ColumnDescriptorV2.cs` | Per-column metadata (name, kind, encoder, flags, optional fixed shape) |
| `DatumFile/V2/PageDescriptorV2.cs` | Per-page directory entry (offset, byte length, row count, zone map) |
| `DatumFile/V2/ColumnFooterV2.cs` | Per-column block (descriptor + pages + chapter/volume zone maps) |
| `DatumFile/V2/DatumFileWriterV2.cs` | Streaming writer with page-flush on encoder full + footer patch on finalize |
| `DatumFile/V2/DatumFileReaderV2.cs` | Tail-first reader with random-access page reads |
| `DatumFile/V2/Encoding/FixedWidthPageEncoderV2.cs` | Encoder for fixed-stride scalars |
| `DatumFile/V2/Encoding/BitPackedBooleanPageEncoderV2.cs` | Encoder for booleans (null bitmap + value bitmap) |
| `DatumFile/V2/Encoding/VariableSlotPageEncoderV2.cs` | Encoder for variable-length kinds (inline-vs-pointer 16-byte slots + sidecar spill) |
| `DatumFile/V2/Encoding/PageEncoderFactoryV2.cs` | Picks the right encoder for a column descriptor |
| `DatumFile/V2/Encoding/ZoneMapHierarchyBuilderV2.cs` | Aggregates page → chapter → volume zone maps |
| `DatumFile/V2/Encoding/PageZoneMapBuilderV2.cs` | Per-column-page zone-map accumulator (records min/max/null) |
| `DatumFile/V2/Decoding/FixedWidthPageDecoderV2.cs` | Random-access reader for fixed-width pages |
| `DatumFile/V2/Decoding/BitPackedBooleanPageDecoderV2.cs` | Random-access reader for boolean pages |
| `DatumFile/V2/Decoding/VariableSlotPageDecoderV2.cs` | Random-access reader for variable-slot pages (inline + sidecar pointer) |
| `DatumFile/V2/Decoding/PageDecoderFactoryV2.cs` | Picks the right decoder for a column descriptor |
| `Catalog/Providers/DatumFileTableProviderV2.cs` | Engine-facing provider with three-tier zone-map pruning + seek session + manifest/index discovery + catalog-level mutation methods (AddColumn / DropColumn / AppendRowsAsync / DeleteRows) routed through a refcounted reader snapshot |
| `Catalog/Providers/DatumFileSeekSessionV2.cs` | Caller-owned seek session with page-index math (`pageIndex = startRow / pageSize`) |
| `Catalog/TableCatalog.cs` | Registry of named tables; provider-agnostic `AddColumn` / `DropColumn` / `BeginAppend` / `AppendRowsAsync` / `DeleteRows` passthroughs that gate on per-provider capability flags |
| `Catalog/ITableProvider.cs` | Provider interface with `CanAlterColumns` / `CanAppendRows` / `CanDeleteRows` flags + default-throw mutation methods; `AppendRowsAsync` is a default-impl wrapper over `BeginAppend` |
| `Catalog/IAppendSession.cs` | Caller-owned streaming append session: `WriteAsync` / `CommitAsync` / abort-on-dispose |
| `DatumFile/V2/TornTailScanner.cs` | Backward `FMTD` scan shared by writer (destructive truncate) and reader (non-destructive logical-EOF recovery) |
| `DatumFile/Sidecar/SidecarRegistry.cs` | Per-catalog `storeId` → `IBlobSource` map; `UpdateAt` swaps the source after AppendRows grows the `.datum-blob` past the previous mmap view |
| `DatumFile/Sidecar/SidecarConstants.cs` | `.datum-blob` magic (`DATUMBLB`), version, header layout |
| `DatumFile/Sidecar/SidecarWriteStore.cs` | Lazy-materialised, locked, append-only writer for the `.datum-blob` sidecar |
| `DatumFile/Sidecar/SidecarReadStore.cs` | mmap-backed reader with header + fingerprint validation |

