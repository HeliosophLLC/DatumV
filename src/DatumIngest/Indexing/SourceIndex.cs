namespace DatumIngest.Indexing;

/// <summary>
/// In-memory representation of a <c>.datum-index</c> file. Aggregates the fingerprint,
/// cached schema, chunk directory, and optional acceleration structures (bloom filters,
/// sorted value indexes, ZIP directory cache). Constructed by <see cref="SourceIndexBuilder"/>
/// and serialized/deserialized by <see cref="IndexWriter"/>/<see cref="IndexReader"/>.
/// </summary>
public sealed class SourceIndex
{
    /// <summary>Source file fingerprint for staleness detection.</summary>
    public SourceFingerprint Fingerprint { get; }

    /// <summary>Cached schema and total row count.</summary>
    public IndexSchema Schema { get; }

    /// <summary>Ordered list of chunks with per-column statistics.</summary>
    public IReadOnlyList<IndexChunk> Chunks { get; }

    /// <summary>
    /// Per-column, per-chunk bloom filters for membership testing,
    /// or <c>null</c> if bloom filters were not built.
    /// </summary>
    public BloomFilterSet? BloomFilters { get; }

    /// <summary>
    /// Creates a new source index.
    /// </summary>
    /// <param name="fingerprint">Source file fingerprint.</param>
    /// <param name="schema">Cached schema and row count.</param>
    /// <param name="chunks">Ordered list of row chunks with column statistics.</param>
    /// <param name="bloomFilters">Optional bloom filter set for membership testing.</param>
    public SourceIndex(
        SourceFingerprint fingerprint,
        IndexSchema schema,
        IReadOnlyList<IndexChunk> chunks,
        BloomFilterSet? bloomFilters = null)
    {
        Fingerprint = fingerprint;
        Schema = schema;
        Chunks = chunks;
        BloomFilters = bloomFilters;
    }
}
