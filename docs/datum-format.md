# `.datum` File Format

[← Back to README](../README.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Providers](providers.md)

The `.datum` format is a binary columnar store designed for high-throughput ML/ETL workloads. It stores data in compressed column pages grouped by row group, with a self-describing footer that carries schema, encoding metadata, and per-column zone maps. Two optional companion sidecars extend the base format:

- A companion [`.datum-index`](indexes.md) sidecar carries bloom filters, sorted value indexes, B+Tree indexes, bitmap indexes, and chunk-level statistics for query acceleration. The optional [`.datum-mapped-index`](indexes.md#memory-mapped-sorted-indexes) variant provides memory-mapped fixed-width sorted indexes for zero-copy multi-tenant access.
- A companion [`.datum-blob`](#optional-sidecar-datum-blob) sidecar carries Large Binary Objects (images, byte arrays, future video) addressed by 64-bit offsets so the data file itself stays compact. The sidecar is created lazily — only `.datum` files that actually carry binary payload produce one.

## Design goals

- **Column-selective reads.** Only decompress columns referenced by the query.
- **Zone-map pruning.** Per-column min/max/null-count in every row group enables row-group skipping without touching data pages.
- **Seekable access.** Footer-at-end layout lets readers locate metadata with two seeks (tail → footer → data) instead of a forward scan. Combined with source indexes that record byte offsets, readers can jump directly to relevant row groups.
- **Streaming writes.** The writer appends data sequentially and patches the header on finalize. No pre-computed metadata is required; shapes and encoding strategies are inferred on first flush.
- **Adaptive encoding.** Each column page independently selects its encoding (dictionary, delta, bit-packed, raw, etc.) based on the data observed in that row group.
- **Compression.** Zstd (level 3) by default, with a BLOSC-style byte-lane shuffle pre-filter for floating-point pages to improve compression ratios on correlated data (embeddings, sensor readings, pixel values).

## Physical layout

```
┌──────────────────────────────────────┐  Offset 0
│  File Header (28 bytes)              │
├──────────────────────────────────────┤  Offset 28
│  Row Group 0                         │
│    Column Page 0  (compressed)       │
│    Column Page 1  (compressed)       │
│    ...                               │
│    Column Page N-1 (compressed)      │
├──────────────────────────────────────┤
│  Row Group 1                         │
│    Column Page 0  ...                │
│    ...                               │
├──────────────────────────────────────┤
│  ...                                 │
├──────────────────────────────────────┤  footerOffset
│  Footer                              │
│    Schema block                      │
│    Row group directory               │
├──────────────────────────────────────┤
│  Tail (8 bytes)                      │
└──────────────────────────────────────┘  EOF
```

Column pages are written consecutively within each row group, with no framing bytes between pages. Their byte offsets and sizes are recorded in the footer's row group directory. This means column data can only be located via the footer — there are no inline page headers.

## File header

28 bytes, little-endian. Written on initialization; the last three fields are patched after all data has been flushed.

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | Magic | bytes | `DTMF` (ASCII) |
| 4 | 2 | FormatVersion | uint16 | `1` |
| 6 | 2 | Flags | uint16 | Bitmask: `HasDictionaryPages` (0x01), `HasZoneMaps` (0x02) |
| 8 | 4 | RowGroupCount | uint32 | Total number of row groups (patched at finalize) |
| 12 | 8 | TotalRowCount | int64 | Total rows across all row groups (patched at finalize) |
| 20 | 8 | FooterOffset | int64 | Byte position of the start of the footer block (patched at finalize) |

## Data region

### Row groups

Rows are buffered in memory and flushed to disk as a **row group** when the buffer reaches the configured size (default 65,536 rows). Each row group produces one compressed column page per column, written sequentially to the output stream.

The writer automatically halves the row group size (floor: 512 rows) for subsequent groups when any `FixedFloat32` page exceeds 32 MiB uncompressed. This prevents individual pages from dominating memory during decode.

### Column pages

Each column page is a single contiguous blob of compressed bytes. There is no inline page header — all metadata (byte offset, compressed length, uncompressed length, encoding, compression codec, zone map) is stored in the footer's `DatumColumnChunkDescriptor`.

The page payload is produced by:

1. **Encoding** — The encoder transforms `DataValue[]` into a raw byte buffer using one of the encoding strategies described below.
2. **Compression** — The encoded bytes are compressed using the selected codec (Zstd by default).

### Encoding strategies

Each column page independently records its encoding as a single `DatumEncoding` byte. The writer selects the encoding based on the column's `DataKind` and may change strategy between row groups (e.g., promoting a column to dictionary encoding after observing its cardinality).

| Encoding | Value | Used for | Layout |
|----------|-------|----------|--------|
| `Raw` | 0 | Float32, UInt8, Uuid | Dense binary array: `float32[N]`, `byte[N]`, or `byte[16×N]` |
| `BitPacked` | 1 | Boolean | Two bit vectors: `nullBitmap[⌈N/8⌉]` then `valueBitmap[⌈N/8⌉]` |
| `DeltaInt32` | 2 | Date | `nullBitmap[⌈N/8⌉]` then delta-encoded `int32[N]` relative to first non-null |
| `DeltaInt64` | 3 | DateTime, Time, Duration | `nullBitmap[⌈N/8⌉]` then delta-encoded `int64[N]` relative to first non-null. DateTime pages append a secondary `int16[N]` array of UTC offset minutes |
| `FixedFloat32` | 4 | Float32, Vector, Matrix, Tensor | `nullBitmap[⌈N/8⌉]` then `float32[N × elementsPerRow]`. Null rows store `NaN` to preserve implicit element offsets. A byte-lane shuffle is applied before compression |
| `VariableBytes` | 5 | String, JsonValue, UInt8Array, Image | `nullBitmap[⌈N/8⌉]` then `uint32 offsets[N+1]` then `byte pool[offsets[N]]`. Null rows: `offsets[i] == offsets[i+1]` with null bit set |
| `VariableDataValue` | 6 | Array (heterogeneous) | Same offset-pool layout as `VariableBytes`, but each pool entry is a serialized `DataValue` |
| `DictionaryRLE` | 7 | Low-cardinality String, Float32 | In-page dictionary followed by code array: `uint8[N]` when ≤ 255 unique values (sentinel `0xFF` = null), otherwise `uint16[N]` (sentinel `0xFFFF`) |
| `ExternalBytes` | 8 | Image, UInt8Array (large blobs) | Same layout as `VariableBytes`, but pool contains UTF-8 relative path strings referencing sidecar files instead of raw bytes |

#### Null bitmap

Bit-major layout: bit `i % 8` in byte `⌊i / 8⌋`. A set bit indicates the row is null.

#### Float byte-lane shuffle

Before Zstd compression, `FixedFloat32` pages pass through a BLOSC-style byte shuffle that separates the four byte lanes of each `float32` into four contiguous blocks:

```
Input:   [b0 b1 b2 b3] [b0 b1 b2 b3] ... (N floats, 4N bytes)
Output:  [b0 b0 b0 ...] [b1 b1 b1 ...] [b2 b2 b2 ...] [b3 b3 b3 ...]
          └── N bytes ─┘ └── N bytes ─┘ └── N bytes ─┘ └── N bytes ─┘
```

This creates long runs of similar byte values (e.g., exponent bytes cluster together, sign bytes are mostly `0x00` or `0x3F`) that the LZ77 back-reference engine in Zstd can exploit. The unshuffle reverses the transform on decode.

#### Variable-length offset table

`VariableBytes` and `VariableDataValue` pages use an N+1 offset table: `offsets[0] = 0`, `offsets[i]` is the byte position of row `i` in the pool, and `offsets[N]` is the total pool length. This allows O(1) random access to any row. Null rows have `offsets[i] == offsets[i+1]`; the null bitmap distinguishes them from empty strings.

#### Blob externalization

When any blob in a row group exceeds the column's externalization threshold (default 1 MiB), the entire column page switches to `ExternalBytes` encoding. Blobs are written to sidecar files in a `.datum_blobs/` directory adjacent to the `.datum` file, and the page stores relative path strings instead of raw bytes. This keeps the column page compact and prevents individual large blobs from inflating the file.

> **Note:** A unified [`.datum-blob`](#optional-sidecar-datum-blob) sidecar (one file holding all binary payloads, addressed by 64-bit offsets) is being introduced as the successor to the per-blob `.datum_blobs/` directory mechanism. Both code paths coexist for backwards compatibility with existing files.

## Footer

The footer begins at the byte offset stored in the header's `FooterOffset` field. It contains two blocks written sequentially:

### Schema block

Describes all columns present in the file.

```
int32:   columnCount
For each column:
  string:  name              (BinaryWriter length-prefixed UTF-8)
  byte:    DataKind           (enum value)
  byte:    DatumColumnFlags   (bitmask)
  If FixedShape flag is set:
    uint16:  shapeRank
    int32[]: dimensions[rank]  (e.g. [256, 512] for a 256×512 matrix)
  If ExternBlobs flag is set:
    uint32:  externalizationThresholdBytes
```

**Column flags:**

| Flag | Value | Meaning |
|------|-------|---------|
| `Nullable` | 0x01 | Column may contain nulls |
| `FixedShape` | 0x02 | Vector/Matrix/Tensor column with uniform dimensions (frozen on first row group) |
| `DictionaryEligible` | 0x04 | Column uses or is eligible for dictionary encoding |
| `ExternBlobs` | 0x08 | Large blobs externalized to sidecar files |

### Row group directory

```
uint32:  rowGroupCount
For each row group:
  uint32:  rowCount
  For each column (in schema order):
    DatumColumnChunkDescriptor:
      int64:   pageOffset             (absolute byte position)
      uint32:  compressedByteLength
      uint32:  uncompressedByteLength
      byte:    DatumEncoding
      byte:    DatumCompression
      DatumZoneMap:
        uint32:  nullCount
        bool:    hasMinMax
        If hasMinMax:
          DataValue: minimum
          DataValue: maximum
```

Zone maps are populated for comparable types (Float32, UInt8, Boolean, String, Date, DateTime, Time, Duration, Uuid). Non-comparable types (Vector, Matrix, Tensor, Image, UInt8Array, JsonValue, Array) carry only `nullCount`; `minimum` and `maximum` are omitted.

## File tail

The last 8 bytes of the file enable reverse-seek opening:

| Offset from EOF | Size | Field | Type | Value |
|-----------------|------|-------|------|-------|
| −8 | 4 | FooterByteLength | uint32 | Size of the footer block in bytes |
| −4 | 4 | TailMagic | bytes | `FMTD` (ASCII — `DTMF` reversed) |

A reader opens a `.datum` file with two seeks:

1. Seek to `fileLength − 8`, read the tail. Validate `FMTD` magic.
2. Compute `footerOffset = fileLength − 8 − footerByteLength`. Seek there, read and deserialize the footer (schema + row group directory).

No forward scan is required. Column data is then demand-loaded by seeking to the `pageOffset` recorded in each `DatumColumnChunkDescriptor`.

## Compression

| Codec | Enum value | Library | Notes |
|-------|------------|---------|-------|
| None | 0 | — | For already-compressed blobs (JPEG, PNG, WebP) |
| Zstd | 1 | ZstdSharp.Port | Default for all columns. Level 3 balances speed and ratio |
| Zlib | 2 | System.IO.Compression | BCL-only fallback (DeflateStream) |
| Brotli | 3 | System.IO.Compression | High ratio, slow encode |

The codec is selected per column page and recorded in the `DatumColumnChunkDescriptor`. A single file can mix codecs across columns and row groups.

## DataValue serialization

Both zone maps (in the footer) and index entries (in the sidecar) serialize `DataValue` using a common wire format:

```
byte:  DataKind enum
Then kind-specific payload:
  Float32:    float32
  UInt8:     byte
  Boolean:   bool (1 byte)
  String:    BinaryWriter length-prefixed UTF-8 string
  Date:      int32 (day number)
  DateTime:  int64 (UTC ticks) + int16 (offset minutes)
  Time:      int64 (ticks of day)
  Duration:  int64 (ticks)
  Uuid:      byte[16]
  JsonValue: BinaryWriter length-prefixed UTF-8 string
  UInt8Array: int32 length + byte[length]
  Vector:    int32 length + float32[length]
  Matrix:    int32 rows + int32 cols + float32[rows × cols]
  Tensor:    int32 rank + int32[rank] dimensions + float32[∏dims]
  Image:     int32 length + byte[length]
  Array:     recursive DataValue serialization
```

Nullable wrapper: `bool hasValue`, then (if true) the `DataValue` payload.

## Sidecar index (`.datum-index`)

The `.datum` format is intentionally simple — all acceleration structures live in separate sidecar files. The [`.datum-index`](indexes.md) sidecar carries bloom filters, sorted value indexes, B+Tree indexes, bitmap indexes, and chunk-level statistics. The optional [`.datum-mapped-index`](indexes.md#memory-mapped-sorted-indexes) sidecar provides memory-mapped fixed-width sorted indexes designed for zero-copy multi-tenant deployments. This separation means the data file format never changes for index features, indexes can be rebuilt independently, and the same index format works across all source file types (CSV, Parquet, HDF5, ZIP archives, etc.).

The sidecar provides:

| Section | Purpose |
|---------|---------|
| **Fingerprint** | Staleness detection via striped SHA-256 of the source file |
| **Schema** | Cached column names, kinds, nullability, and total row count |
| **Chunk directory** | Per-chunk row ranges with per-column min/max/null/cardinality statistics |
| **Bloom filters** | Per-column, per-chunk Kirsch–Mitzenmacher double-hashed bloom filters |
| **Sorted value indexes** | Per-column sorted arrays enabling binary-search key lookup (Zstd-compressed in v3) |
| **ZIP directory** | Cached central directory for ZIP archive sources |
| **Row offsets** | Byte offsets into the source file for seekable chunk access |

The `.datum-mapped-index` sidecar (format version 4) stores memory-mapped fixed-width sorted indexes in a separate file with magic `DXIX`. See [Memory-mapped sorted indexes](indexes.md#memory-mapped-sorted-indexes) for the binary specification.

See [indexes.md](indexes.md) for the full binary specification of both sidecar formats.

## Optional sidecar (`.datum-blob`)

An optional `.datum-blob` companion file provides a unified sidecar for **Large Binary Objects (LBOs)** — images, byte arrays, and (eventually) video — that would otherwise inflate column pages or fragment into many per-blob files. Unlike the legacy `.datum_blobs/` directory used by [`ExternalBytes`](#encoding-strategies) encoding (one filesystem entry per blob), the `.datum-blob` is a single append-only file containing all sidecar-routed payloads concatenated.

Bytes are addressed by absolute 64-bit file offset, so a single sidecar can hold terabytes of binary data without per-blob or per-file caps. Readers memory-map the entire sidecar and slice it directly — zero copy, zero decompression for already-compressed payloads (JPEG, PNG, MP4, etc.).

### Lazy materialization

The sidecar is created only when a write actually happens. `.datum` files containing only inline-sized data (numbers, short strings, dates) leave no orphan `.datum-blob` artifact. The `.datum` footer carries a sidecar fingerprint reference only when the sidecar was materialized; readers detect this and require the companion file to exist.

### Header layout (32 bytes)

```
[magic       : 8 bytes  "DATUMBLB" little-endian (0x424C424D55544144)]
[version     : 4 bytes  uint32 = 1]
[reserved1   : 4 bytes  zero]
[fingerprint : 8 bytes  uint64 — must match the .datum footer's reference]
[reserved2   : 8 bytes  zero]
[blob bytes  : append-only payload region — concatenated raw bytes]
```

There is no internal framing between blobs in the payload region. Each blob's location is recorded in its referencing column page's pointer table (see *DataValue coordinates* below); the sidecar itself is opaque to anyone not holding the corresponding `.datum`.

### Fingerprint linkage

The fingerprint is a random 64-bit value generated once when a sidecar is first materialized. It is stored both in the sidecar header (offset 16) and in the companion `.datum` footer. Readers compare both before opening the sidecar — a mismatch (file swap, manual edit, stale partial restore) raises a clear error rather than risking silent corruption.

### DataValue coordinates

A `DataValue` referencing a sidecar payload carries:

- **64-bit absolute offset** — the byte position in `.datum-blob` (includes the 32-byte header, so readers slice the mmap directly without offset translation)
- **40-bit length** — payload size, supports per-blob values up to 1 TiB (covers all realistic image, video, tensor, and scientific-array sizes)
- **24 reserved bits** — for future per-value metadata (format ID, codec hint, dimensions, etc.); zero in v1

The encoder for sidecar-routed columns produces a column page consisting of `nullBitmap | (uint64 offset, uint40 length)[N]` — no inline byte pool. Decoders reconstruct `DataValue`s with sidecar coordinates, never copying bytes through arena memory at decode time.

### Concurrency and atomicity

`SidecarWriteStore.Append` serialises concurrent appenders internally; multiple producers (parallel deserializers, threaded ingest) can safely share a single sink without external coordination. On finalize, the sidecar is flushed and closed before the `.datum` footer is written, so a crash mid-finalize leaves either a complete pair of files or an orphaned sidecar (easily detected and discarded — the `.datum` footer's fingerprint reference is the source of truth for "valid pair").

### Relationship to legacy `ExternalBytes` / `.datum_blobs/`

The original [blob externalization](#blob-externalization) mechanism — one sidecar file per externalized blob, written into a `.datum_blobs/` directory — remains the legacy code path and continues to read older `.datum` files unchanged. New writes use the unified `.datum-blob` sidecar; the per-blob-file mechanism will be retired once readers no longer need backwards compatibility with files predating the unified sidecar.

## Reading flow

```
1. Open file
   └─ Seek to EOF − 8, read tail, validate FMTD magic
   └─ Compute footerOffset, seek there, deserialize schema + row group directory

2. Plan query
   └─ Identify relevant columns from SELECT/WHERE/JOIN clauses
   └─ Use zone maps to skip row groups that cannot match predicates
   └─ If sidecar index exists, apply bloom filter and sorted index pruning

3. Read matching row groups
   └─ For each surviving row group:
      └─ For each needed column:
         └─ Seek to pageOffset
         └─ Read compressedByteLength bytes
         └─ Decompress (Zstd/Zlib/Brotli/None)
         └─ Decode (reverse encoding: unshuffle, un-delta, dictionary expand, etc.)
         └─ Yield DataValue[] for this column
```

## Write flow

```
1. Initialize
   └─ Write 28-byte header (with zero placeholders for mutable fields)

2. Buffer rows
   └─ Accumulate DataValue[] per column
   └─ On first flush: freeze fixed shapes (Vector/Matrix/Tensor dimensions)
   └─ When buffer reaches rowGroupSize:
      └─ Encode each column → DatumEncodedPage
      └─ Compress payload → write compressed bytes to stream
      └─ Record DatumColumnChunkDescriptor (offset, sizes, encoding, codec, zone map)
      └─ Auto-tune: halve row group size if any FixedFloat32 page > 32 MiB

3. Finalize
   └─ Flush remaining rows as final row group
   └─ Write footer (schema + row group directory)
   └─ Write 8-byte tail (footer length + FMTD magic)
   └─ Seek back to header, patch RowGroupCount, TotalRowCount, FooterOffset
```

## Source files

| File | Purpose |
|------|---------|
| `DatumFileConstants.cs` | Magic bytes, version, enums (`DatumEncoding`, `DatumCompression`, `DatumColumnFlags`, `DatumFileFlags`) |
| `DatumFileWriter.cs` | Sequential writer with auto-tuning row group size |
| `DatumFileReader.cs` | Footer-first reader with column-selective decode |
| `DatumFileSchema.cs` | Schema serialization/deserialization |
| `DatumColumnDescriptor.cs` | Per-column metadata record |
| `DatumColumnChunkDescriptor.cs` | Per-column, per-row-group page location and zone map |
| `DatumRowGroupDescriptor.cs` | Per-row-group metadata (row count + column chunks) |
| `DatumZoneMap.cs` | Per-column min/max/null-count statistics |
| `DatumCompressor.cs` | Compression/decompression dispatch (Zstd, Zlib, Brotli) |
| `FloatByteShuffle.cs` | BLOSC-style byte-lane interleaving for float pages |
| `DatumFileTableProvider.cs` | Query-engine table provider with seekable chunk access |
| `Sidecar/IBlobSink.cs` | Append-only sink contract (64-bit offset/length) for the `.datum-blob` write path |
| `Sidecar/IBlobSource.cs` | Read-only random-access source contract for the `.datum-blob` read path |
| `Sidecar/SidecarConstants.cs` | `.datum-blob` magic (`DATUMBLB`), version, header layout |
| `Sidecar/SidecarWriteStore.cs` | Lazy-materialised, locked, append-only writer for the `.datum-blob` sidecar |
| `Sidecar/SidecarReadStore.cs` | mmap-backed reader with header + fingerprint validation |
