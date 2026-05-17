using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Produces a single row with no columns. Used as the source for SELECT statements
/// without a FROM clause (e.g., recursive CTE anchors like <c>SELECT 1 AS n</c>).
/// </summary>
internal sealed class SingleEmptyRowOperator : QueryOperator
{
    /// <summary>
    /// Creates a single empty row operator.
    /// </summary>
    public SingleEmptyRowOperator() : base(false) { }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return new OperatorPlanDescription("Single Empty Row")
        {
            EstimatedRows = 1,
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        OutputBatchAccumulator output = new(context);

        try
        {
            Row row = new(ColumnLookup.Empty, []);
            RowBatch? full = output.Adopt(ColumnLookup.Empty, row);
            if (full is not null) yield return full;

            RowBatch? trailing = output.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            RowBatch? leftover = output.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }
}
