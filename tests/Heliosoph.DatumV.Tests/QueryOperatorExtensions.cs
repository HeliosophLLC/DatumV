using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests;

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
    /// backing arrays whose arena-backed payloads are stabilised into
    /// <c>context.Store</c>. Safe to use after the query stream and pool are
    /// disposed; assertions reading non-inline values should pass
    /// <c>context.Store</c> as the resolution store.
    /// </summary>
    internal static async Task<List<Row>> CollectRowsAsync(
        this QueryOperator plan, ExecutionContext context)
    {
        List<Row> rows = [];

        await foreach (RowBatch batch in plan.ExecuteAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i].CloneForTest(batch.Arena, context.Store));
            }

            context.ReturnRowBatch(batch);
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
