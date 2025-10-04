using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Converts a columnar <see cref="IColumnBatchOperator"/> into a row-major
/// <see cref="IQueryOperator"/> at the boundary between columnar and row-based
/// sub-pipelines.  Each incoming <see cref="ColumnBatch"/> is decomposed into
/// <see cref="Row"/> objects packed into <see cref="RowBatch"/> containers.
/// </summary>
/// <remarks>
/// <para>
/// Row values are allocated from <see cref="LocalBufferPool"/> and owned for
/// the query lifetime, matching the allocation pattern used by
/// <see cref="ProjectOperator"/> and other operators.
/// Arena-backed strings are materialised so each <see cref="Row"/> is
/// self-contained after the source <see cref="ColumnBatch"/> is disposed.
/// </para>
/// </remarks>
public sealed class ColumnBatchToRowBatchAdapter : IQueryOperator
{
    private readonly IColumnBatchOperator _source;

    /// <summary>
    /// Creates an adapter that wraps a columnar operator as a row-based operator.
    /// </summary>
    /// <param name="source">The columnar operator to adapt.</param>
    public ColumnBatchToRowBatchAdapter(IColumnBatchOperator source)
    {
        _source = source;
    }

    /// <summary>The wrapped columnar operator.</summary>
    public IColumnBatchOperator Source => _source;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return _source.DescribeForExplain();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        LocalBufferPool pool = context.LocalBufferPool;

        await foreach (ColumnBatch columnBatch in _source.ExecuteColumnBatchAsync(context).ConfigureAwait(false))
        {
            int rowCount = columnBatch.RowCount;
            int columnCount = columnBatch.ColumnCount;
            RowBatch rowBatch = RowBatch.Rent(rowCount);

            for (int row = 0; row < rowCount; row++)
            {
                DataValue[] buffer = pool.Rent(columnCount);
                rowBatch.Add(columnBatch.GetRow(row, buffer));
            }

            columnBatch.Dispose();
            yield return rowBatch;
        }
    }
}
