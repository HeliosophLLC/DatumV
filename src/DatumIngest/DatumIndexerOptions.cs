using DatumIngest.Diagnostics;
using DatumIngest.Indexing;

namespace DatumIngest;

/// <summary>
/// Configuration options for <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, Action{IndexingProgress}?, CancellationToken)"/>.
/// </summary>
public sealed class DatumIndexerOptions
{
    /// <summary>
    /// Default options: auto-indexed compact columns, no bloom filters, default chunk size.
    /// </summary>
    public static readonly DatumIndexerOptions Default = new();

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
    /// Overrides <see cref="AutoIndexColumns"/> when set.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool IndexAllColumns { get; init; } = false;

    /// <summary>
    /// When <c>true</c>, automatically selects columns for sorted value indexes
    /// based on their <see cref="Model.DataKind"/>. Compact types (numeric, date,
    /// boolean, UUID, and short strings up to 16 characters) are indexed; wide types
    /// (vectors, matrices, tensors, images, JSON, arrays) are not.
    /// Has no effect when <see cref="IndexAllColumns"/> is <c>true</c>.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool AutoIndexColumns { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, compresses sorted index sections in the <c>.datum-index</c> file
    /// using Zstd. Sorted indexes are the dominant cost in index size for wide tables
    /// with many rows; compression typically achieves 5–10× reduction with negligible
    /// impact on read latency (decompression is sub-millisecond).
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool CompressIndexes { get; init; } = true;

    /// <summary>
    /// Maximum number of columns to include in the sorted value index.
    /// When set, auto-index selects the first N eligible
    /// columns (in schema order). Useful in multi-tenant environments where index size
    /// must be bounded without disabling indexing entirely.
    /// <see langword="null"/> means no limit (all eligible columns are indexed).
    /// Defaults to <see langword="null"/>.
    /// </summary>
    public int? MaxIndexedColumns { get; init; }

    /// <summary>
    /// Controls which index implementation is used for each column.
    /// <see cref="Indexing.IndexStrategy.Auto"/> selects the implementation per column
    /// based on entry count: small columns use flat sorted arrays, large columns use
    /// disk-resident B+Trees. <see cref="Indexing.IndexStrategy.Sorted"/> forces sorted
    /// arrays for all columns. <see cref="Indexing.IndexStrategy.BTree"/> forces B+Trees
    /// for all columns, which is useful for testing the B+Tree path on small datasets.
    /// Defaults to <see cref="Indexing.IndexStrategy.Auto"/>.
    /// </summary>
    public IndexStrategy IndexStrategy { get; init; } = IndexStrategy.Auto;

    /// <summary>
    /// Optional diagnostic callback invoked at key lifecycle points during index building:
    /// chunk flushes, scan completion, and index writing. When <c>null</c> (default),
    /// no diagnostic events are emitted and there is no overhead on hot paths.
    /// </summary>
    public Action<IndexingDiagnosticEvent>? Diagnostics { get; init; }
}
