using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Reads data from an <see cref="IColumnBatchProvider"/> source in column-major
/// format, applying projection pushdown and statistics-based partition pruning.
/// This is the columnar counterpart of <see cref="ScanOperator"/> for providers
/// that natively produce <see cref="ColumnBatch"/> output.
/// </summary>
public sealed class ColumnBatchScanOperator : IColumnBatchOperator
{
    private readonly TableDescriptor _descriptor;
    private readonly IReadOnlySet<string>? _requiredColumns;
    private Expression? _filterHint;

    /// <summary>
    /// Creates a columnar scan operator for the given table.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the data source.</param>
    /// <param name="requiredColumns">Columns needed downstream; null means all columns.</param>
    public ColumnBatchScanOperator(TableDescriptor descriptor, IReadOnlySet<string>? requiredColumns)
    {
        _descriptor = descriptor;
        _requiredColumns = requiredColumns;
    }

    /// <summary>The table descriptor this operator scans.</summary>
    public TableDescriptor Descriptor => _descriptor;

    /// <summary>The set of columns requested for projection pushdown.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <summary>The advisory filter hint, or <c>null</c>.</summary>
    public Expression? FilterHint => _filterHint;

    /// <summary>
    /// Estimated row count from provider capabilities or manifest, set at plan time.
    /// </summary>
    public long? EstimatedRowCount { get; set; }

    /// <summary>
    /// Per-column statistics from a <see cref="QueryResultsManifest"/>, set at plan time.
    /// </summary>
    public IReadOnlyDictionary<string, FeatureManifest>? ColumnStatistics { get; set; }

    /// <summary>
    /// Adds an advisory filter predicate for statistics-based partition pruning.
    /// Multiple calls combine predicates with AND.
    /// </summary>
    /// <param name="predicate">The predicate to add as a filter hint.</param>
    public void AddFilterHint(Expression predicate)
    {
        _filterHint = _filterHint is null
            ? predicate
            : new BinaryExpression(_filterHint, BinaryOperator.And, predicate);
    }

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["table"] = _descriptor.Name,
            ["provider"] = _descriptor.Provider,
            ["mode"] = "columnar",
            ["columns"] = _requiredColumns is not null
                ? string.Join(", ", _requiredColumns)
                : "*",
        };

        if (_filterHint is not null)
        {
            properties["statistics filter"] = QueryExplainer.FormatExpression(_filterHint);
        }

        return new OperatorPlanDescription("Scan")
        {
            Properties = properties,
            EstimatedRows = EstimatedRowCount,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ColumnBatch> ExecuteColumnBatchAsync(
        ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);

        if (provider is not IColumnBatchProvider columnBatchProvider)
        {
            throw new InvalidOperationException(
                $"Provider for '{_descriptor.Name}' does not implement IColumnBatchProvider. " +
                "The query planner should not have created a ColumnBatchScanOperator for this table.");
        }

        await foreach (ColumnBatch batch in columnBatchProvider.OpenColumnBatchAsync(
            _descriptor, _requiredColumns, _filterHint, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
