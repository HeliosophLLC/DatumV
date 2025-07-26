namespace DatumIngest.Indexing;

/// <summary>
/// Container for per-sub-table source indexes within a single <c>.datum-index</c> file.
/// The fingerprint is shared across all tables (one source file), while each table has
/// its own schema, chunk directory, and optional acceleration structures.
/// </summary>
/// <remarks>
/// <para>
/// Single-table sources use an empty-string key in <see cref="Tables"/>.
/// Multi-table sources (e.g. a JSON file with multiple array properties) use the
/// sub-table qualifier as the key (the leaf property name).
/// </para>
/// <para>
/// Use <see cref="Create"/> to wrap a single <see cref="SourceIndex"/> with the
/// default empty key, extracting the shared fingerprint automatically.
/// </para>
/// </remarks>
public sealed class SourceIndexSet
{
    /// <summary>
    /// Gets the source file fingerprint shared by all tables.
    /// Used for staleness detection — any change to the source file
    /// invalidates all sub-table indexes.
    /// </summary>
    public SourceFingerprint Fingerprint { get; }

    /// <summary>
    /// Gets the per-sub-table source indexes, keyed by sub-table qualifier.
    /// An empty string key represents the sole or default table in the source file.
    /// </summary>
    public IReadOnlyDictionary<string, SourceIndex> Tables { get; }

    /// <summary>
    /// Creates a new source index set.
    /// </summary>
    /// <param name="fingerprint">Shared source file fingerprint.</param>
    /// <param name="tables">Per-sub-table source indexes keyed by qualifier.</param>
    public SourceIndexSet(SourceFingerprint fingerprint, IReadOnlyDictionary<string, SourceIndex> tables)
    {
        Fingerprint = fingerprint;
        Tables = tables;
    }

    /// <summary>
    /// Creates a <see cref="SourceIndexSet"/> wrapping a single <see cref="SourceIndex"/>
    /// with an empty-string key. The fingerprint is extracted from the index.
    /// </summary>
    /// <param name="index">The single-table source index.</param>
    /// <returns>A new source index set with one entry keyed by <c>""</c>.</returns>
    public static SourceIndexSet Create(SourceIndex index)
    {
        Dictionary<string, SourceIndex> tables = new() { [""] = index };
        return new SourceIndexSet(index.Fingerprint, tables);
    }
}
