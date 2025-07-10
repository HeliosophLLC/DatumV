using DatumQuery.Catalog;
using DatumQuery.Model;

namespace DatumQuery.Execution.Operators;

/// <summary>
/// Reads rows from a table provider, applying projection pushdown
/// to skip unreferenced columns at the source.
/// </summary>
public sealed class ScanOperator : IQueryOperator
{
    private readonly TableDescriptor _descriptor;
    private readonly IReadOnlySet<string>? _requiredColumns;

    /// <summary>
    /// Creates a scan operator for the given table.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the data source.</param>
    /// <param name="requiredColumns">Columns needed downstream; null means all columns.</param>
    public ScanOperator(TableDescriptor descriptor, IReadOnlySet<string>? requiredColumns)
    {
        _descriptor = descriptor;
        _requiredColumns = requiredColumns;
    }

    /// <summary>The table descriptor this operator scans.</summary>
    public TableDescriptor Descriptor => _descriptor;

    /// <summary>The set of columns requested for projection pushdown.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);

        await foreach (Row row in provider.OpenAsync(
            _descriptor, _requiredColumns, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return row;
        }
    }
}
