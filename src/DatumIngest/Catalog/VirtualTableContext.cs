using DatumIngest.Functions;

namespace DatumIngest.Catalog;

/// <summary>
/// Context passed to <see cref="IVirtualTableSource.ScanAsync"/> so virtual tables
/// can access the current catalog and function registry to produce metadata rows.
/// The catalog reflects the active query context's layered view (including temp tables).
/// </summary>
public sealed class VirtualTableContext
{
    /// <summary>
    /// Creates a new virtual table context.
    /// </summary>
    /// <param name="catalog">The active table catalog (context overlay + session base).</param>
    /// <param name="functionRegistry">The function registry for the current session.</param>
    public VirtualTableContext(TableCatalog catalog, FunctionRegistry functionRegistry)
    {
        Catalog = catalog;
        FunctionRegistry = functionRegistry;
    }

    /// <summary>
    /// The active table catalog, including any temp tables from the current query context.
    /// </summary>
    public TableCatalog Catalog { get; }

    /// <summary>
    /// The function registry for the current session.
    /// </summary>
    public FunctionRegistry FunctionRegistry { get; }
}
