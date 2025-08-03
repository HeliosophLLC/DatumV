namespace DatumIngest.DatumFile;

/// <summary>
/// Constants, magic bytes, and enumerations for the <c>.datum</c> binary column-store format.
/// </summary>
public static class DatumFileConstants
{
    /// <summary>Magic bytes identifying a datum file: ASCII "DTMF" (DaTuM File).</summary>
    public static ReadOnlySpan<byte> Magic => "DTMF"u8;

    /// <summary>
    /// Tail sentinel enabling reverse-seek open: ASCII "FMTD".
    /// Stored in the last 4 bytes of every file so readers can locate the footer
    /// without scanning forward from the beginning.
    /// </summary>
    public static ReadOnlySpan<byte> TailMagic => "FMTD"u8;

    /// <summary>Current format version. Incremented only on backwards-incompatible changes.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>
    /// Size of the fixed file header in bytes.
    /// Layout: magic(4) + version(2) + flags(2) + rowGroupCount(4) + totalRowCount(8) + footerOffset(8) = 28.
    /// </summary>
    public const int HeaderSize = 28;

    /// <summary>
    /// Size of the fixed file tail in bytes: footerByteLength(4) + tailMagic(4) = 8.
    /// Readers seek to <c>fileLength - TailSize</c> to read the footer offset without a forward scan.
    /// </summary>
    public const int TailSize = 8;

    /// <summary>
    /// Byte offset of the <c>footerOffset</c> field within the file header.
    /// The writer patches this field after the footer has been written.
    /// </summary>
    public const int FooterOffsetPosition = 20;

    /// <summary>Default number of rows buffered per row group before flushing a page.</summary>
    public const int DefaultRowGroupSize = 65_536;

    /// <summary>
    /// The writer will not reduce the row group size below this floor even when
    /// the auto-tuner detects oversized float pages.
    /// </summary>
    public const int MinimumRowGroupSize = 512;

    /// <summary>
    /// When any FixedFloat32 column decompressed page would exceed this many bytes,
    /// the writer halves the row group size for all subsequent row groups.
    /// 32 MiB keeps one float column row group comfortably within typical L3 budgets.
    /// </summary>
    public const int LargePageAutoTuneThresholdBytes = 33_554_432;

    /// <summary>
    /// Default blob externalization threshold in bytes. When the largest blob in a row group
    /// exceeds this value, the entire column page for that row group is externalized to sidecar files
    /// and the page stores relative path strings instead of raw bytes.
    /// </summary>
    public const long DefaultExternalizationThresholdBytes = 1_048_576;

    /// <summary>Directory name appended to the datum file path for externalized blob storage.</summary>
    public const string BlobsFolderSuffix = ".datum_blobs";

    /// <summary>Default Zstd compression level. Level 3 balances speed and ratio well for ETL workloads.</summary>
    public const int DefaultZstdCompressionLevel = 3;
}

/// <summary>
/// Column page encoding strategies stored as a single byte in each page header.
/// A column is not bound to one encoding for its lifetime — the encoding byte
/// per page allows the writer to change strategy (e.g., promote to dictionary)
/// between row groups.
/// </summary>
public enum DatumEncoding : byte
{
    /// <summary>Raw unencoded bytes. Used for Scalar float arrays, UInt8 byte arrays, and Uuid (16 bytes each).</summary>
    Raw = 0,

    /// <summary>
    /// Truth and null values both packed as bit vectors. Two bitmaps of <c>ceil(N/8)</c> bytes each:
    /// the null bitmap followed by the value bitmap. Used exclusively for Boolean.
    /// </summary>
    BitPacked = 1,

    /// <summary>
    /// Delta-encoded <c>int32</c> array relative to the first non-null value in the page.
    /// Used for Date (stored as integer day numbers).
    /// </summary>
    DeltaInt32 = 2,

    /// <summary>
    /// Delta-encoded <c>int64</c> array relative to the first non-null value in the page.
    /// Used for DateTime (UTC ticks), Time (ticks of day), and Duration (ticks).
    /// DateTime pages include a secondary <c>int16[N]</c> timezone offset array after the delta array.
    /// </summary>
    DeltaInt64 = 3,

    /// <summary>
    /// Dense packed <c>float32</c> array with a null bitmap prefix. Shape is fixed per column in the schema.
    /// Applied to Vector, Matrix, Tensor, and Scalar columns.
    /// A byte-shuffle pre-filter (BLOSC-style lane interleaving) is applied before Zstd compression.
    /// Null rows store <c>float.NaN</c> in the array so element offsets remain implicit.
    /// </summary>
    FixedFloat32 = 4,

