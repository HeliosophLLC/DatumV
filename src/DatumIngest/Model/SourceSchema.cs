namespace DatumIngest.Model;

/// <summary>
/// Container for per-table schemas within a single source file.
/// A <c>.datum-schema</c> sidecar always contains a <see cref="SourceSchema"/>,
/// keyed by the logical table name used in the catalog.
/// </summary>
/// <remarks>
/// <para>
/// When a source file exposes multiple logical tables (e.g. a JSON file with several
/// top-level array properties), each table's schema is stored under its fully
/// qualified catalog name (e.g. <c>"data"</c>, <c>"data.annotations"</c>).
/// </para>
/// <para>
/// Use <see cref="Create"/> to wrap a single <see cref="Schema"/> with its catalog
/// table name.
/// </para>
/// </remarks>
public sealed class SourceSchema
{
    /// <summary>
    /// Gets the per-table schemas, keyed by catalog table name.
    /// Single-table sources have one entry; multi-table sources have one per sub-table.
    /// </summary>
    public required IReadOnlyDictionary<string, Schema> Tables { get; init; }

    /// <summary>
    /// Creates a <see cref="SourceSchema"/> wrapping a single <see cref="Schema"/>.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="schema">The single-table schema.</param>
    /// <returns>A new source schema with one entry keyed by <paramref name="tableName"/>.</returns>
    public static SourceSchema Create(string tableName, Schema schema)
    {
        return new SourceSchema
        {
            Tables = new Dictionary<string, Schema> { [tableName] = schema }
        };
    }
}
