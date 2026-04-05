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
    /// Default columnar-batch loop. Dispatches
    /// <see cref="IScalarFunction.ExecuteAsync"/> row-by-row in a single
    /// sequential pass. Per-row results land in the same order as the
    /// input columns so overrides that opt out via
    /// <c>ExecuteBatchAsync</c> remain byte-equivalent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why sequential.</strong> An earlier revision parallelised
    /// this loop via <c>Parallel.ForAsync</c> for batches at or above 4,
    /// on the theory that pure scalar functions can be
    /// dispatched independently. The theory is correct for the function
    /// surface but not for the shared <see cref="Arena"/> they read
    /// through: when one parallel worker writes to the arena (managed
    /// payload → arena copy inside <c>ValueRef.ToDataValue</c>) and the
    /// arena grows, the old mmap region gets unmapped. Any other worker
    /// holding a <see cref="System.Span{T}"/> into the old region now
    /// dangles, and the next index triggers
    /// <see cref="System.AccessViolationException"/>. The parallel path
    /// was latent until the calibration auto-trigger started dispatching
    /// at batch sizes ≥ 4 in production; the crashes appeared
    /// immediately. Restoring parallelism requires either making arena
    /// growth span-safe (keep old mmaps pinned until outstanding spans
    /// drop) or removing the managed-payload-to-arena round-trip
    /// entirely. Both are filed as follow-ups; until they land,
    /// sequential is the safe default.
    /// </para>
    /// <para>
    /// <strong>Thread-safety contract.</strong> Scalar functions are
    /// still expected to be pure with respect to the
    /// <see cref="EvaluationFrame"/> they receive — they read it, produce
    /// a <see cref="ValueRef"/>, and don't mutate shared state. Future
    /// re-parallelisation depends on that contract holding.
    /// </para>
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

        // Reuses one rowArgs buffer for every row — safe because there's
        // only ever one in-flight call.
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
