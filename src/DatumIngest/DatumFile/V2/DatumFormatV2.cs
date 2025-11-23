namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Constants and enumerations for the <c>.datum</c> format v2 — the
/// uncompressed columnar format spec'd in
/// <c>project_datum_format_v2.md</c>. Designed for mmap zero-copy reads
/// and a tiny encoder/decoder surface (three encoders only). v1 is
/// dropped wholesale on cutover.
/// </summary>
public static class DatumFormatV2
{
    /// <summary>Magic bytes identifying a v2 datum file: ASCII "DTMF" (unchanged from v1).</summary>
    public static ReadOnlySpan<byte> Magic => "DTMF"u8;

    /// <summary>
    /// Tail sentinel enabling reverse-seek open: ASCII "FMTD". Stored in
    /// the last 4 bytes of every file so readers can locate the footer
    /// without scanning forward from the beginning.
    /// </summary>
    public static ReadOnlySpan<byte> TailMagic => "FMTD"u8;

    /// <summary>
    /// Format version. Bumped past the existing v2-in-code (which was
    /// itself v1-in-docs and is being replaced wholesale) to clearly
    /// signal incompatibility — readers of the old format will see this
    /// version and refuse rather than mis-interpret bytes.
    /// </summary>
    public const ushort FormatVersion = 3;

    /// <summary>
    /// Fixed-size file header in bytes:
    /// magic(4) + version(2) + flags(2) + columnCount(4) +
    /// pageSize(4) + totalRowCount(8) + footerOffset(8) = 32.
    /// </summary>
    public const int HeaderSize = 32;

    /// <summary>
    /// Fixed-size file tail in bytes: footerByteLength(4) + tailMagic(4) = 8.
    /// </summary>
    public const int TailSize = 8;

    /// <summary>
    /// Byte offset of the <c>totalRowCount</c> field within the header.
    /// Patched on finalize.
    /// </summary>
    public const int TotalRowCountFieldOffset = 16;

    /// <summary>
    /// Byte offset of the <c>footerOffset</c> field within the header.
    /// Patched on finalize.
    /// </summary>
    public const int FooterOffsetFieldOffset = 24;

    /// <summary>
    /// Default rows per column page. Locked at 1024 to align with
    /// <c>ExecutionContext.BatchSize</c>: one page = one batch, no
    /// aggregation or splitting on read.
    /// </summary>
    public const int DefaultPageSize = 1024;

    /// <summary>
    /// Pages per chapter. Chapters aggregate page-level zone maps for
    /// coarser predicate pruning and serve as the chunk grain for
    /// <c>.datum-index</c> structures. 64 pages × 1024 rows/page = 64 K
    /// rows per chapter, matching the old v1 row-group grain so
    /// index-pruning selectivity is preserved.
    /// </summary>
    public const int PagesPerChapter = 64;

    /// <summary>
    /// Chapters per volume. Volumes aggregate chapter zone maps for the
    /// coarsest level of predicate pruning. 16 chapters × 64 K rows =
    /// 1 M rows per volume. Volume zone maps are only emitted when the
    /// total row count exceeds <see cref="VolumeEmitRowThreshold"/>;
    /// below that, page+chapter pruning is sufficient.
    /// </summary>
    public const int ChaptersPerVolume = 16;

    /// <summary>
    /// Below this row count, volume-level zone maps are not emitted —
    /// the chapter level already covers the file. 1 M rows = the
    /// natural break point where a volume tier starts adding value.
    /// </summary>
    public const long VolumeEmitRowThreshold = 1_000_000;

    /// <summary>
    /// Width in bytes of each VariableSlot cell. The slot is interpreted
    /// per-row as either an inline payload (DataValue's <c>_p0</c>-<c>_p3</c>
    /// region, 16 bytes) or a sidecar pointer (offset(8) + length(5) +
    /// reserved(2) + codec(1) = 16 bytes). The inline-vs-pointer bitmap
    /// in the page header decides per row.
    /// </summary>
    public const int VariableSlotBytes = 16;

    /// <summary>
    /// Sidecar pointer slot byte offsets — a sidecar pointer cell of
    /// the variable slot decomposes as documented here.
    /// </summary>
    public static class PointerSlot
    {
        /// <summary>Bytes 0-7 (8 bytes): absolute byte offset into <c>.datum-blob</c>.</summary>
        public const int OffsetField = 0;

