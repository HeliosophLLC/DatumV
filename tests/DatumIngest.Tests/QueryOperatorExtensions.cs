using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests;

/// <summary>
/// Extension methods for collecting query results in tests. Rows are deep-copied
/// so they survive <see cref="LocalBufferPool"/> disposal, which returns owned
/// <see cref="DataValue"/> arrays to <see cref="GlobalBufferPool"/> when the
/// query stream is exhausted.
/// </summary>
internal static class QueryOperatorExtensions
{
    /// <summary>
    /// Executes the operator and collects all rows into a list with deep-copied
    /// backing arrays. Safe to use after the query stream and pool are disposed.
    /// </summary>
    internal static async Task<List<Row>> CollectRowsAsync(
        this IQueryOperator plan, ExecutionContext context)
    {
        List<Row> rows = [];

        await foreach (RowBatch batch in plan.ExecuteAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i].CloneForTest());
            }

            context.Pool.ReturnRowBatch(batch);
        }

        return rows;
    }

    /// <summary>
    /// Collects all rows from an async row batch stream with deep-copied
    /// backing arrays. Safe to use after the query stream and pool are disposed.
    /// </summary>
    internal static async Task<List<Row>> CollectRowsAsync(
        this IAsyncEnumerable<RowBatch> source)
    {
        List<Row> rows = [];

        await foreach (RowBatch batch in source)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i].CloneForTest());
            }
        }

        return rows;
    }
}
