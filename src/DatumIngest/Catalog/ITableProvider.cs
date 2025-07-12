using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Interface for data source providers that stream rows from a backing store
/// (CSV, JSON, ZIP, HDF5, Parquet, etc.).
/// </summary>
public interface ITableProvider
{
    /// <summary>
    /// Returns the schema of the table described by <paramref name="descriptor"/>.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The inferred or declared schema.</returns>
    Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the table and streams rows asynchronously.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="requiredColumns">
    /// Set of column names the consumer needs. The provider may skip
    /// producing columns not in this set for projection pushdown.
    /// When null, all columns are returned.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of rows from the data source.</returns>
    IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the operational capabilities of this provider for the given descriptor,
    /// enabling cost-based query planning decisions.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Provider capabilities for query planning.</returns>
    Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken);
}
