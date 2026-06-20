namespace Heliosoph.DatumV.DatumFile.V2;

/// <summary>
/// Constants and enumerations for the <c>.datum</c> format. Designed for
/// mmap zero-copy reads and a tiny encoder/decoder surface (three
/// encoders only).
/// </summary>
/// <remarks>
/// The "V2" in the type names refers to the second-generation engine
/// rewrite, not the on-disk format version. The on-disk
/// <see cref="FormatVersion"/> is currently <c>4</c>: v3 added the
/// streaming three-encoder layout; v4 added forward-compat hooks for
/// crash-safe append, soft mutation (drop column / delete rows),
/// cross-file page references, and multi-writer concurrency. Most v4
/// fields are passive (zero-valued) today and become load-bearing in
/// follow-up PRs.
/// </remarks>
public static class DatumFormatV2
{
    /// <summary>Magic bytes identifying a datum file: ASCII "DTMF" (unchanged from v1).</summary>
    public static ReadOnlySpan<byte> Magic => "DTMF"u8;

    /// <summary>
    /// Tail sentinel enabling reverse-seek open: ASCII "FMTD". Stored in
    /// the last 4 bytes of every file so readers can locate the footer
    /// without scanning forward from the beginning.
    /// </summary>
    public static ReadOnlySpan<byte> TailMagic => "FMTD"u8;

    /// <summary>
    /// Format version. v4 added the footer prologue (generation,
    /// writerId, baseGeneration, tombstone granularity, file table,
    /// chapter tombstone offset table), the <c>fileId</c> field on
    /// <see cref="PageDescriptorV2"/>, and the
    /// <see cref="ColumnFlagsV2.Tombstoned"/> bit. v5 added the
    /// per-file struct type table (descriptor blobs in the sidecar +
    /// directory in the footer) and the per-Struct-column
    /// <c>StructTypeId</c> field gated by
    /// <see cref="ColumnFlagsV2.HasStructTypeId"/>. v6 added the
    /// per-file <c>GENERATED ALWAYS AS</c> computed-columns block gated
    /// by <see cref="DatumFileFlagsV2.HasColumnComputeds"/>. v7 widened
    /// the per-column flags field from 1 byte to 4 bytes, replaced the
    /// per-page <c>HasNullBitmap</c> bool with a 2-byte
    /// <see cref="PageFlagsV7"/> word (with bit 0 carrying
    /// <c>HasNullBitmap</c> and bit 1 gating an optional per-page CRC32C),
    /// and reserved a prologue extensions TLV block gated by
    /// <see cref="DatumFileFlagsV2.HasPrologueExtensions"/>. Writer
    /// always emits the latest version; reader accepts any version in
    /// the range [<see cref="MinReadableFormatVersion"/>,
    /// <see cref="FormatVersion"/>].
    /// </summary>
    public const ushort FormatVersion = 7;

    /// <summary>
    /// Defensive floor: the oldest format version this reader will even
    /// attempt to parse. Set to 7 because pre-v7 files have a different
    /// header layout (no <see cref="HeaderV2.MinReaderVersion"/> field,
    /// wider <c>PageSize</c> encoding) and would mis-parse if read
    /// through the current code path.
    /// </summary>
    /// <remarks>
    /// The cooperative accept/reject gate is
    /// <see cref="HeaderV2.MinReaderVersion"/>, not this constant — a
    /// future v8/v9 file can stamp a low <c>MinReaderVersion</c> and be
    /// readable by today's binary, as long as it only used purely-additive
    /// features (new prologue extension tags, new flag-gated
    /// end-of-footer blocks). This floor exists purely to fail fast on
    /// truly-incompatible historical files.
    /// </remarks>
    public const ushort MinReadableFormatVersion = 7;

    /// <summary>
    /// Pinned tombstone granularity for v4. Value <c>1</c> means
    /// chapter-level tombstone bitmaps (one 8 KiB block per
    /// <see cref="PagesPerChapter"/>×<see cref="DefaultPageSize"/> rows).
    /// Value <c>0</c> is reserved for a future page-level granularity if
    /// it ever proves justified; the writer always emits <c>1</c> today
    /// and the reader rejects other values.
    /// </summary>
    public const byte TombstoneGranularityChapter = 1;

    /// <summary>
    /// Size in bytes of the chapter tombstone bitmap. 64 K rows ÷ 8 bits
    /// = 8 KiB per chapter. Each bit corresponds to one logical row in
    /// the chapter; bit <c>0</c> = row <c>chapterIndex × 65536 + 0</c>.
    /// </summary>
    public const int ChapterTombstoneBlockBytes = 8 * 1024;

