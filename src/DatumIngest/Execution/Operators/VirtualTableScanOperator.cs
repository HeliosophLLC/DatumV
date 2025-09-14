using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Scan operator for virtual schema tables (e.g. <c>information_schema.tables</c>).
/// Wraps an <see cref="IVirtualTableSource"/> and produces <see cref="RowBatch"/>es
/// at execution time using the catalog and function registry from the
/// <see cref="ExecutionContext"/>.
/// </summary>
internal sealed class VirtualTableScanOperator : IQueryOperator
{
    private readonly IVirtualTableSource _source;
    private readonly string _schemaName;
    private readonly string _tableName;
    private readonly IReadOnlySet<string>? _requiredColumns;

    /// <summary>
    /// Creates a virtual table scan operator.
    /// </summary>
    /// <param name="source">The virtual table source to scan.</param>
    /// <param name="schemaName">The schema name for explain output (e.g. <c>information_schema</c>).</param>
    /// <param name="tableName">The table name for explain output (e.g. <c>tables</c>).</param>
    /// <param name="requiredColumns">
    /// The set of column names needed by the query, or <see langword="null"/> for all columns.
    /// Used for projection pushdown.
    /// </param>
    public VirtualTableScanOperator(
        IVirtualTableSource source,
        string schemaName,
        string tableName,
        IReadOnlySet<string>? requiredColumns)
    {
        _source = source;
        _schemaName = schemaName;
        _tableName = tableName;
        _requiredColumns = requiredColumns;
    }

    /// <summary>
    /// Returns the schema of this virtual table, filtered to the required columns
    /// if projection pushdown is active.
    /// </summary>
    public Schema GetSchema()
    {
        Schema fullSchema = _source.GetSchema();

        if (_requiredColumns is null)
        {
            return fullSchema;
        }

        List<ColumnInfo> filtered = new();
        foreach (ColumnInfo column in fullSchema.Columns)
        {
            if (_requiredColumns.Contains(column.Name))
            {
                filtered.Add(column);
            }
        }

        return filtered.Count > 0 ? new Schema(filtered) : fullSchema;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        VirtualTableContext virtualContext = new(context.Catalog, context.FunctionRegistry);

        await foreach (RowBatch batch in _source.ScanAsync(virtualContext, context.CancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc />
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["table"] = $"{_schemaName}.{_tableName}",
        };

        if (_requiredColumns is not null)
        {
            properties["columns"] = string.Join(", ", _requiredColumns);
        }

        return new OperatorPlanDescription("VirtualScan")
        {
            Properties = properties,
        };
    }
}