    /// <summary>
    /// Variable-length byte sequences preceded by a <c>uint32</c> offset table of <c>N + 1</c> entries.
    /// <c>offset[i]</c> is the byte position of row <c>i</c>; <c>offset[N]</c> is the total pool length.
    /// Null rows have <c>offset[i] == offset[i+1]</c> and their null bitmap bit set; empty strings
    /// share the same zero-length range but have their null bit clear.
    /// Used for String, JsonValue, UInt8Array (embedded), and Image (embedded or externalized paths).
    /// </summary>
    VariableBytes = 5,

    /// <summary>
    /// Serialized <see cref="DatumIngest.Model.DataValue"/> elements with a <c>uint32</c> offset table.
    /// Used for the heterogeneous <see cref="DatumIngest.Model.DataKind.Array"/> kind.
    /// Each element is serialized using the same wire format as <c>IndexWriter.WriteDataValue</c>.
    /// </summary>
    VariableDataValue = 6,

    /// <summary>
    /// Dictionary-compressed column. An in-page dictionary precedes a compact code array.
    /// Codes are <c>byte</c> when unique count ≤ 255 (sentinel 0xFF = null) or <c>uint16</c> when ≤ 65 535 (0xFFFF = null).
    /// Applied to low-cardinality String, JsonValue, and Scalar columns.
    /// </summary>
    DictionaryRLE = 7,

    /// <summary>
    /// Externalized binary data. Same offset/pool layout as <see cref="VariableBytes"/>, but the pool
    /// contains relative UTF-8 path strings pointing to sidecar files rather than raw binary bytes.
    /// Used by <see cref="DatumIngest.Model.DataKind.Image"/> and
    /// <see cref="DatumIngest.Model.DataKind.UInt8Array"/> column pages when any blob in the row group
    /// exceeds the column's externalization threshold.
    /// </summary>
    ExternalBytes = 8,
}

/// <summary>
/// Compression algorithm applied to a column page payload after encoding.
/// Stored as a single byte in the page header, allowing per-page codec selection.
/// </summary>
public enum DatumCompression : byte
{
    /// <summary>
    /// No compression. Applied to Image and UInt8Array blobs that are themselves
    /// already compressed (JPEG, PNG, WebP, etc.) to avoid expansion.
    /// </summary>
    None = 0,

    /// <summary>
    /// Zstd compression via ZstdSharp.Port.
    /// Default for all column types except already-compressed blobs.
    /// </summary>
    Zstd = 1,

    /// <summary>Deflate (raw zlib) via <c>System.IO.Compression.DeflateStream</c>. BCL-only fallback.</summary>
    Zlib = 2,

    /// <summary>Brotli via <c>System.IO.Compression.BrotliEncoder</c>. High ratio, slow encode.</summary>
    Brotli = 3,
}

/// <summary>
/// File-level flags stored in the fixed header at byte offset 6 (after magic + version).
/// </summary>
[Flags]
public enum DatumFileFlags : ushort
{
    /// <summary>No special flags.</summary>
    None = 0,

    /// <summary>One or more column pages use <see cref="DatumEncoding.DictionaryRLE"/>.</summary>
    HasDictionaryPages = 0x01,

    /// <summary>Zone maps are embedded in the row group directory. Always set by this writer.</summary>
    HasZoneMaps = 0x02,
}

/// <summary>
/// Column-level flags stored as a single byte in the schema section of the file footer.
/// </summary>
[Flags]
public enum DatumColumnFlags : byte
{
    /// <summary>No special flags.</summary>
    None = 0,

    /// <summary>This column may contain null values.</summary>
    Nullable = 0x01,

    /// <summary>
    /// Values in this column have a fixed shape recorded in the schema footer.
    /// Applies to Vector, Matrix, and Tensor columns. When set, the shape dimensions
    /// follow the flags byte in the binary schema block.
    /// </summary>
    FixedShape = 0x02,

    /// <summary>This column has been promoted to dictionary encoding and is eligible for it in future row groups.</summary>
    DictionaryEligible = 0x04,

    /// <summary>
    /// Oversized blobs in this column are externalized to sidecar files.
    /// Pages store UTF-8 relative path strings instead of raw binary content.
    /// </summary>
    ExternBlobs = 0x08,
}
