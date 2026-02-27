using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.GroupBy;

/// <summary>
/// Per-row workflow for keyed (non-global) hash aggregation. Composes the
/// four collaborators that make a keyed GROUP BY pipeline — hash table,
/// argument binder, optional spill coordinator, optional memory estimator —
/// and routes each row through them.
/// </summary>
/// <remarks>
/// <para>
/// One instance per pipeline, constructed only when at least one GROUP BY
/// key is present. The operator's main loop reduces to a cancellation /
/// query-meter guard plus a single call to <see cref="ConsumeRowAsync"/>.
/// </para>
/// <para>
/// Owns the <see cref="MemoryEstimator"/> (created iff a memory budget was
/// supplied) and the spill-trigger decision. References — but does not own — the
/// <see cref="IHashGroupTable"/>, <see cref="AggregateArgumentBinder"/>, and
/// optional <see cref="SpillCoordinator"/> the operator keeps alive for the
/// emit / drain / disposal phases.
/// </para>
/// </remarks>
internal sealed class KeyedHashAggregator
{
    private readonly ExecutionContext _context;
    private readonly IHashGroupTable _hashTable;
    private readonly AggregateArgumentBinder _binder;
    private readonly SpillCoordinator? _spillCoordinator;
    private readonly MemoryEstimator? _estimator;
    private readonly long? _memoryBudget;
    private readonly GroupStateFactory _groupStateFactory;

    public KeyedHashAggregator(
        ExecutionContext context,
        IHashGroupTable hashTable,
        AggregateArgumentBinder binder,
        SpillCoordinator? spillCoordinator,
        long? memoryBudget,
        GroupStateFactory groupStateFactory)
    {
        _context = context;
        _hashTable = hashTable;
        _binder = binder;
        _spillCoordinator = spillCoordinator;
        _memoryBudget = memoryBudget;
        _groupStateFactory = groupStateFactory;
        _estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
    }

    /// <summary>
    /// Consumes a single input row. Evaluates GROUP BY keys + aggregate
    /// arguments, then either stages into the spill coordinator's partition
    /// buffers (when already spilling) or looks up / creates the in-memory
    /// group and accumulates. After each accumulation, samples for memory
    /// pressure and triggers spill or escalation as appropriate.
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
            group = _groupStateFactory.Create(in accumFrame);
            _hashTable.InsertNew(group);
        }

        _binder.AccumulateInto(group, _context, in accumFrame);

        if (_estimator is not null)
        {
            if (_estimator.ShouldSample())
                _estimator.RecordSample(row);
            _estimator.IncrementRowCount();
            long groupCount = _hashTable.Count;
            long estimatedMemory = _estimator.EstimateBytesForRowCount(groupCount);

            if (estimatedMemory > _memoryBudget!.Value)
            {
                _spillCoordinator!.BeginSpilling(_memoryBudget.Value, estimatedMemory, groupCount);
            }
            else if (estimatedMemory > (long)(_memoryBudget.Value * MemoryEstimator.EscalationThreshold))
            {
                _estimator.EscalateToEveryRow();
            }
        }
    }
}