    /// <summary>
    /// Sentinel offset value in the per-chapter tombstone offset table
    /// meaning "no tombstone bitmap allocated for this chapter — treat
    /// every row as alive." Avoids writing 8 KiB of zeros for chapters
    /// that haven't been touched by any delete.
    /// </summary>
    public const long NoTombstoneBlock = -1L;

    /// <summary>
    /// Sentinel <c>fileId</c> meaning "this page lives in the primary
    /// <c>.datum</c> file." Any non-zero value indexes into the footer
    /// prologue's file table for cross-file (post-compaction) pages.
    /// Always <c>0</c> in v4 PR1 — the file table stays empty until
    /// cross-file rewrite ships.
    /// </summary>
    public const ushort LocalFileId = 0;

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
/// File-level flags stored in the header. Reader ignores unknown bits
/// rather than failing — additive flag-bit allocation is a
/// forward-compatible operation.
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

    /// <summary>
    /// At least one <see cref="PageDescriptorV2.FileId"/> is non-zero —
    /// some pages live in external <c>.datum-pack</c> files referenced
    /// from the footer prologue's file table. Lets the open path
    /// fast-skip file-table parsing on the common single-file case.
    /// Always clear in v4 PR1 (cross-file pages aren't produced yet).
    /// </summary>
    HasExternalPages = 0x04,

    /// <summary>
    /// At least one chapter has a non-empty tombstone bitmap (some rows
    /// have been soft-deleted). Lets the read path skip tombstone-mask
    /// resolution when clear. Always clear in v4 PR1 (delete API ships
    /// in PR5).
    /// </summary>
    HasTombstones = 0x08,

    /// <summary>
    /// File carries a per-file struct type table (v5+). When set, the
    /// footer ends with a <c>(typeTableEntryCount, entries[])</c> block
    /// that maps on-disk struct type-ids to descriptor blobs in the
    /// sidecar; readers load it into the per-query <see cref="Model.TypeRegistry"/>
    /// at file open and register the on-disk → runtime translation on
    /// the <see cref="DatumFile.Sidecar.SidecarRegistry"/>. Clear in v4
    /// files and in v5 files with no Struct columns.
    /// </summary>
    HasTypeTable = 0x10,

    /// <summary>
    /// File carries one or more <c>GENERATED ALWAYS AS</c> computed columns
    /// (v6+). When set, the footer prologue's trailing
    /// <c>(columnComputedCount, ColumnComputedV4[])</c> block enumerates each
    /// computed column's SQL fragment; the catalog re-parses it on open and
    /// the INSERT/UPDATE paths evaluate the expression per row instead of
    /// accepting an explicit value. Clear in pre-v6 files and in v6 files
    /// with no computed columns.
    /// </summary>
    HasColumnComputeds = 0x20,

    /// <summary>
    /// Footer prologue carries a trailing extensions TLV block (v7+).
    /// When set, the prologue ends with a length-prefixed list of
    /// <c>(uint16 tag, uint32 length, byte[length] payload)</c> entries
    /// — the reserved escape hatch for future file-level scalars that
    /// don't warrant their own flag bit (collation ids, tenant ids,
    /// schema-evolution log offsets). Readers ignore unknown tags so
    /// adding new extension entries is a forward-compatible operation.
    /// Clear in v7 files that ship no extensions; the empty-block
    /// version of the layout is never emitted to save bytes.
    /// </summary>
    HasPrologueExtensions = 0x40,
}

/// <summary>
/// Per-page flag word serialized into <see cref="PageDescriptorV2"/>
/// from v7 onward, replacing the single <c>bool HasNullBitmap</c> field
/// used by v4-v6. Sixteen bits of room for future per-page metadata
/// (CRC presence, per-page codec, encryption marker, compaction
/// generation, partial-page marker). Readers ignore unknown bits.
/// </summary>
[Flags]
public enum PageFlagsV7 : ushort
{
    /// <summary>No per-page flags set.</summary>
    None = 0,

    /// <summary>
    /// Page payload is preceded by a per-row null bitmap. Carried per-page
    /// rather than per-column so <c>ALTER … DROP NOT NULL</c> can leave
    /// historical pages without bitmaps while new pages flush with them.
    /// </summary>
    HasNullBitmap = 0x0001,

    /// <summary>
    /// Page descriptor carries a trailing <c>uint32</c> CRC32C computed
    /// over the page bytes at flush time. Lets readers detect bit-rot at
    /// decode time without scanning the whole file. Optional today; future
    /// writers may emit it unconditionally.
    /// </summary>
    HasPageCrc = 0x0002,
}
