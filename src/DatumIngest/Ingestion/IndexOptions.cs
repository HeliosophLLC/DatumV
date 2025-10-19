using DatumIngest.Indexing;

namespace DatumIngest.Ingestion;

/// <summary>
/// Column selection policy for <see cref="IndexOptions"/>.
/// </summary>
public abstract record IndexColumnSelection
{
    private IndexColumnSelection() { }

    /// <summary>
    /// Index the columns the builder considers cheap and useful: primitives, dates, UUIDs,
    /// booleans, and short strings. Wide reference types (Image, Vector, Matrix, Tensor,
    /// Array, Struct, JsonValue, UInt8Array) are skipped. This is the default.
    /// </summary>
    public sealed record Auto : IndexColumnSelection;

    /// <summary>Index every column in the schema, including reference types.</summary>
    public sealed record All : IndexColumnSelection;

    /// <summary>Index only the named columns. Ignored columns receive no index.</summary>
    /// <param name="Columns">Column names to index.</param>
    public sealed record Explicit(IReadOnlyList<string> Columns) : IndexColumnSelection;

    /// <summary>Build no column indexes. Zone maps and chunk directory are still produced.</summary>
    public sealed record None : IndexColumnSelection;
}

/// <summary>
/// Memory/throughput knobs for <see cref="Indexer.IndexAsync(DatumIngest.Serialization.DatumFileDescriptor, DatumIngest.Serialization.OutputDescriptor, IndexOptions, System.Threading.CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Defaults are tuned for single-tenant bulk indexing. For multi-tenant servers that serve
/// queries concurrently, use <see cref="MultiTenantServer"/>.
/// </remarks>
public sealed record IndexOptions
{
    /// <summary>
    /// Column selection policy. Defaults to <see cref="IndexColumnSelection.Auto"/>.
    /// </summary>
    public IndexColumnSelection Columns { get; init; } = new IndexColumnSelection.Auto();

    /// <summary>
    /// Number of rows per index chunk. Smaller chunks reduce peak per-chunk accumulator
    /// memory at the cost of a larger chunk directory and more finalization overhead.
    /// </summary>
    public int ChunkSize { get; init; } = IndexConstants.DefaultChunkSize;

    /// <summary>Default options: maximum throughput, highest peak memory.</summary>
    public static IndexOptions Default { get; } = new();

    /// <summary>
    /// Preset tuned for multi-tenant servers. Halves the chunk size to reduce peak per-chunk
    /// working set at the cost of a larger on-disk chunk directory.
    /// </summary>
    public static IndexOptions MultiTenantServer { get; } = new()
    {
        ChunkSize = 5_000,
    };
}
