using System.Buffers;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Filters rows from a columnar child operator by evaluating a WHERE predicate
/// on an entire <see cref="ColumnBatch"/> at once.  Matching rows are compacted
/// in-place, avoiding per-row allocation and enabling column-at-a-time evaluation.
/// </summary>
public sealed class ColumnBatchFilterOperator : IColumnBatchOperator
{
    private readonly IColumnBatchOperator _source;
    private readonly Expression _predicate;

    /// <summary>
    /// Creates a columnar filter operator.
    /// </summary>
    /// <param name="source">The child columnar operator producing batches.</param>
    /// <param name="predicate">The WHERE predicate expression.</param>
    public ColumnBatchFilterOperator(IColumnBatchOperator source, Expression predicate)
    {
        _source = source;
        _predicate = predicate;
    }

    /// <summary>The child columnar operator.</summary>
    public IColumnBatchOperator Source => _source;

    /// <summary>The filter predicate expression.</summary>
    public Expression Predicate => _predicate;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Filter")
        {
            Properties = new Dictionary<string, string>
            {
                ["predicate"] = QueryExplainer.FormatExpression(_predicate),
                ["mode"] = "columnar",
            },
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ColumnBatch> ExecuteColumnBatchAsync(ExecutionContext context)
    {
        int[]? selectedIndices = null;

        await foreach (ColumnBatch batch in _source.ExecuteColumnBatchAsync(context).ConfigureAwait(false))
        {
            if (batch.RowCount == 0)
            {
                batch.Dispose();
                continue;
            }

            // Rent a selection vector large enough for the batch.
            if (selectedIndices is null || selectedIndices.Length < batch.RowCount)
            {
                if (selectedIndices is not null)
                {
                    ArrayPool<int>.Shared.Return(selectedIndices);
                }

                selectedIndices = ArrayPool<int>.Shared.Rent(batch.RowCount);
            }

            int selectedCount;
            using (ColumnBatchEvaluator evaluator = new(context.FunctionRegistry))
            {
                selectedCount = evaluator.EvaluateFilter(_predicate, batch, selectedIndices);
            }

            if (selectedCount == 0)
            {
                batch.Dispose();
                continue;
            }

            // All rows pass — yield unchanged.
            if (selectedCount == batch.RowCount)
            {
                yield return batch;
                continue;
            }

            // Compact the batch in-place by moving selected rows to the front.
            // selectedIndices is sorted ascending, so we never overwrite a source
            // position that hasn't been read yet (selectedIndices[i] >= i always holds).
            for (int column = 0; column < batch.ColumnCount; column++)
            {
                DataValue[] columnBuffer = batch.GetColumnBuffer(column);

                for (int i = 0; i < selectedCount; i++)
                {
                    columnBuffer[i] = columnBuffer[selectedIndices[i]];
                }
            }

            batch.SetRowCount(selectedCount);
            yield return batch;
        }

        if (selectedIndices is not null)
        {
            ArrayPool<int>.Shared.Return(selectedIndices);
        }
    }
}
