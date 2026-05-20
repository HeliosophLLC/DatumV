using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution.Operators.GroupBy;

/// <summary>
/// Builds <see cref="GroupState"/> instances for a GROUP BY pipeline. Owns
/// the DISTINCT memory-budget split, the estimated-distinct-count-per-group
/// pre-sizing hint, and the runtime cache that lets pooled accumulators be
/// reused across <see cref="Create"/> calls without re-instantiation.
/// </summary>
/// <remarks>
/// <para>
/// One instance per pipeline execution. Thread-safe: <see cref="Create"/>
/// may be called concurrently (e.g. by the parallel global-aggregation
/// workers), and the accumulator-type cache is filled via
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>.
/// </para>
/// <para>
/// The DISTINCT memory-budget split (computed in the constructor) is what
/// lets <see cref="DistinctAccumulatorDecorator"/> spill its hash set to
/// disk under pressure. Streaming aggregation passes
/// <c>memoryBudget: null</c> to opt out of the split — its single-group-
/// at-a-time mode does not benefit from per-group bookkeeping.
/// </para>
/// </remarks>
internal sealed class GroupStateFactory
{
    private readonly Pool _pool;
    private readonly ExecutionContext _context;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;

    /// <summary>
    /// Per-accumulator DISTINCT memory budget. <c>null</c> disables
    /// spill-to-disk for DISTINCT hash sets.
    /// </summary>
    private readonly long? _distinctMemoryBudgetBytes;

    /// <summary>
    /// Estimated number of distinct values per group. Pre-sizes the DISTINCT
    /// hash set so its first inserts don't trigger resize doublings.
    /// </summary>
    private readonly int _estimatedDistinctCountPerGroup;

    /// <summary>
    /// Cached <see cref="RuntimeTypeHandle"/> per aggregate column of the
    /// inner accumulator type (before DISTINCT decoration). Populated by
    /// the first <see cref="Create"/> call via
    /// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>; subsequent
    /// calls reuse pooled accumulators whose type handle matches.
    /// </summary>
    private RuntimeTypeHandle[]? _accumulatorInnerTypes;

    public GroupStateFactory(
        Pool pool,
        ExecutionContext context,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        long? memoryBudget = null,
        long? estimatedSourceRowCount = null,
        bool isGlobalAggregation = false)
    {
        _pool = pool;
        _context = context;
        _aggregateColumns = aggregateColumns;

        int initialCapacity = estimatedSourceRowCount.HasValue
            ? (int)Math.Min(estimatedSourceRowCount.Value, int.MaxValue / 2)
            : 16;

        // For DISTINCT aggregates, compute a per-accumulator memory budget so
        // the DistinctAccumulatorDecorator can spill to disk when its hash set
        // grows beyond the limit.
        //
        // For global aggregation (no GROUP BY), the full budget is split across
        // the distinct aggregate count. For keyed aggregation, the budget is
        // divided by an assumed concurrent-DISTINCT-set count (256) so total
        // memory across groups stays within the overall budget while avoiding
        // the pathological case where a handful of groups accumulate millions
        // of entries.
        if (memoryBudget.HasValue)
        {
            int distinctAggregateCount = 0;
            for (int i = 0; i < aggregateColumns.Count; i++)
            {
                if (aggregateColumns[i].Distinct) distinctAggregateCount++;
            }

            if (distinctAggregateCount > 0)
            {
                if (isGlobalAggregation)
                {
                    _distinctMemoryBudgetBytes = memoryBudget.Value / distinctAggregateCount;
                }
                else
                {
                    const long MaxAssumedGroups = 256;
                    _distinctMemoryBudgetBytes = memoryBudget.Value / MaxAssumedGroups / distinctAggregateCount;
                }
            }
        }

        // Pre-size DISTINCT hash sets based on estimated distinct values per group.
        // Cap at 1M to avoid over-allocating for skewed data.
        bool anyDistinct = false;
        for (int i = 0; i < aggregateColumns.Count; i++)
        {
            if (aggregateColumns[i].Distinct) { anyDistinct = true; break; }
        }
        if (anyDistinct && estimatedSourceRowCount.HasValue)
        {
            long divisor = isGlobalAggregation ? 1 : Math.Max(initialCapacity, 256);
            _estimatedDistinctCountPerGroup = (int)Math.Min(
                estimatedSourceRowCount.Value / divisor, 1_000_000);
        }
    }

    /// <summary>
    /// Creates a fresh <see cref="GroupState"/>, reusing pooled accumulators
    /// when their inner type matches the cache. DISTINCT accumulators are
    /// wrapped in <see cref="DistinctAccumulatorDecorator"/> using the
    /// pre-computed memory budget.
    /// </summary>
    public GroupState Create(in InvocationFrame frame)
    {
        int count = _aggregateColumns.Count;
        GroupState state = _pool.Backing.RentGroupState(count);
        RuntimeTypeHandle[]? innerTypes = _accumulatorInnerTypes;

        for (int index = 0; index < count; index++)
        {
            AggregateColumn column = _aggregateColumns[index];
            IAggregateAccumulator? existing = state.Accumulators[index];
            IAggregateAccumulator? accumulator = null;

            // Try to reuse the accumulator left in the pooled array from the
            // previous owner. A type-handle comparison avoids creating fresh
            // objects for the overwhelmingly common same-operator-shape case.
            // Pooled decorators are not reused when a memory budget is active
            // because the budget is context-specific and the pooled instance
            // may carry stale spill state.
            if (innerTypes is not null && existing is not null)
            {
                if (!column.Distinct
                    && existing is not DistinctAccumulatorDecorator
                    && existing.GetType().TypeHandle.Equals(innerTypes[index]))
                {
                    existing.Reset();
                    accumulator = existing;
                }
                else if (column.Distinct
                         && _distinctMemoryBudgetBytes is null
                         && existing is DistinctAccumulatorDecorator decorator
                         && decorator.InnerAccumulator.GetType().TypeHandle.Equals(innerTypes[index]))
                {
                    existing.Reset();
                    accumulator = existing;
                }
            }

            if (accumulator is null)
            {
                accumulator = column.Function.CreateAccumulator();

                if (column.Distinct)
                {
                    accumulator = new DistinctAccumulatorDecorator(
                        accumulator,
                        column.ArgumentExpressions.Count,
                        in frame,
                        _context,
                        _distinctMemoryBudgetBytes,
                        _distinctMemoryBudgetBytes.HasValue
                            ? column.Function.CreateAccumulator
                            : null,
                        _estimatedDistinctCountPerGroup);
                }
            }

            state.Accumulators[index] = accumulator;

            if (column.OrderBy is not null)
            {
                state.OrderedBuffers ??= new OrderedAggregateBuffer?[count];
                state.OrderedBuffers[index] = new OrderedAggregateBuffer(
                    column.ArgumentExpressions.Count, column.OrderBy.Count);
            }
        }

        if (innerTypes is null)
        {
            RuntimeTypeHandle[] newTypes = new RuntimeTypeHandle[count];
            for (int i = 0; i < count; i++)
            {
                IAggregateAccumulator accumulator = state.Accumulators[i];
                if (accumulator is DistinctAccumulatorDecorator decorator)
                {
                    newTypes[i] = decorator.InnerAccumulator.GetType().TypeHandle;
                }
                else
                {
                    newTypes[i] = accumulator.GetType().TypeHandle;
                }
            }

            Interlocked.CompareExchange(ref _accumulatorInnerTypes, newTypes, null);
        }

        return state;
    }
}
