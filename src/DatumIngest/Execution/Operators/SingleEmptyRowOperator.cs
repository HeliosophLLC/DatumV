using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Produces a single row with no columns. Used as the source for SELECT statements
/// without a FROM clause (e.g., recursive CTE anchors like <c>SELECT 1 AS n</c>).
/// </summary>
internal sealed class SingleEmptyRowOperator : IQueryOperator
{
    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Single Empty Row")
        {
            EstimatedRows = 1,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        RowBatch outputBatch = context.Pool.RentRowBatch(ColumnLookup.Empty, 1);

        outputBatch.Add([]);

        yield return outputBatch;
    }
}
