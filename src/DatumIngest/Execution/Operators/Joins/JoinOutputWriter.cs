using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Joins;

/// <summary>
/// Owns the output-batch lifecycle for a join: renting from the
/// <see cref="ExecutionContext"/>, adding rows, tracking
/// <see cref="RowBatch.IsFull"/>, detaching when full, and flushing the
/// trailing batch. Hides the <c>rent ??= ... → Add → IsFull → yield</c>
/// pattern that joins repeat at every emit site.
/// </summary>
/// <remarks>
/// <para>
/// Each emit method returns a <see cref="RowBatch"/> when the current batch
/// fills (caller must <c>yield return</c> it) and <see langword="null"/>
/// otherwise. <see cref="OutputBatchAccumulator.Flush"/> returns the
/// in-progress batch (if any) and detaches it from the writer; safe to call
/// from a <c>finally</c> block.
/// </para>
/// <para>
/// <b>Schema invariant:</b> within a single execution, only one of the emit
/// shapes (<see cref="EmitCombined"/> or <see cref="EmitPassThrough"/>) is
/// expected to fire. This matches the join semantics today — combined emits
/// require a non-empty build side, pass-through emits fire only when the
/// build side is empty or the join is semi-style. The shape-stability
/// assertion in <see cref="OutputBatchAccumulator"/> defends against future
/// regressions.
/// </para>
/// </remarks>
internal sealed class JoinOutputWriter : OutputBatchAccumulator
{
    private JoinSchema? _combinedSchema;

    public JoinOutputWriter(ExecutionContext context) : base(context)
    {
    }

    /// <summary>
    /// Returns the combined-row schema, building it on the first call from
    /// the supplied template rows. Exposed for residual-check buffer setup —
    /// when a non-equi residual exists, the operator needs the schema before
    /// deciding to emit.
    /// </summary>
    public JoinSchema GetCombinedSchema(Row left, Row right)
        => _combinedSchema ??= JoinSchema.Build(left, right);

    /// <summary>
    /// Emits a combined-shape row (matched pair, or one side null-padded).
    /// Builds the shared combined schema on the first call.
    /// </summary>
    public RowBatch? EmitCombined(Row left, Row right)
    {
        JoinSchema schema = _combinedSchema ??= JoinSchema.Build(left, right);
        RowBatch current = EnsureRentedAndGetCurrent(schema.ColumnLookup);
        current.Add(schema.CombinePooledValues(left, right, Pool));
        return TakeIfFull();
    }

    /// <summary>
    /// Emits a single-side pass-through row (semi-join hit, anti-semi miss,
    /// outer fallback when the opposite side is empty, or build-solo when no
    /// probe row was ever observed). Stabilises the row's values from
    /// <paramref name="sourceArena"/> into the output batch's arena.
    /// </summary>
    public RowBatch? EmitPassThrough(Row row, Arena sourceArena)
    {
        RowBatch current = EnsureRentedAndGetCurrent(row.ColumnLookup);
        DataValue[] copy = Pool.RentAndCopyDataValues(row, sourceArena, current.Arena);
        current.Add(copy);
        return TakeIfFull();
    }
}
