namespace DatumIngest.Catalog;

/// <summary>
/// Optional extension of <see cref="ITableProvider"/> for providers that can discover
/// multiple logical tables within a single source file. When a provider implements this
/// interface, the catalog calls <see cref="DiscoverTablesAsync"/> during expansion to
/// register each sub-table individually.
/// </summary>
/// <remarks>
/// <para>
/// Returning <c>null</c> signals that the source should be treated as a single table
/// (no expansion). Returning a non-empty list causes the catalog to replace the original
/// registration with one entry per discovered sub-table.
/// </para>
/// <para>
/// The JSON provider implements this to auto-discover array properties in root-object
/// JSON files. Other providers (e.g. HDF5) can adopt the same pattern.
/// </para>
/// </remarks>
public interface IMultiTableSource : ITableProvider
{
    /// <summary>
    /// Discovers logical sub-tables within the source file described by the given descriptor.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the source file and options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of discovered sub-tables, or <c>null</c> if the source should remain a single table.
    /// An empty list also indicates no expansion (treated the same as <c>null</c>).
    /// </returns>
    Task<IReadOnlyList<DiscoveredTable>?> DiscoverTablesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken);
}
