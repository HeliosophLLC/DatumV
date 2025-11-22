# `.datum` File Format

[← Back to README](../README.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Providers](providers.md)

The `.datum` format is a binary columnar store designed for high-throughput ML/ETL workloads. It's **uncompressed and mmap-friendly**: data lives in fixed-stride 1024-row pages with three compact encoders, hierarchical zone maps for predicate pruning, and a companion sidecar heap for non-inline payloads. The trade vs a heavily-compressed alternative is ~2–4× larger files; the win is decompress-free reads, simpler decode logic, and bounded peak memory during both write and read.

Two optional companion sidecars extend the base format:

- A companion [`.datum-index`](indexes.md) sidecar carries bloom filters, sorted value indexes, B+Tree indexes, bitmap indexes, and chunk-level statistics for query acceleration. The optional [`.datum-mapped-index`](indexes.md#memory-mapped-sorted-indexes) variant provides memory-mapped fixed-width sorted indexes for zero-copy multi-tenant access.
- A companion [`.datum-blob`](#optional-sidecar-datum-blob) sidecar carries non-inline payloads (long strings, byte arrays, images, vectors, structs) addressed by 64-bit offsets so the data file itself stays compact. The sidecar is created lazily — only `.datum` files that actually carry non-inline payload produce one.

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
| 0 | 4 | Magic | bytes | `DTMF` (ASCII, unchanged from v1) |
| 4 | 2 | FormatVersion | uint16 | `3` |
| 6 | 2 | Flags | uint16 | `DatumFileFlagsV2` bitmask (see below) |
| 8 | 4 | ColumnCount | int32 | Number of columns in the schema |
| 12 | 4 | PageSize | int32 | Rows per page (default 1024) |
| 16 | 8 | TotalRowCount | int64 | Patched at finalize |
| 24 | 8 | FooterOffset | int64 | Absolute byte offset of the footer body, patched at finalize |

### File flags (`DatumFileFlagsV2`)

| Flag | Value | Meaning |
|------|-------|---------|
| `None` | 0 | No special flags |
| `HasSidecarReferences` | 0x01 | At least one row spilled to the companion `.datum-blob`; the sidecar must be present at read time |
| `HasVolumeZoneMaps` | 0x02 | Volume-level zone maps were emitted (file row count exceeded the 1 M-row threshold) |

`HasSidecarReferences` is only set when the sidecar's blob sink actually received an `Append` — files whose variable-slot columns happened to all stay inline leave the flag clear so the reader doesn't try to open a non-existent companion file.

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

### `VariableSlot` (String, JsonValue, Array, UInt8Array, Image, Vector, Matrix, Tensor, Struct, typed arrays)

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
| 13–14 | Reserved | Zero in v1; reserved for future length-field expansion past 1 TiB |
| 15 | Codec | `SidecarBlobCodec` byte: `0=Raw` (only legal value in v1), `1=Zstd`, `2=Zstd+ByteShuffle` reserved for v2.x |

Readers reject any non-zero codec byte with a clear error so a future v2.x writer's bytes are never silently misinterpreted by a v1 reader.

## Footer

The footer body begins at the offset stored in the header's `FooterOffset` field and runs until the 8-byte tail. It contains one block per column, written in schema order. The column count is taken from the header — there's no separate `columnCount` prefix in the footer body.

### Per-column block

```
For each column (in schema order):
  string  : name                    (length-prefixed UTF-8)
  byte    : DataKind                (enum value)
  byte    : EncoderKind              (0=FixedWidth, 1=BitPackedBoolean, 2=VariableSlot)
  byte    : ColumnFlagsV2            (Nullable | IsArray | HasFixedShape)

  If HasFixedShape:
    uint16  : shapeRank
    int32[] : dimensions[rank]      (e.g. [256, 512] for a 256×512 matrix)

  int32: pageCount
  For each page:
    int64  : pageOffset             (absolute byte position)
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

### Zone-map serialization

```
uint32 : nullCount
bool   : hasMinMax                  (false for non-comparable kinds: Vector, Matrix, Tensor, Image, UInt8Array, JsonValue, Array, Struct)
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
  UInt8Array / Image: int32 length + byte[length]
  Vector:    int32 length + float32[length]
  Matrix:    int32 rows + int32 cols + float32[rows × cols]
  Tensor:    int32 rank + int32[rank] dimensions + float32[∏dims]
  Array:     int32 length + recursive DataValue[length]
  Struct:    uint16 fieldCount + recursive DataValue[fieldCount]
```

The same wire format is used to pack `Struct` and legacy `Array` payloads into the sidecar when a `VariableSlot` encoder spills them: `uint16 fieldCount` (or `byte elementKind + uint32 elementCount` for arrays) followed by N field/element records.

## Sidecar index (`.datum-index`)

The `.datum` format is intentionally simple — all acceleration structures live in separate sidecar files. The [`.datum-index`](indexes.md) sidecar carries bloom filters, sorted value indexes, B+Tree indexes, bitmap indexes, and chunk-level statistics. The sidecar's chunk grain is 10 K rows by default (configurable); the v2 chapter (64 K rows) is its natural size if you want chunk boundaries to align with footer-level zone-map chapters.

The index format is unchanged across v1 and v2 source files — `Indexer` opens either format via the version-aware `DatumFileTableProvider.Open` factory. Sidecar-bound values are skipped at the bloom layer (recall trade for self-contained content addressing); the existing v1 rule of dropping non-inline strings from sorted/B+Tree indexes still applies.

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

The codec byte is reserved for future use; v1 always writes `Raw`:

| Codec | Value | Notes |
|-------|-------|-------|
| `Raw` | 0 | Stored as-is. Only legal value in v1. |
| `Zstd` | 1 | Reserved for v2.x — per-blob Zstd compression. |
| `ZstdShuffle` | 2 | Reserved for v2.x — byte-shuffle pre-filter + Zstd. Intended for Vector/Matrix/Tensor where shuffle dramatically improves compression. |

Per-blob codec means each value in a column can choose its own compression independently. The `Image` column might mix already-compressed JPEGs (codec=Raw) with uncompressed bitmaps that benefit from Zstd; the writer picks per blob.

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
| `Catalog/Providers/DatumFileTableProviderV2.cs` | Engine-facing provider with three-tier zone-map pruning + seek session + manifest/index discovery |
| `Catalog/Providers/DatumFileSeekSessionV2.cs` | Caller-owned seek session with page-index math (`pageIndex = startRow / pageSize`) |
| `DatumFile/Sidecar/SidecarConstants.cs` | `.datum-blob` magic (`DATUMBLB`), version, header layout — unchanged from v1 |
| `DatumFile/Sidecar/SidecarWriteStore.cs` | Lazy-materialised, locked, append-only writer for the `.datum-blob` sidecar |
| `DatumFile/Sidecar/SidecarReadStore.cs` | mmap-backed reader with header + fingerprint validation |