        /// <summary>Bytes 8-12 (5 bytes): payload length, max 1 TiB (40-bit).</summary>
        public const int LengthField = 8;

        /// <summary>Bytes 13-14 (2 bytes): reserved for length-field expansion past 1 TiB.</summary>
        public const int ReservedField = 13;

        /// <summary>Byte 15 (1 byte): codec identifier (see <see cref="SidecarBlobCodec"/>).</summary>
        public const int CodecField = 15;
    }
}

/// <summary>
/// Per-column page encoder identifier, written once per column in the
/// schema footer and used by readers to dispatch decode. The set is
/// intentionally small (three values) — picking the encoder is a
/// function of <see cref="Model.DataKind"/> alone, decided at write
/// time and stable for the column's lifetime.
/// </summary>
public enum EncoderKind : byte
{
    /// <summary>
    /// Fixed-stride scalar payloads. One payload-bytes-wide cell per row,
    /// preceded by a null bitmap. Stride is determined by
    /// <see cref="Model.DataKind"/>: 1 (Int8/UInt8), 2 (Int16/UInt16),
    /// 4 (Int32/UInt32/Float32/Date), 8 (Int64/UInt64/Float64/Time/Duration),
    /// 10 (DateTime — int64 ticks + int16 offset minutes), 16 (Uuid).
    /// Null cells store zero; the bitmap is authoritative.
    /// </summary>
    FixedWidth = 0,

    /// <summary>
    /// Booleans only. Two parallel bitmaps in the page: null bitmap then
    /// value bitmap. ~256 bytes for a 1024-row page (vs 1024 bytes for
    /// raw byte-per-row). The 8× reduction over fixed-width is the
    /// reason this is its own encoder rather than a FixedWidth special
    /// case.
    /// </summary>
    BitPackedBoolean = 1,

    /// <summary>
    /// Variable-length kinds: String, Array, Image, Vector, Struct,
    /// byte arrays, typed arrays. Each row gets a fixed
    /// 16-byte slot in the page; an inline-vs-pointer bitmap (one bit
    /// per row) tells the decoder whether the slot bytes ARE the
    /// payload (DataValue's inline tier) or a sidecar pointer
    /// (offset/length/codec into <c>.datum-blob</c>). Page layout:
    /// null bitmap + inline bitmap + 16 × rowCount bytes.
    /// </summary>
    VariableSlot = 2,
}

/// <summary>
/// Sidecar blob compression codec, written into each variable-slot
/// pointer cell at byte offset 15. v1 only ships <see cref="Raw"/>;
/// the other values are reserved for forward-compatible v2.x evolution.
/// </summary>
/// <remarks>
/// Unlike v1's <c>DatumCompression</c> enum which lived per-column-page,
/// the codec lives per-blob in v2 — every blob in the sidecar can pick
/// its own compression independently. Today's writer always picks Raw.
/// </remarks>
public enum SidecarBlobCodec : byte
{
    /// <summary>Stored as raw bytes, no compression. Only legal value in v1.</summary>
    Raw = 0,

    /// <summary>Reserved for v2.x: Zstd-compressed blob.</summary>
    Zstd = 1,

    /// <summary>Reserved for v2.x: byte-shuffle pre-filter + Zstd. Intended for Vector/Matrix/Tensor.</summary>
    ZstdShuffle = 2,
}

/// <summary>
/// File-level flags stored in the header. Reserved for forward-compatible
/// v2.x evolution; v1 always writes <see cref="None"/>.
/// </summary>
[Flags]
public enum DatumFileFlagsV2 : ushort
{
    /// <summary>No special flags.</summary>
    None = 0,

    /// <summary>
    /// At least one column has a sidecar reference, so the companion
    /// <c>.datum-blob</c> sidecar is required for reads. Reader will
    /// refuse to open the file without the sidecar present.
    /// </summary>
    HasSidecarReferences = 0x01,

    /// <summary>
    /// Volume-level zone maps were emitted (file row count exceeded
    /// <see cref="DatumFormatV2.VolumeEmitRowThreshold"/>). When clear,
    /// volume-level pruning is unavailable and ScanOperator falls
    /// back to chapter+page pruning.
    /// </summary>
    HasVolumeZoneMaps = 0x02,
}
