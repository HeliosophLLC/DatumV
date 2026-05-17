using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.BatchProjections;

/// <summary>
/// A projection that can copy/compute output cells for a whole
/// <see cref="RowBatch"/> in one call, without entering the per-row async
/// state machine of <c>ProjectionSchema.ProjectAsync</c>. The contract:
/// <list type="bullet">
///   <item><description>Pulls source values from <c>inputBatch</c>, materialises one
///     output row per input row into <c>currentOutput</c>.</description></item>
///   <item><description>If <c>currentOutput</c> fills mid-loop, the projector detaches it
///     by appending to <c>readyBatches</c> and rents a fresh one. Caller drains
///     <c>readyBatches</c> and yields them once the input batch has been returned.</description></item>
///   <item><description>Source values that point into the input batch's arena are stabilised
///     into the output batch's arena via <see cref="DataValueRetention.Stabilize"/>
///     so the output remains valid after the input batch is returned to the pool.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The point of this abstraction is to collapse projection's per-row state-machine
/// cost into a tight monomorphic loop, the same win we got on FilterOperator with
/// <c>IBatchPredicate</c>. v1 covers the all-<c>CopyOrdinal</c> case (every output
/// column is a direct copy from a source column, no expression evaluation, no LET,
/// no ASSERT). Anything richer falls back to the existing per-row path.
/// </remarks>
internal interface IBatchProjector
{
    /// <summary>
    /// Projects every row of <paramref name="inputBatch"/> through this projector.
    /// Output rows land in <paramref name="output"/>'s in-progress batch; full output
    /// batches are detached into <paramref name="readyBatches"/> as they fill.
    /// </summary>
    /// <param name="inputBatch">The batch to project.</param>
    /// <param name="context">Execution context (cancellation, pool access).</param>
    /// <param name="outputLookup">Column lookup for any newly-rented output batch.</param>
    /// <param name="output">Output accumulator holding the in-progress batch across input batches.</param>
    /// <param name="readyBatches">Receives detached full output batches in the order they fill.</param>
    void Project(
        RowBatch inputBatch,
        ExecutionContext context,
        ColumnLookup outputLookup,
        OutputBatchAccumulator output,
        List<RowBatch> readyBatches);
}

/// <summary>
/// Batch projector for the all-<c>CopyOrdinal</c> case: every output column
/// is a direct copy from a fixed source column. The compiler picks this
/// projector when there are no LET bindings, no ASSERT clauses, and no
/// expression-evaluation slots — the common pure-passthrough projection
/// like <c>SELECT id, name, value FROM data</c>.
/// </summary>
/// <remarks>
/// Inner loop is monomorphic: rent the output array, copy + stabilise each
/// cell by ordinal, add the row, repeat. No method dispatch into a schema
/// object, no async state machine per row.
/// </remarks>
internal sealed class CopyOnlyBatchProjector : IBatchProjector
{
    private readonly int[] _sourceOrdinals;

    public CopyOnlyBatchProjector(int[] sourceOrdinals)
    {
        _sourceOrdinals = sourceOrdinals;
    }

    public void Project(
        RowBatch inputBatch,
        ExecutionContext context,
        ColumnLookup outputLookup,
        OutputBatchAccumulator output,
        List<RowBatch> readyBatches)
    {
        int width = _sourceOrdinals.Length;
        int n = inputBatch.Count;
        Pool pool = context.Pool;
        IValueStore sourceArena = inputBatch.Arena;

        for (int r = 0; r < n; r++)
        {
            RowBatch current = output.EnsureRentedAndGetCurrent(outputLookup);
            IValueStore destArena = current.Arena;

            DataValue[] outputRow = pool.RentDataValues(width);
            Row src = inputBatch[r];
            for (int c = 0; c < width; c++)
            {
                outputRow[c] = DataValueRetention.Stabilize(src[_sourceOrdinals[c]], sourceArena, destArena);
            }
            current.Add(outputRow);

            RowBatch? full = output.TakeIfFull();
            if (full is not null)
            {
                readyBatches.Add(full);
            }
        }
    }
}
