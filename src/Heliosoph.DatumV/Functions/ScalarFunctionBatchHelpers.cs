using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

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
    /// <strong>Why sequential (currently).</strong> The Arena now uses
    /// reserve-once-commit-on-demand so its base pointer is stable across grows —
    /// the original span-dangling AVE that forced sequentialisation is fixed. A
    /// first attempt at re-enabling parallel dispatch (via
    /// <c>Parallel.ForAsync</c> at threshold 4) crashed depth-model calibration
    /// (the since-replaced depth-anything-v3-large entry) with a native-level
    /// fault (Windows 0xC0000409). The arena
    /// itself isn't suspect — the basic concurrent-reads-across-grow scenario
    /// passes its unit test cleanly — but something else in the scalar pipeline
    /// or model-body composition is not parallel-safe. Kept sequential while
    /// that's investigated.
    /// </para>
    /// <para>
    /// <strong>Thread-safety contract.</strong> Scalar functions are expected to
    /// be pure with respect to the <see cref="EvaluationFrame"/> they receive —
    /// they read it, produce a <see cref="ValueRef"/>, and don't mutate shared
    /// state.
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
