using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Single-shape output writer that copies rows from input batches into output
/// batches via <c>Pool.RentAndCopyToOutput</c>. Wraps the shared
/// <see cref="OutputBatchAccumulator"/> plumbing so operators whose emit
/// shape never changes mid-execution — scans, set operations, distinct, skip,
/// any "pass-through with filter" — collapse the
/// <c>rent ??= ... → RentAndCopyToOutput → IsFull → yield</c> pattern into
/// a single <c>Add</c> call per emitted row.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Joins.JoinOutputWriter"/>, this writer never builds rows —
/// it only copies. The shape-stability assertion in
/// <see cref="OutputBatchAccumulator"/> defends the output batch's
/// <c>ColumnLookup</c> against unexpected mid-stream changes.
/// </para>
/// <para>
/// Two emit shapes. <c>Add(sourceBatch, sourceIndex)</c> takes the output lookup
/// from the source batch — correct when the source is the canonical schema
/// (scans, distinct, skip). <c>Add(outputLookup, sourceBatch, sourceIndex)</c>
/// accepts an explicit output lookup — required when the output schema is
/// fixed but per-source-batch lookups can vary (UNION concatenates two
/// upstream operators whose batches carry their own <c>ColumnLookup</c>
/// references for the same logical schema).
/// </para>
/// </remarks>
internal sealed class RowCopyOutputWriter : OutputBatchAccumulator
{
    public RowCopyOutputWriter(ExecutionContext context) : base(context)
    {
    }

    /// <summary>
    /// Copies row <paramref name="sourceIndex"/> from <paramref name="sourceBatch"/>
    /// into the current output batch using <paramref name="sourceBatch"/>'s
    /// <see cref="ColumnLookup"/> as the output shape. Returns the in-progress
    /// batch detached from the writer when it fills, or <see langword="null"/>
    /// when not full.
    /// </summary>
    public RowBatch? Add(RowBatch sourceBatch, int sourceIndex)
        => Add(sourceBatch.ColumnLookup, sourceBatch, sourceIndex);

    /// <summary>
    /// Copies row <paramref name="sourceIndex"/> from <paramref name="sourceBatch"/>
    /// into the current output batch using <paramref name="outputLookup"/> as
    /// the rented batch's shape. Use this overload when the output schema is
    /// captured once but per-source-batch lookups may differ (UNION, etc.).
    /// </summary>
    public RowBatch? Add(ColumnLookup outputLookup, RowBatch sourceBatch, int sourceIndex)
    {
        RowBatch current = EnsureRentedAndGetCurrent(outputLookup);
        Pool.RentAndCopyToOutput(sourceBatch, sourceIndex, current);
        return TakeIfFull();
    }
}
