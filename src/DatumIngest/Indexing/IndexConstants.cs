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
    /// Per-flow override for the index-build chunk size, scoped via
    /// <see cref="AsyncLocal{T}"/> so concurrent tests don't race on it.
    /// Production code paths leave this unset and fall through to
    /// <see cref="DefaultChunkSize"/>. Tests can lower it (via
    /// <see cref="OverrideChunkSizeForTest"/>) to force chunk-boundary
    /// behavior without inserting millions of rows.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<int?> _chunkSizeOverride = new();

    /// <summary>
    /// Returns the chunk size that should be used by index builders in the
    /// current async flow: the test-scoped override if set, otherwise
    /// <see cref="DefaultChunkSize"/>.
    /// </summary>
    public static int EffectiveChunkSize => _chunkSizeOverride.Value ?? DefaultChunkSize;

    /// <summary>
    /// Test-only: sets the index-build chunk size for the current async
    /// flow. Returns a disposable that restores the previous value.
    /// </summary>
    /// <example>
    /// <code>
    /// using (IndexConstants.OverrideChunkSizeForTest(100))
    /// {
    ///     // inserts here split into chunks of 100 rows
    /// }
    /// </code>
    /// </example>
    public static IDisposable OverrideChunkSizeForTest(int chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        int? previous = _chunkSizeOverride.Value;
        _chunkSizeOverride.Value = chunkSize;
        return new RestoreOnDispose(previous);
    }

    private sealed class RestoreOnDispose : IDisposable
    {
        private readonly int? _previous;
        internal RestoreOnDispose(int? previous) => _previous = previous;
        public void Dispose() => _chunkSizeOverride.Value = _previous;
    }

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
