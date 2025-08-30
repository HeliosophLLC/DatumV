using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Takes a limited number of rows from a columnar child operator, optionally
/// skipping an offset.  When the limit is reached, upstream iteration is
/// terminated by breaking out of the <c>await foreach</c>, disposing the
/// upstream enumerator and stopping the scan.
/// </summary>
public sealed class ColumnBatchLimitOperator : IColumnBatchOperator
{
    private readonly IColumnBatchOperator _source;
    private readonly int _limit;
    private readonly int _offset;

    /// <summary>
    /// Creates a columnar limit operator.
    /// </summary>
    /// <param name="source">The child columnar operator producing batches.</param>
    /// <param name="limit">Maximum number of rows to emit.</param>
    /// <param name="offset">Number of rows to skip before emitting.</param>
    public ColumnBatchLimitOperator(IColumnBatchOperator source, int limit, int offset = 0)
    {
        _source = source;
        _limit = limit;
        _offset = offset;
    }

    /// <summary>The child columnar operator.</summary>
    public IColumnBatchOperator Source => _source;

    /// <summary>Maximum number of rows to emit.</summary>
    public int Limit => _limit;

    /// <summary>Number of rows to skip before emitting.</summary>
    public int Offset => _offset;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["limit"] = _limit.ToString(),
            ["mode"] = "columnar",
        };

        if (_offset > 0)
        {
            properties["offset"] = _offset.ToString();
        }

        return new OperatorPlanDescription("Limit")
        {
            Properties = properties,
            EstimatedRows = _limit,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ColumnBatch> ExecuteColumnBatchAsync(ExecutionContext context)
    {
        int skipped = 0;
        int emitted = 0;

        await foreach (ColumnBatch batch in _source.ExecuteColumnBatchAsync(context).ConfigureAwait(false))
        {
            int rowCount = batch.RowCount;

            // Skip entire batches within the offset range.
            if (skipped + rowCount <= _offset)
            {
                skipped += rowCount;
                batch.Dispose();
                continue;
            }

            // Determine the effective start row within this batch.
            int startRow = 0;
            if (skipped < _offset)
            {
                startRow = _offset - skipped;
                skipped = _offset;
            }

            // Determine how many rows to take from this batch.
            int remaining = _limit - emitted;
            int availableRows = rowCount - startRow;
            int takeRows = Math.Min(remaining, availableRows);

            if (startRow > 0 || takeRows < rowCount)
            {
                // Compact the batch to the selected row range.
                for (int column = 0; column < batch.ColumnCount; column++)
                {
                    DataValue[] columnBuffer = batch.GetColumnBuffer(column);

                    if (startRow > 0)
                    {
                        Array.Copy(columnBuffer, startRow, columnBuffer, 0, takeRows);
                    }
                }

                batch.SetRowCount(takeRows);
            }

            emitted += takeRows;
            yield return batch;

            if (emitted >= _limit)
            {
                yield break;
            }
        }
    }
}
