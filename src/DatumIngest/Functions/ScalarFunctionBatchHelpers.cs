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
    /// Below this row count the parallel-dispatch overhead exceeds the
    /// savings, so DefaultLoop stays sequential. Empirically 4 is the
    /// crossover for cheap scalar functions; expensive ones (image
    /// preprocessing) benefit at any batch ≥ 2 but the constant chosen
    /// here favours not regressing the cheap-and-tiny case.
    /// </summary>
    private const int ParallelThreshold = 4;

    /// <summary>
    /// Default columnar-batch loop. Dispatches <see cref="IScalarFunction.ExecuteAsync"/>
    /// row-by-row, sequentially below <see cref="ParallelThreshold"/> and
    /// in parallel above it. Per-row results land in the same order as
    /// the input columns so overrides that opt out via <c>ExecuteBatchAsync</c>
    /// remain byte-equivalent to the sequential reference behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Thread-safety contract.</strong> Scalar functions are expected
    /// to be pure with respect to the <see cref="EvaluationFrame"/> passed in
    /// — they read it, produce a <see cref="ValueRef"/>, and don't mutate
    /// shared state. Every shipped scalar function meets this contract
    /// (image preprocessing, math, string operations all return new managed
    /// payloads). A function that DOES mutate shared state (caches a static
    /// dictionary, accumulates a side effect) must override
    /// <c>ExecuteBatchAsync</c> with its own sequential implementation —
    /// the default's parallel path will race otherwise.
    /// </para>
    /// <para>
    /// <strong>Why this matters.</strong> Image preprocessing (image_to_tensor,
    /// image_height, image_width, depth_map_to_image) dominates the CPU
    /// portion of model dispatch — measured at ~600 ms per 32-row chunk on a
    /// recent depth-model trace. Parallel dispatch across CPU cores shrinks
    /// that to ~80 ms on an 8-core box; the savings compound for any
    /// multi-row model query. Sequential below the threshold keeps the
    /// per-row scalar-call sites cheap (no thread-pool involvement for
    /// batches that wouldn't benefit).
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

        if (rowCount < ParallelThreshold)
        {
            // Sequential path. Reuses one rowArgs buffer for every row —
            // safe because there's only ever one in-flight call.
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

        // Parallel path. Each task owns its own rowArgs buffer (small
        // alloc per row — a few ValueRefs); the shared `results` array
        // is written at distinct indices so there's no contention.
        await Parallel.ForAsync(0, rowCount,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            },
            async (row, ct) =>
            {
                ValueRef[] rowArgs = new ValueRef[paramCount];
                for (int col = 0; col < paramCount; col++)
                {
                    rowArgs[col] = argumentColumns[col].Span[row];
                }
                results[row] = await function
                    .ExecuteAsync(rowArgs.AsMemory(0, paramCount), frame, ct)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        return results;
    }
}
