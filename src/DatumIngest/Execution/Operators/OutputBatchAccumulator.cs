using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Shared plumbing for an operator's output side: rent a <see cref="RowBatch"/>
/// on the first add, track <see cref="RowBatch.IsFull"/>, detach when full,
/// and flush the trailing batch in a <c>finally</c>. Subclasses add their
/// domain-specific row-building logic (combine two rows for joins, stamp
/// aggregate results for GROUP BY, copy from an input batch for scans).
/// </summary>
/// <remarks>
/// <para>
/// The base class is the answer to "every operator's output side duplicates
/// the same 30 lines of rent/IsFull/yield". Subclasses inherit
/// <see cref="EnsureRentedAndGetCurrent"/> and <see cref="TakeIfFull"/> as
/// protected helpers, expose their own <c>Add</c>/<c>Emit</c> methods that
/// build a <see cref="DataValue"/>[] in whatever shape they emit, and call
/// the base helpers to manage batch lifecycle. <see cref="Flush"/> is the
/// shared finalizer — public so callers can drain at end-of-iteration and
/// recover ownership on exception paths.
/// </para>
/// <para>
/// The base assertion in <see cref="EnsureRentedAndGetCurrent"/> guards
/// against schema drift mid-execution: a subclass that emits rows with two
/// different <see cref="ColumnLookup"/> references into the same batch
/// would silently corrupt the output, so the second emit's
/// <see cref="object.ReferenceEquals(object?, object?)"/> mismatch throws.
/// Subclasses whose schema is set once at construction never see this fire
/// (the assertion is structurally cheap when not violated).
/// </para>
/// </remarks>
internal abstract class OutputBatchAccumulator
{
    private readonly ExecutionContext _context;
    private readonly Pool _pool;
    private ColumnLookup? _rentedLookup;
    private RowBatch? _current;

    protected OutputBatchAccumulator(ExecutionContext context)
    {
        _context = context;
        _pool = context.Pool;
    }

    /// <summary>The execution context used to rent output batches.</summary>
    protected ExecutionContext Context => _context;

    /// <summary>The pool used by subclasses for arena/data-value rentals.</summary>
    protected Pool Pool => _pool;

    /// <summary>
    /// Whether an in-progress batch is currently held by the accumulator. Used by
    /// operators with a pass-through fast path (e.g. LIMIT yielding a full source
    /// batch directly) to detect that no pending output would need to be flushed
    /// first — preserving batch-boundary semantics across the migration.
    /// </summary>
    public bool HasPendingBatch => _current is not null;

    /// <summary>
    /// Rents an output batch with <paramref name="lookup"/> if none is open,
    /// or returns the in-progress batch. Throws if the in-progress batch's
    /// lookup reference does not equal <paramref name="lookup"/>.
    /// </summary>
    protected RowBatch EnsureRentedAndGetCurrent(ColumnLookup lookup)
    {
        if (_current is null)
        {
            _current = _context.RentRowBatch(lookup);
            _rentedLookup = lookup;
            return _current;
        }

        if (!ReferenceEquals(_rentedLookup, lookup))
        {
            throw new InvalidOperationException(
                $"{GetType().Name} received a row whose ColumnLookup differs from the open batch's.");
        }
        return _current;
    }

    /// <summary>
    /// Detaches and returns the in-progress batch when it is full; otherwise
    /// returns <see langword="null"/>. Subclasses call this at the end of
    /// every emit method so callers can <c>yield return</c> the full batch
    /// without losing ownership.
    /// </summary>
    protected RowBatch? TakeIfFull()
    {
        if (_current is { IsFull: true } ready)
        {
            _current = null;
            _rentedLookup = null;
            return ready;
        }
        return null;
    }

    /// <summary>
    /// Emits a row whose <see cref="DataValue"/>[] is already pool-rented and stabilised,
    /// transferring ownership to the output batch with no copy. For operators that
    /// materialise rows ahead of time (top-N heap, reservoir sampling, index-seek prefetch),
    /// these rows already own pool-rented arrays — re-renting via a copy-style emit would
    /// double-charge the pool.
    /// </summary>
    /// <remarks>
    /// The caller MUST NOT call <c>Pool.ReturnRow</c> on the row afterwards; the output
    /// batch's recycle (via the consumer) reclaims the array exactly once. Returns the
    /// in-progress batch detached from the writer when it fills, or <see langword="null"/>
    /// when not full — same contract as the copy-style <c>Add</c> methods.
    /// </remarks>
    public RowBatch? Adopt(ColumnLookup outputLookup, Row row)
    {
        RowBatch current = EnsureRentedAndGetCurrent(outputLookup);
        current.Add(row.RawValues);
        return TakeIfFull();
    }

    /// <summary>
    /// Detaches and returns the in-progress batch (if any) regardless of
    /// fill state. Idempotent — subsequent calls return <see langword="null"/>.
    /// Safe to call from a <c>finally</c> block to recover ownership on
    /// exception paths.
    /// </summary>
    public RowBatch? Flush()
    {
        RowBatch? trailing = _current;
        _current = null;
        _rentedLookup = null;
        return trailing;
    }
}
