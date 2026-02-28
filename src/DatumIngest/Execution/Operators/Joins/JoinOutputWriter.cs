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
/// otherwise. <see cref="Flush"/> returns the in-progress batch (if any) and
/// detaches it from the writer; safe to call from a <c>finally</c> block.
/// </para>
/// <para>
/// <b>Schema invariant:</b> within a single execution, only one of the emit
/// shapes (<see cref="EmitCombined"/> or <see cref="EmitPassThrough"/>) is
/// expected to fire. This matches the join semantics today — combined emits
/// require a non-empty build side, pass-through emits fire only when the
/// build side is empty or the join is semi-style. The writer asserts the
/// invariant on each call: the rented batch's
/// <see cref="ColumnLookup"/> must match every subsequent emit's lookup.
/// A mismatch throws — defending against future regressions.
/// </para>
/// </remarks>
internal sealed class JoinOutputWriter
{
    private readonly ExecutionContext _context;
    private readonly Pool _pool;

    private JoinSchema? _combinedSchema;
    private ColumnLookup? _rentedLookup;
    private RowBatch? _current;

    public JoinOutputWriter(ExecutionContext context, Pool pool)
    {
        _context = context;
        _pool = pool;
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
        EnsureRented(schema.ColumnLookup);
        _current!.Add(schema.CombinePooledValues(left, right, _pool));
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
        EnsureRented(row.ColumnLookup);
        DataValue[] copy = _pool.RentAndCopyDataValues(row, sourceArena, _current!.Arena);
        _current.Add(copy);
        return TakeIfFull();
    }

    /// <summary>
    /// Detaches and returns the in-progress batch (if any). Idempotent — calls
    /// after the first non-null return null. Safe to invoke from a
    /// <c>finally</c> block to recover ownership on exception paths.
    /// </summary>
    public RowBatch? Flush()
    {
        RowBatch? trailing = _current;
        _current = null;
        _rentedLookup = null;
        return trailing;
    }

    private void EnsureRented(ColumnLookup lookup)
    {
        if (_current is null)
        {
            _current = _context.RentRowBatch(lookup);
            _rentedLookup = lookup;
            return;
        }

        if (!ReferenceEquals(_rentedLookup, lookup))
        {
            throw new InvalidOperationException(
                "JoinOutputWriter received emits with different ColumnLookup references in the same batch — the writer assumes one shape per execution.");
        }
    }

    private RowBatch? TakeIfFull()
    {
        if (_current!.IsFull)
        {
            RowBatch ready = _current;
            _current = null;
            _rentedLookup = null;
            return ready;
        }
        return null;
    }
}
