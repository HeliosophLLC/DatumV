using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Single-shape output writer that copies rows from input batches into output
/// batches via <c>Pool.RentAndCopyToOutput</c>. Wraps the shared
/// <see cref="OutputBatchAccumulator"/> plumbing so operators whose emit
/// shape never changes mid-execution — scans, set operations, distinct, skip,
/// any "pass-through with filter" — collapse the
/// <c>rent ??= ... → RentAndCopyToOutput → IsFull → yield</c> pattern into
/// a single <see cref="Add"/> call per emitted row.
/// </summary>
/// <remarks>
/// Unlike <see cref="Joins.JoinOutputWriter"/>, this writer never builds rows —
/// it only copies. Every <see cref="Add"/> takes the source batch + row index;
/// the source batch's <see cref="RowBatch.ColumnLookup"/> defines the output
/// shape. The shape-stability assertion in <see cref="OutputBatchAccumulator"/>
/// defends against an upstream operator unexpectedly switching schemas mid-stream.
/// </remarks>
internal sealed class RowCopyOutputWriter : OutputBatchAccumulator
{
    public RowCopyOutputWriter(ExecutionContext context) : base(context)
    {
    }

    /// <summary>
    /// Copies row <paramref name="sourceIndex"/> from <paramref name="sourceBatch"/>
    /// into the current output batch, renting from the context if necessary.
    /// Returns the in-progress batch detached from the writer when it fills, or
    /// <see langword="null"/> when not full.
    /// </summary>
    public RowBatch? Add(RowBatch sourceBatch, int sourceIndex)
    {
        RowBatch current = EnsureRentedAndGetCurrent(sourceBatch.ColumnLookup);
        Pool.RentAndCopyToOutput(sourceBatch, sourceIndex, current);
        return TakeIfFull();
    }
}
