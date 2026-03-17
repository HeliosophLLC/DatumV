using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Helpers backing the default implementation of
/// <see cref="IScalarFunction.ExecuteBatchAsync"/>. Lives in its own type
/// (rather than as a static method on the interface) so the implementation
/// has a place to grow without polluting the interface contract.
/// </summary>
internal static class ScalarFunctionBatchHelpers
{
    /// <summary>
    /// Default columnar-batch loop: rebuilds per-row argument tuples from
    /// the columns and calls <see cref="IScalarFunction.ExecuteAsync"/>
    /// once per row. Behaviour is byte-equivalent to calling
    /// <c>ExecuteAsync</c> in a hand-written loop — overrides that need
    /// to preserve correctness while gaining speed must produce the same
    /// per-row results in the same order.
    /// </summary>
    /// <remarks>
    /// Allocates a single shared <see cref="ValueRef"/>[] of length
    /// <c>argumentColumns.Length</c> and refills it per row, so the only
    /// per-row allocations are whatever <c>ExecuteAsync</c> itself does.
    /// </remarks>
    public static async ValueTask<ValueRef[]> DefaultLoop(
        IScalarFunction function,
        ReadOnlyMemory<ValueRef>[] argumentColumns,
        int rowCount,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef[] results = new ValueRef[rowCount];
        int paramCount = argumentColumns.Length;
        ValueRef[] rowArgs = new ValueRef[paramCount];

        for (int row = 0; row < rowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int col = 0; col < paramCount; col++)
            {
                rowArgs[col] = argumentColumns[col].Span[row];
            }
            results[row] = await function
                .ExecuteAsync(rowArgs.AsMemory(0, paramCount), frame, cancellationToken)
                .ConfigureAwait(false);
        }

        return results;
    }
}
