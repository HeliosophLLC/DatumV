using DatumIngest.Indexing;

namespace DatumIngest;

/// <summary>
/// Configuration options for <see cref="DatumIngester"/>.
/// </summary>
public sealed class DatumIngesterOptions
{
    /// <summary>
    /// Default options: all columns sorted-indexed, no bloom filters, default chunk size.
    /// </summary>
    public static readonly DatumIngesterOptions Default = new();

    /// <summary>
    /// Number of rows per index chunk.
    /// Smaller values allow finer-grained chunk pruning at query time;
    /// larger values reduce index size.
    /// Defaults to <see cref="IndexConstants.DefaultChunkSize"/>.
    /// </summary>
    public int ChunkSize { get; init; } = IndexConstants.DefaultChunkSize;

    /// <summary>
    /// When <c>true</c>, builds bloom filters for every column.
    /// Useful for high-cardinality string or UUID membership tests.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool BloomAllColumns { get; init; } = false;

    /// <summary>
    /// When <c>true</c>, builds a sorted value index for every column,
    /// enabling O(log n) key lookup and range pruning at query time.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool IndexAllColumns { get; init; } = true;
}
