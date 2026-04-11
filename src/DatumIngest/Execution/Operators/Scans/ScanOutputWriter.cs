using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Owns the output-batch lifecycle for a <see cref="ScanOperator"/>: renting,
/// adding rows, tracking <see cref="RowBatch.IsFull"/>, detaching when full,
/// and flushing the trailing batch. Hides the
/// <c>rent ??= ... → RentAndCopyToOutput → IsFull → yield</c> pattern that
/// repeats at every emit site in the index-pruning path.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>JoinOutputWriter</c>, a scan only ever emits in one shape — every
/// added row comes from a provider batch whose <see cref="RowBatch.ColumnLookup"/>
/// reflects the scan's projection. The writer still asserts
/// <see cref="object.ReferenceEquals(object?, object?)"/> on the lookup as a
/// defensive check: a provider unexpectedly switching schemas mid-stream would
/// silently corrupt the output without it.
/// </para>
/// <para>
/// <see cref="Add"/> returns a non-null <see cref="RowBatch"/> when the
/// current batch fills (caller must <c>yield return</c> it) or
/// <see langword="null"/> otherwise. <see cref="Flush"/> returns the in-progress
/// batch (if any) and detaches it from the writer; safe to call from a
/// <c>finally</c> block to recover ownership on exception paths.
/// </para>
/// </remarks>
internal sealed class ScanOutputWriter
{
    private readonly ExecutionContext _context;
    private readonly Pool _pool;

    private ColumnLookup? _rentedLookup;
    private RowBatch? _current;

    public ScanOutputWriter(ExecutionContext context)
    {
        _context = context;
        _pool = context.Pool;
    }

    /// <summary>
    /// Copies row <paramref name="sourceIndex"/> from <paramref name="sourceBatch"/>
    /// into the current output batch, renting from the context if necessary.
    /// Returns the in-progress batch detached from the writer when it fills, or
    /// <see langword="null"/> when not full.
    /// </summary>
    public RowBatch? Add(RowBatch sourceBatch, int sourceIndex)
    {
        EnsureRented(sourceBatch.ColumnLookup);
        _pool.RentAndCopyToOutput(sourceBatch, sourceIndex, _current!);
        return TakeIfFull();
    }

    /// <summary>
    /// Detaches and returns the in-progress batch (if any). Idempotent.
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
                "ScanOutputWriter received rows whose ColumnLookup differs from the rented batch — " +
                "the provider unexpectedly switched schemas mid-stream.");
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
