using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// A virtual table source that produces rows from metadata rather than from
/// a backing data file. Used by virtual schemas such as <c>information_schema</c>
/// and <c>datum_catalog</c> to expose catalog metadata as queryable tables.
/// </summary>
public interface IVirtualTableSource
{
    /// <summary>
    /// Returns the fixed schema of this virtual table.
    /// </summary>
    Schema GetSchema();

    /// <summary>
    /// Scans the virtual table and yields row batches using the provided context
    /// for catalog and function registry access.
    /// </summary>
    /// <param name="context">
    /// Provides access to the current <see cref="TableCatalog"/> and
    /// <see cref="Functions.FunctionRegistry"/> so the virtual table can
    /// reflect context-aware metadata (e.g. temp tables in the active query context).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An asynchronous sequence of row batches.</returns>
    IAsyncEnumerable<RowBatch> ScanAsync(VirtualTableContext context, CancellationToken cancellationToken);
}
