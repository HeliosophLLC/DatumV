using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.GroupBy;

/// <summary>
/// Per-row workflow for keyed (non-global) hash aggregation. Composes the
/// four collaborators that make a keyed GROUP BY pipeline — hash table,
/// argument binder, optional spill coordinator, plan-wide accountant — and
/// routes each row through them.
/// </summary>
/// <remarks>
/// <para>
/// One instance per pipeline, constructed only when at least one GROUP BY
/// key is present. The operator's main loop reduces to a cancellation /
/// query-meter guard plus a single call to <see cref="ConsumeRowAsync"/>.
/// </para>
/// <para>
/// Reports per-group residency into the plan-wide
/// <see cref="MemoryAccountant"/> at insert time and consults
/// <see cref="MemoryAccountant.WouldExceedBudget"/> for spill decisions —
/// so a GroupBy entering an already-over-budget plan spills on its very
/// first new group instead of treating the budget as its own. References
/// — but does not own — the <see cref="IHashGroupTable"/>,
/// <see cref="AggregateArgumentBinder"/>, and optional
/// <see cref="SpillCoordinator"/> the operator keeps alive for the
/// emit / drain / disposal phases.
/// </para>
/// </remarks>
internal sealed class KeyedHashAggregator
{
    /// <summary>
    /// Approximate per-DataValue overhead in the in-RAM hash table — covers
    /// the GroupState's key array slot and any inline byte payloads. Matches
    /// the constant used elsewhere in the engine for structural residency.
    /// </summary>
    private const long DataValueOverheadBytes = 20;

    /// <summary>
    /// Approximate per-Dictionary entry overhead (hash, key ref, value ref,
    /// next pointer) plus the GroupState header.
    /// </summary>
    private const long PerGroupOverheadBytes = 64;

    /// <summary>
    /// Per-aggregate-accumulator state size estimate. Captures the typical
    /// sum/count/avg accumulator (two longs + a flag). Larger accumulators
    /// (e.g. ordered-buffer aggregates) under-report; that's acceptable for
    /// v1 budget triggering, which only needs structural-magnitude accuracy.
    /// </summary>
    private const long PerAccumulatorBytes = 32;

    private readonly ExecutionContext _context;
    private readonly IHashGroupTable _hashTable;
    private readonly AggregateArgumentBinder _binder;
    private readonly SpillCoordinator? _spillCoordinator;
    private readonly GroupStateFactory _groupStateFactory;
    private readonly long _perGroupBytes;
    private long _residentBytesNotified;

    public KeyedHashAggregator(
        ExecutionContext context,
        IHashGroupTable hashTable,
        AggregateArgumentBinder binder,
        SpillCoordinator? spillCoordinator,
        int groupByKeyCount,
        int aggregateCount,
        GroupStateFactory groupStateFactory)
    {
        _context = context;
        _hashTable = hashTable;
        _binder = binder;
        _spillCoordinator = spillCoordinator;
        _groupStateFactory = groupStateFactory;
        _perGroupBytes =
            DataValueOverheadBytes * groupByKeyCount
            + PerGroupOverheadBytes
            + PerAccumulatorBytes * aggregateCount;
    }

    /// <summary>
    /// Total bytes this aggregator has notified the accountant about so far.
    /// The owning <see cref="GroupByOperator"/> calls
    /// <see cref="MemoryAccountant.NotifyReleased"/> with this value in its
    /// <c>finally</c> block — single bulk release matches the operator's
    /// "hash table survives until dispose" lifecycle.
    /// </summary>
    public long ResidentBytesNotified => _residentBytesNotified;

    /// <summary>
    /// Consumes a single input row. Evaluates GROUP BY keys + aggregate
    /// arguments, then either stages into the spill coordinator's partition
    /// buffers (when already spilling) or looks up / creates the in-memory
    /// group and accumulates. New-group inserts notify the accountant; the
    /// next would-be insert triggers spill if it would push residency past
    /// the plan-wide budget.
    /// </summary>
    public async ValueTask ConsumeRowAsync(
        Row row,
        ExpressionEvaluator evaluator,
        Arena sourceArena,
        InvocationFrame accumFrame,
        CancellationToken cancellationToken)
    {
        await _hashTable.EvaluateAsync(evaluator, row, cancellationToken).ConfigureAwait(false);
        await _binder.EvaluateAsync(evaluator, row, cancellationToken).ConfigureAwait(false);

        if (_spillCoordinator is not null && _spillCoordinator.IsSpilling)
        {
            _spillCoordinator.StageRow(_hashTable, _binder, sourceArena);

            // Pre-existing in-memory groups still receive new rows so pre-spill
            // data is not lost. Drain skips these keys via partition-local dedup.
            if (_hashTable.TryGetExisting() is GroupState existing)
                _binder.AccumulateInto(existing, _context, in accumFrame);
            return;
        }

        GroupState? group = _hashTable.TryGetExisting();
        if (group is null)
        {
            // New group about to be inserted. Check the plan-wide budget
            // BEFORE we materialise — if adding this group's structural bytes
            // would push residency past the budget, spill instead.
            if (_spillCoordinator is not null
                && _context.Accountant.WouldExceedBudget(_perGroupBytes))
            {
                _spillCoordinator.BeginSpilling(
                    _context.Accountant.MemoryBudgetBytes ?? 0,
                    _context.Accountant.CurrentResidentBytes,
                    _hashTable.Count);
                // Restart this row through the spill path now that BeginSpilling
                // has flipped IsSpilling to true.
                _spillCoordinator.StageRow(_hashTable, _binder, sourceArena);
                return;
            }

            group = _groupStateFactory.Create(in accumFrame);
            _hashTable.InsertNew(group);
            _context.Accountant.NotifyMaterialized(_perGroupBytes);
            _residentBytesNotified += _perGroupBytes;
        }

        _binder.AccumulateInto(group, _context, in accumFrame);
    }
}
