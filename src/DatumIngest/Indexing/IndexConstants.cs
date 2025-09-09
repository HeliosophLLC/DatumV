namespace DatumIngest.Indexing;

/// <summary>
/// Shared numeric constants for index building, fingerprinting, and automatic
/// index-type selection. These values are independent of any particular on-disk
/// format version and are referenced by the build pipeline and query planner.
/// </summary>
public static class IndexConstants
{
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

    /// <summary>
    /// Entry count threshold for automatic B+Tree promotion. Columns with more
    /// entries than this value use a B+Tree index instead of a sorted value index.
    /// </summary>
    public const long BPlusTreeAutoThreshold = 5_000_000;

    /// <summary>
    /// Estimated cardinality ceiling for automatic bitmap index selection. Columns with
    /// at most this many distinct values use bitmap indexes instead of sorted or B+Tree.
    /// </summary>
    public const int BitmapAutoThreshold = 256;
}
