namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// Container for per-table source indexes within a single <c>.datum-index</c> file.
/// The fingerprint is shared across all tables (one source file), while each table has
/// its own schema, chunk directory, and optional acceleration structures.
/// </summary>
/// <remarks>
/// <para>
/// Table keys are the fully qualified catalog table names (e.g. <c>"annotations"</c>,
/// <c>"annotations.labels"</c>). Single-table sources have one entry.
/// </para>
/// <para>
/// Use <see cref="Create"/> to wrap a single <see cref="SourceIndex"/> with its
/// catalog table name, extracting the shared fingerprint automatically.
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
    /// Gets the per-table source indexes, keyed by catalog table name.
    /// Single-table sources have one entry; multi-table sources have one per sub-table.
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
    /// with a named key. The fingerprint is extracted from the index.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="index">The single-table source index.</param>
    /// <returns>A new source index set with one entry keyed by <paramref name="tableName"/>.</returns>
    public static SourceIndexSet Create(string tableName, SourceIndex index)
    {
        Dictionary<string, SourceIndex> tables = new() { [tableName] = index };
        return new SourceIndexSet(index.Fingerprint, tables);
    }
}
