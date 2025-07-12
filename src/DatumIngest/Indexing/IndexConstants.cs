namespace DatumIngest.Indexing;

/// <summary>
/// Constants and enumerations for the <c>.datum-index</c> binary file format.
/// The format uses a table-of-contents at the end of the file (like ZIP central directory)
/// to enable sequential writing and random-access reading.
/// </summary>
public static class IndexConstants
{
    /// <summary>Magic bytes identifying a datum-index file: ASCII "DTIX".</summary>
    public static ReadOnlySpan<byte> Magic => "DTIX"u8;

    /// <summary>Current format version.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>Size of the fixed file header in bytes (magic + version + flags + TOC offset).</summary>
    public const int HeaderSize = 16;

    /// <summary>
    /// Size of each 64 KiB sample read during fingerprint computation.
    /// </summary>
    public const int FingerprintSampleSize = 65_536;

    /// <summary>
    /// Byte interval between fingerprint samples (10 MiB).
    /// </summary>
    public const long FingerprintSampleInterval = 10 * 1024 * 1024;

    /// <summary>Default number of rows per index chunk.</summary>
    public const int DefaultChunkSize = 10_000;
}

/// <summary>
/// Identifies the type of a section within a <c>.datum-index</c> file.
/// Each section is a contiguous block of bytes located via the table of contents.
/// </summary>
public enum IndexSectionType : byte
{
    /// <summary>Source file fingerprint (size + striped hash) for staleness detection.</summary>
    Fingerprint = 0,

    /// <summary>Cached schema (column names, kinds, nullability) and total row count.</summary>
    Schema = 1,

    /// <summary>Chunk boundaries with per-column min/max/null/cardinality statistics.</summary>
    ChunkDirectory = 2,

    /// <summary>Per-column, per-chunk bloom filters for membership testing.</summary>
    BloomFilters = 3,

    /// <summary>Per-column sorted value arrays for binary-search key lookup.</summary>
    SortedIndexes = 4,

    /// <summary>Cached ZIP central directory entries.</summary>
    ZipDirectory = 5,

    /// <summary>Per-chunk byte offsets into the source file for seekable providers.</summary>
    RowOffsets = 6,
}
