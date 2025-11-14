using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Aggregation operator that groups input rows by one or more key expressions
/// and computes aggregate function results per group.
/// <para>
/// When <see cref="StreamingSorted"/> is <c>false</c> (default), operates in
/// hash mode: all input rows must be consumed before any output rows are emitted.
/// When <see cref="ExecutionContext.MemoryBudgetBytes"/> is set, the operator
/// spills raw input rows to hash-partitioned disk files when estimated memory
/// usage exceeds the budget.
/// </para>
/// <para>
/// When <see cref="StreamingSorted"/> is <c>true</c>, the operator assumes input
/// rows arrive pre-sorted on the GROUP BY keys and emits each completed group
/// as soon as the key changes. This enables a downstream <see cref="LimitOperator"/>
/// to short-circuit after the desired number of groups without processing the
/// entire input.
/// </para>
/// </summary>
public sealed class GroupByOperator : IQueryOperator, IDisposable
{
    /// <summary>Number of hash partitions used when spilling to disk.</summary>
    private const int SpillPartitionCount = 64;

    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private readonly bool _streamingSorted;

    /// <summary>
    /// Per-accumulator memory budget for DISTINCT hash sets. Computed once at the
    /// start of <see cref="ExecuteHashAsync"/> and used by <see cref="CreateGroupState"/>.
    /// <c>null</c> disables spill-to-disk for DISTINCT sets.
    /// </summary>
    private long? _distinctMemoryBudgetBytes;

    /// <summary>
    /// Estimated number of distinct values per group for DISTINCT aggregates.
    /// Used to pre-size the <see cref="HashSet{T}"/> in
    /// <see cref="DistinctAccumulatorDecorator"/> and avoid repeated resize
    /// doublings that generate Gen2 garbage.
    /// </summary>
    private int _estimatedDistinctCountPerGroup;

    /// <summary>
    /// Cached <see cref="RuntimeTypeHandle"/> of each aggregate column's inner
    /// accumulator type (before <see cref="DistinctAccumulatorDecorator"/> wrapping).
    /// Built lazily on the first <see cref="CreateGroupState"/> call so that
    /// subsequent calls can rent pooled accumulators by type without creating
    /// throwaway instances.
    /// </summary>
    private RuntimeTypeHandle[]? _accumulatorInnerTypes;

    /// <summary>
    /// Creates a GROUP BY operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="groupByExpressions">
    /// The GROUP BY key expressions. May be empty for global aggregation
    /// (e.g. <c>SELECT COUNT(*) FROM t</c>).
    /// </param>
    /// <param name="aggregateColumns">
    /// The aggregate function calls with their output column names.
    /// </param>
    /// <param name="streamingSorted">
    /// When <c>true</c>, the operator assumes input is pre-sorted on the GROUP BY
    /// keys and emits groups one at a time as the key changes, enabling LIMIT
    /// short-circuit. Defaults to <c>false</c> (hash aggregation).
    /// </param>
    public GroupByOperator(
        IQueryOperator source,
        IReadOnlyList<Expression> groupByExpressions,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        bool streamingSorted = false)
    {
        _source = source;
        _groupByExpressions = groupByExpressions;
        _aggregateColumns = aggregateColumns;
        _streamingSorted = streamingSorted;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The GROUP BY key expressions.</summary>
    public IReadOnlyList<Expression> GroupByExpressions => _groupByExpressions;

    /// <summary>The aggregate columns being computed.</summary>
    public IReadOnlyList<AggregateColumn> AggregateColumns => _aggregateColumns;

    /// <summary>
    /// Whether the operator assumes pre-sorted input and emits groups
    /// one at a time (streaming) instead of materializing all groups (hash).
    /// </summary>
    public bool StreamingSorted => _streamingSorted;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new();

        if (_groupByExpressions.Count > 0)
        {
            properties["keys"] = string.Join(", ",
                _groupByExpressions.Select(QueryExplainer.FormatExpression));
        }
        else
        {
            properties["keys"] = "(global)";
        }

        properties["aggregates"] = string.Join(", ",
            _aggregateColumns.Select(aggregate => $"{aggregate.Function.Name}() AS {aggregate.OutputName}"));

        string operatorName = _streamingSorted ? "Streaming Group By" : "Group By";

        return new OperatorPlanDescription(operatorName)
        {
            Properties = properties,
            Children = [(Source, null)],
            Warnings = _streamingSorted ? [] : ["materializes all rows per group"],
            Annotations = _streamingSorted ? ["sorted input \u2014 streaming one group at a time"] : [],
        };
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // GroupBy is a blocking operator — it must consume ALL input before
        // emitting any output. A downstream RowLimit cannot short-circuit the
        // child operators, so strip it to prevent children (e.g. JoinOperator)
        // from picking strategies like index nested-loop that only pay off when
        // the consumer needs few rows.
        if (context.RowLimit is not null)
        {
            context = new ExecutionContext(context) { RowLimit = null };
        }

        if (_streamingSorted)
        {
            return ExecuteStreamingAsync(context);
        }

        return ExecuteHashAsync(context);
    }

    /// <summary>
    /// Streaming aggregation path: emits each completed group as soon as the
    /// GROUP BY key changes. Requires input sorted on the GROUP BY keys.
    /// Memory usage is O(1) per group — no hash table or spill infrastructure.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteStreamingAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);

        bool useSingleKey = _groupByExpressions.Count == 1;

        ColumnLookup? outputLookup = null;
        Arena? operatorArena = null;

        GroupState? currentGroup = null;
        DataValue? currentSingleKey = null;
        DataValue[]? currentKeyValues = null;

        (DataValue[][] argumentScratch, DataValue[]?[]? sortKeyScratch) = CreateAggregateArgumentScratch();

        // Reusable scratch buffer for composite key evaluation — avoids a
        // per-row DataValue[] allocation in the multi-key path. Only the keys
        // for the first row of each new group are copied to permanent storage.
        int keyCount = _groupByExpressions.Count;
        DataValue[]? compositeKeyScratch = (!useSingleKey) ? new DataValue[keyCount] : null;

        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (inputBatch.Count == 0) continue;

                    operatorArena ??= pool.RentArena();

                    InvocationFrame frame = new(
                        inputBatch.Arena,
                        operatorArena,
                        context.SidecarRegistry);

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row row = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        // Evaluate group keys and detect key change.
                        if (useSingleKey)
                        {
                            DataValue key = evaluator.Evaluate(_groupByExpressions[0], row);

                            if (currentGroup is not null && !key.Equals(currentSingleKey!))
                            {
                                FlushOrderedBuffersForGroup(currentGroup, context, in frame);
                                Row emitted = EmitGroupRow(currentGroup, isGlobalAggregation: false,
                                    pool, ref outputLookup, in frame);
                                outputBatch ??= pool.RentRowBatch(outputLookup!, context.BatchSize, operatorArena!);
                                outputBatch.Add(emitted.RawValues);
                                pool.Backing.Return(currentGroup);
                                currentGroup = null;
                                if (outputBatch.IsFull)
                                {
                                    RowBatch toYield = outputBatch;
                                    outputBatch = null;
                                    yield return toYield;
                                }
                            }

                            if (currentGroup is null)
                            {
                                currentGroup = CreateGroupState(pool, in frame);
                                currentGroup.KeyValues = [key];
                                currentSingleKey = key;
                            }
                        }
                        else
                        {
                            for (int index = 0; index < keyCount; index++)
                            {
                                compositeKeyScratch![index] = evaluator.Evaluate(_groupByExpressions[index], row);
                            }

                            if (currentGroup is not null && !CompositeKeysEqual(currentKeyValues!, compositeKeyScratch!))
                            {
                                FlushOrderedBuffersForGroup(currentGroup, context, in frame);
                                Row emitted = EmitGroupRow(currentGroup, isGlobalAggregation: false,
                                    pool, ref outputLookup, in frame);
                                outputBatch ??= pool.RentRowBatch(outputLookup!, context.BatchSize, operatorArena!);
                                outputBatch.Add(emitted.RawValues);
                                pool.Backing.Return(currentGroup);
                                currentGroup = null;
                                if (outputBatch.IsFull)
                                {
                                    RowBatch toYield = outputBatch;
                                    outputBatch = null;
                                    yield return toYield;
                                }
                            }

                            if (currentGroup is null)
                            {
                                // Copy scratch into permanent storage only at group boundaries.
                                DataValue[] permanentKey = compositeKeyScratch!.AsSpan(0, keyCount).ToArray();
                                currentGroup = CreateGroupState(pool, in frame);
                                currentGroup.KeyValues = permanentKey;
                                currentKeyValues = permanentKey;
                            }
                        }

                        // Evaluate and accumulate aggregate arguments using reusable scratch buffers.
                        EvaluateAggregateArgumentsInto(evaluator, row, argumentScratch, sortKeyScratch);
                        AccumulateRow(currentGroup, argumentScratch, sortKeyScratch, context, in frame);
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(inputBatch);
                }
            }

            // Emit the final group.
            if (currentGroup is not null)
            {
                operatorArena ??= pool.RentArena();
                InvocationFrame trailingFrame = new(operatorArena, operatorArena, context.SidecarRegistry);
                FlushOrderedBuffersForGroup(currentGroup, context, in trailingFrame);
                Row emitted = EmitGroupRow(currentGroup, isGlobalAggregation: false,
                    pool, ref outputLookup, in trailingFrame);
                outputBatch ??= pool.RentRowBatch(outputLookup!, context.BatchSize, operatorArena);
                outputBatch.Add(emitted.RawValues);
                pool.Backing.Return(currentGroup);
                currentGroup = null;
            }

            if (outputBatch is not null)
            {
                RowBatch toYield = outputBatch;
                outputBatch = null;
                yield return toYield;
            }
        }
        finally
        {
            if (currentGroup is not null) pool.Backing.Return(currentGroup);
            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (operatorArena is not null) pool.ReturnArena(operatorArena);
        }
    }


    /// <summary>
    /// Walks through transparent operator wrappers to find the underlying
    /// <see cref="ScanOperator"/> and returns its estimated row count.
    /// Returns <see langword="null"/> when the tree does not bottom out at a scan.
    /// </summary>
    private long? GetEstimatedSourceRowCount()
    {
        IQueryOperator current = _source;
        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    return scan.TableRowCount;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case JoinOperator join:
                    // The probe (left) side determines the output row count for
                    // INNER/LEFT joins. Use it as a conservative lower bound.
                    current = join.Left;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Hash-based aggregation. For <c>DegreeOfParallelism &gt; 1</c> uses
    /// key-partitioned fan-out: the feeder evaluates each row's GROUP BY keys
    /// and routes the row to the worker whose partition index matches
    /// <c>(uint)keyHash % workerCount</c>. Each worker maintains a hash table
    /// over a disjoint subset of the key space, so no cross-worker duplication
    /// occurs and no merge phase is needed. Global aggregation (no GROUP BY)
    /// falls back to a shared channel with round-robin fan-out and merges
    /// per-worker partials at the end.
    /// <para>
    /// At <c>DegreeOfParallelism = 1</c> the same worker/channel machinery
    /// runs with a single worker; the channel hop is the only added overhead
    /// versus a direct single-threaded loop.
    /// </para>
    /// <para>
    /// Supports spill-to-disk: each worker is allocated
    /// <c>MemoryBudgetBytes / workerCount</c> bytes and writes raw input rows
    /// to hash-partitioned per-worker disk files when its share is exceeded;
    /// rows are re-aggregated in a drain phase after input is exhausted.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteHashAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        bool useSingleKey = _groupByExpressions.Count == 1;
        bool isGlobalAggregation = _groupByExpressions.Count == 0;
        CancellationToken cancellationToken = context.CancellationToken;
        Arena? operatorArena = null;

        InitializeDistinctBudgets(context, isGlobalAggregation);

        // Acquire worker slots from the optional global budget.
        int desiredWorkers = context.DegreeOfParallelism;
        int acquiredFromBudget = 0;

        // Spilled DISTINCT sets cannot be deduplicated across parallel workers
        // during merge, so DISTINCT-with-budget falls back to single-threaded.
        bool hasDistinctWithBudget = context.MemoryBudgetBytes.HasValue
            && _aggregateColumns.Any(column => column.Distinct);
        if (hasDistinctWithBudget)
        {
            desiredWorkers = 1;
        }

        // Keyed aggregation always uses the single-worker fast path. The
        // multi-worker keyed code in this method has a per-row race (the
        // routing feeder can return input batches before workers finish
        // reading channelled Rows that reference the batch's DataValue[]),
        // and the parallel keyed path was already gated off in the prior
        // dispatcher as consistently slower than serial. Multi-worker remains
        // useful for global aggregation, where workers consume the same
        // channel without per-row routing.
        if (_groupByExpressions.Count > 0)
        {
            desiredWorkers = 1;
        }

        if (context.ParallelismBudget is ParallelismBudget budget)
        {
            acquiredFromBudget = budget.TryAcquire(desiredWorkers);
            desiredWorkers = Math.Max(1, acquiredFromBudget);
        }

        try
        {
            int workerCount = desiredWorkers;

            // ----------------------------------------------------------------
            // Single-worker fast path: read input batches synchronously into
            // one hash table without channel marshalling. Avoids the per-row
            // race where the parallel feeder returns input batches before
            // workers finish reading rows from the channel (Row in channel
            // references DataValue[] owned by the returned batch).
            // ----------------------------------------------------------------
            if (workerCount == 1)
            {
                await foreach (RowBatch fastPathBatch in ExecuteHashSingleWorkerAsync(
                    context, pool, useSingleKey, isGlobalAggregation).ConfigureAwait(false))
                {
                    yield return fastPathBatch;
                }
                yield break;
            }

            // ----------------------------------------------------------------
            // Global aggregation path: no GROUP BY key to route on.
            // Distribute rows round-robin across all workers so each accumulates
            // a partial global result, then merge into worker 0 at the end.
            // ----------------------------------------------------------------
            if (isGlobalAggregation)
            {
                Channel<Row> globalChannel = Channel.CreateBounded<Row>(
                    new BoundedChannelOptions(workerCount * 64)
                    {
                        SingleWriter = true,
                        SingleReader = false,
                    });

                // Worker accumulator state lives in context.Store (per-query, long-lived);
                // worker-side Source is filled in per-row from each batch's arena before
                // the channel hop, but the worker reads from context.Store after the
                // batch is returned, so we use context.Store for both.
                InvocationFrame workerAccumFrame = InvocationFrame.Symmetric(
                    context.Store, context.SidecarRegistry);

                GroupState[] workerGlobalGroups = new GroupState[workerCount];
                for (int i = 0; i < workerCount; i++)
                {
                    workerGlobalGroups[i] = CreateGroupState(pool, in workerAccumFrame);
                }

                Task globalFeeder = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
                        {
                            for (int i = 0; i < inputBatch.Count; i++)
                            {
                                context.QueryMeter?.ThrowIfExceeded();
                                await globalChannel.Writer.WriteAsync(inputBatch[i], cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            pool.ReturnRowBatch(inputBatch);
                        }
                    }
                    finally
                    {
                        globalChannel.Writer.Complete();
                    }
                }, cancellationToken);

                Task[] globalWorkers = new Task[workerCount];
                for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
                {
                    int wi = workerIndex;
                    globalWorkers[wi] = Task.Run(async () =>
                    {
                        ExpressionEvaluator workerEvaluator = new(
                            context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);

                        (DataValue[][] workerArgScratch, DataValue[]?[]? workerSortScratch) =
                            CreateAggregateArgumentScratch();

                        await foreach (Row row in globalChannel.Reader.ReadAllAsync(cancellationToken)
                            .ConfigureAwait(false))
                        {
                            EvaluateAggregateArgumentsInto(
                                workerEvaluator, row, workerArgScratch, workerSortScratch);
                            AccumulateRow(workerGlobalGroups[wi], workerArgScratch, workerSortScratch, context, in workerAccumFrame);
                            // Row values extracted — batch-level ReturnBatch handles array return.
                        }
                    }, cancellationToken);
                }

                await globalFeeder.ConfigureAwait(false);
                await Task.WhenAll(globalWorkers).ConfigureAwait(false);

                for (int i = 1; i < workerCount; i++)
                {
                    MergeGroupState(workerGlobalGroups[0], workerGlobalGroups[i]);
                }

                ColumnLookup? globalOutputLookup = null;

                bool globalHasOrderedAggregates = _aggregateColumns.Any(
                    column => column.OrderBy is not null);

                operatorArena ??= pool.RentArena();
                InvocationFrame globalEmitFrame = new(
                    context.Store, operatorArena, context.SidecarRegistry);

                if (globalHasOrderedAggregates)
                {
                    FlushOrderedBuffers([workerGlobalGroups[0]], context, in globalEmitFrame);
                }

                Row globalEmitted = EmitGroupRow(
                    workerGlobalGroups[0], isGlobalAggregation: true,
                    pool, ref globalOutputLookup, in globalEmitFrame);
                RowBatch globalOutputBatch = pool.RentRowBatch(globalOutputLookup!, context.BatchSize, operatorArena);
                globalOutputBatch.Add(globalEmitted.RawValues);
                yield return globalOutputBatch;

                yield break;
            }

        }
        finally
        {
            if (acquiredFromBudget > 0)
            {
                context.ParallelismBudget!.Release(acquiredFromBudget);
            }

            if (operatorArena is not null) pool.ReturnArena(operatorArena);
        }
    }

    /// <summary>
    /// Single-worker hash aggregation: reads input batches synchronously into one
    /// hash table (or one global GroupState) and spills via <see cref="SpillReaderWriter"/>
    /// when the memory budget is exceeded. No channels, no Tasks — avoids the per-row
    /// race in the multi-worker path where the feeder may return an input batch before
    /// workers finish reading rows from the channel.
    /// </summary>
    /// <remarks>
    /// Spill flow: each row whose hash routes to partition <c>p</c> is staged into
    /// <c>partitionBuffers[p]</c> (a <see cref="RowBatch"/> over a per-operator
    /// <c>bufferArena</c>) and flushed to the spiller when full. The spiller stabilises
    /// into its consolidated arena, so spilled values resolve correctly during replay
    /// regardless of which input batch produced them. Drain replays each partition,
    /// builds a partition-local hash table, and emits one row per group, skipping keys
    /// already represented in the in-memory table.
    /// </remarks>
    private async IAsyncEnumerable<RowBatch> ExecuteHashSingleWorkerAsync(
        ExecutionContext context,
        Pool pool,
        bool useSingleKey,
        bool isGlobalAggregation)
    {
        Arena? operatorArena = null;
        Arena? bufferArena = null;
        ExpressionEvaluator evaluator = new(
            context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);

        Dictionary<DataValue, GroupState>? singleKeyTable = useSingleKey && !isGlobalAggregation
            ? new Dictionary<DataValue, GroupState>() : null;
        Dictionary<CompositeKey, GroupState>? compositeKeyTable = !useSingleKey && !isGlobalAggregation
            ? new Dictionary<CompositeKey, GroupState>(CompositeKeyComparer.Instance) : null;

        // Long-lived frame captured by accumulators that need a stable Target arena
        // (e.g. DistinctAccumulatorDecorator's _capturedFrame for replay merges).
        // context.Store survives the query's lifetime.
        InvocationFrame initFrame = InvocationFrame.Symmetric(context.Store, context.SidecarRegistry);
        GroupState? globalGroup = isGlobalAggregation ? CreateGroupState(pool, in initFrame) : null;

        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue && !isGlobalAggregation
            ? new MemoryEstimator() : null;

        SpillReaderWriter? spiller = null;
        ColumnLookup? spillSchema = null;
        RowBatch?[]? partitionBuffers = null;
        bool spilling = false;

        (DataValue[][] argumentScratch, DataValue[]?[]? sortKeyScratch) = CreateAggregateArgumentScratch();
        int keyCount = _groupByExpressions.Count;
        DataValue[]? compositeKeyScratch = (!useSingleKey && !isGlobalAggregation) ? new DataValue[keyCount] : null;

        ColumnLookup? outputLookup = null;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (inputBatch.Count == 0) continue;

                    // Per-batch accumulation frame: Source = batch arena (where this row's
                    // arena-backed values resolve), Target = context.Store (long-lived for
                    // anything an accumulator wants to persist across batches).
                    InvocationFrame accumFrame = new(
                        inputBatch.Arena, context.Store, context.SidecarRegistry);

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row row = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        DataValue singleKey = default;
                        if (useSingleKey)
                        {
                            singleKey = evaluator.Evaluate(_groupByExpressions[0], row);
                        }
                        else if (!isGlobalAggregation)
                        {
                            for (int k = 0; k < keyCount; k++)
                            {
                                compositeKeyScratch![k] = evaluator.Evaluate(_groupByExpressions[k], row);
                            }
                        }

                        EvaluateAggregateArgumentsInto(evaluator, row, argumentScratch, sortKeyScratch);

                        if (isGlobalAggregation)
                        {
                            AccumulateRow(globalGroup!, argumentScratch, sortKeyScratch, context, in accumFrame);
                            continue;
                        }

                        if (spilling)
                        {
                            int hashCode = useSingleKey
                                ? singleKey.GetHashCode()
                                : CompositeKeyHashMap<GroupState>.ComputeHash(
                                    compositeKeyScratch!.AsSpan(0, keyCount));
                            int partition = (int)((uint)hashCode % SpillPartitionCount);

                            partitionBuffers![partition] ??= pool.RentRowBatch(
                                spillSchema!, context.BatchSize, bufferArena!);

                            DataValue[] flatValues = pool.RentDataValues(spillSchema!.Count);
                            try
                            {
                                int offset = 0;
                                if (useSingleKey)
                                {
                                    flatValues[offset++] = DataValueRetention.Stabilize(
                                        singleKey, inputBatch.Arena, bufferArena!);
                                }
                                else
                                {
                                    for (int k = 0; k < keyCount; k++)
                                    {
                                        flatValues[offset++] = DataValueRetention.Stabilize(
                                            compositeKeyScratch![k], inputBatch.Arena, bufferArena!);
                                    }
                                }
                                for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
                                {
                                    DataValue[] argValues = argumentScratch[aggregateIndex];
                                    for (int argIndex = 0; argIndex < argValues.Length; argIndex++)
                                    {
                                        flatValues[offset++] = DataValueRetention.Stabilize(
                                            argValues[argIndex], inputBatch.Arena, bufferArena!);
                                    }
                                    if (sortKeyScratch?[aggregateIndex] is DataValue[] sortKeys)
                                    {
                                        for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                                        {
                                            flatValues[offset++] = DataValueRetention.Stabilize(
                                                sortKeys[sortIndex], inputBatch.Arena, bufferArena!);
                                        }
                                    }
                                }
                                partitionBuffers[partition]!.Add(flatValues);
                                flatValues = null!;
                            }
                            finally
                            {
                                if (flatValues is not null) pool.Backing.Return(flatValues);
                            }

                            if (partitionBuffers[partition]!.IsFull)
                            {
                                spiller!.Write(partitionBuffers[partition]!, partition);
                                partitionBuffers[partition] = null;
                            }

                            // Pre-existing in-memory groups still receive new rows so pre-spill
                            // data is not lost. Drain skips these keys via partition-local dedup.
                            GroupState? existing = null;
                            if (useSingleKey)
                                singleKeyTable!.TryGetValue(singleKey, out existing);
                            else
                                compositeKeyTable!.TryGetValue(
                                    new CompositeKey(compositeKeyScratch!.AsSpan(0, keyCount).ToArray()),
                                    out existing);

                            if (existing is not null)
                                AccumulateRow(existing, argumentScratch, sortKeyScratch, context, in accumFrame);
                            continue;
                        }

                        GroupState group;
                        if (useSingleKey)
                        {
                            if (!singleKeyTable!.TryGetValue(singleKey, out GroupState? existingGroup))
                            {
                                existingGroup = CreateGroupState(pool, in accumFrame);
                                existingGroup.KeyValues = [singleKey];
                                singleKeyTable[singleKey] = existingGroup;
                            }
                            group = existingGroup;
                        }
                        else
                        {
                            DataValue[] permanentKey = compositeKeyScratch!.AsSpan(0, keyCount).ToArray();
                            CompositeKey ck = new(permanentKey);
                            if (!compositeKeyTable!.TryGetValue(ck, out GroupState? existingGroup))
                            {
                                existingGroup = CreateGroupState(pool, in accumFrame);
                                existingGroup.KeyValues = permanentKey;
                                compositeKeyTable[ck] = existingGroup;
                            }
                            group = existingGroup;
                        }

                        AccumulateRow(group, argumentScratch, sortKeyScratch, context, in accumFrame);

                        if (estimator is not null)
                        {
                            if (estimator.ShouldSample())
                                estimator.RecordSample(row);
                            estimator.IncrementRowCount();
                            long groupCount = useSingleKey
                                ? singleKeyTable!.Count
                                : compositeKeyTable!.Count;
                            long estimatedMemory = estimator.EstimateBytesForRowCount(groupCount);

                            if (estimatedMemory > memoryBudget!.Value)
                            {
                                spilling = true;
                                spillSchema = BuildSpillSchema();
                                bufferArena = pool.RentArena();
                                int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                spiller = new SpillReaderWriter(
                                    pool, spillSchema, context.SpillDirectory,
                                    initialArenaCapacity: hint,
                                    partitionCount: SpillPartitionCount);
                                partitionBuffers = new RowBatch?[SpillPartitionCount];

                                if (ExecutionTracer.IsEnabled)
                                {
                                    ExecutionTracer.Write(
                                        $"GROUP BY spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}  groups={groupCount}");
                                }
                            }
                            else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                            {
                                estimator.EscalateToEveryRow();
                            }
                        }
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(inputBatch);
                }
            }

            // Flush remaining partition buffers before drain.
            if (spiller is not null && partitionBuffers is not null)
            {
                for (int p = 0; p < partitionBuffers.Length; p++)
                {
                    if (partitionBuffers[p] is not null)
                    {
                        spiller.Write(partitionBuffers[p]!, p);
                        partitionBuffers[p] = null;
                    }
                }
            }

            // Emit phase.
            bool hasOrderedAggregates = _aggregateColumns.Any(c => c.OrderBy is not null);

            // Output frame: Source = context.Store (where accumulator state lives),
            // Target = operatorArena (where result-batch values land). Lazily build
            // operatorArena since the global path may need it before in-memory emit.
            operatorArena ??= pool.RentArena();
            InvocationFrame emitFrame = new(context.Store, operatorArena, context.SidecarRegistry);

            if (isGlobalAggregation)
            {
                if (hasOrderedAggregates)
                    FlushOrderedBuffers([globalGroup!], context, in emitFrame);

                Row globalEmitted = EmitGroupRow(globalGroup!, isGlobalAggregation: true, pool, ref outputLookup, in emitFrame);
                outputBatch = pool.RentRowBatch(outputLookup!, context.BatchSize, operatorArena);
                outputBatch.Add(globalEmitted.RawValues);
                RowBatch globalToYield = outputBatch;
                outputBatch = null;
                yield return globalToYield;
                yield break;
            }

            IEnumerable<GroupState> inMemoryGroups = useSingleKey
                ? singleKeyTable!.Values
                : compositeKeyTable!.Values;

            if (hasOrderedAggregates)
                FlushOrderedBuffers(inMemoryGroups, context, in emitFrame);

            foreach (GroupState g in inMemoryGroups)
            {
                Row emitted = EmitGroupRow(g, isGlobalAggregation: false, pool, ref outputLookup, in emitFrame);
                outputBatch ??= pool.RentRowBatch(outputLookup!, context.BatchSize, operatorArena);
                outputBatch.Add(emitted.RawValues);
                if (outputBatch.IsFull)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }
            }

            // Drain spilled partitions: rebuild partition-local hash tables from the
            // replayed spill rows, skipping keys already represented in the in-memory
            // table (those rows have already been accumulated into the in-memory group
            // via the during-spill side-channel).
            if (spilling)
            {
                // Drain frame: replayed batches' values resolve against the spiller's
                // consolidated arena (Source). Accumulator state still lives in
                // context.Store (Target) so it can outlive the replayed batches.
                InvocationFrame drainFrame = new(
                    spiller!.ConsolidatedArena, context.Store, context.SidecarRegistry);

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spiller!.RowsWrittenInPartition(partition) == 0) continue;

                    Dictionary<DataValue, GroupState>? partSingleGroups = useSingleKey
                        ? new Dictionary<DataValue, GroupState>() : null;
                    Dictionary<CompositeKey, GroupState>? partCompositeGroups = !useSingleKey
                        ? new Dictionary<CompositeKey, GroupState>(CompositeKeyComparer.Instance) : null;

                    await foreach (RowBatch spillBatch in spiller.ReplayPartitionAsync(
                        context, spillSchema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int sb = 0; sb < spillBatch.Count; sb++)
                            {
                                Row spillRow = spillBatch[sb];
                                int offset = 0;

                                DataValue[] partKey = new DataValue[keyCount];
                                for (int k = 0; k < keyCount; k++)
                                {
                                    partKey[k] = spillRow[offset++];
                                }

                                bool isAlreadyInMemory = useSingleKey
                                    ? singleKeyTable!.ContainsKey(partKey[0])
                                    : compositeKeyTable!.ContainsKey(new CompositeKey(partKey));
                                if (isAlreadyInMemory) continue;

                                GroupState partGroup;
                                if (useSingleKey)
                                {
                                    if (!partSingleGroups!.TryGetValue(partKey[0], out GroupState? pg))
                                    {
                                        pg = CreateGroupState(pool, in drainFrame);
                                        pg.KeyValues = partKey;
                                        partSingleGroups[partKey[0]] = pg;
                                    }
                                    partGroup = pg;
                                }
                                else
                                {
                                    CompositeKey partCk = new(partKey);
                                    if (!partCompositeGroups!.TryGetValue(partCk, out GroupState? pg))
                                    {
                                        pg = CreateGroupState(pool, in drainFrame);
                                        pg.KeyValues = partKey;
                                        partCompositeGroups[partCk] = pg;
                                    }
                                    partGroup = pg;
                                }

                                for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
                                {
                                    DataValue[] argValues = argumentScratch[aggregateIndex];
                                    for (int argIndex = 0; argIndex < argValues.Length; argIndex++)
                                    {
                                        argValues[argIndex] = spillRow[offset++];
                                    }
                                    if (sortKeyScratch?[aggregateIndex] is DataValue[] sortKeys)
                                    {
                                        for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                                        {
                                            sortKeys[sortIndex] = spillRow[offset++];
                                        }
                                    }
                                }

                                AccumulateRow(partGroup, argumentScratch, sortKeyScratch, context, in drainFrame);
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spillBatch);
                        }
                    }

                    IEnumerable<GroupState> partGroups = useSingleKey
                        ? partSingleGroups!.Values
                        : partCompositeGroups!.Values;

                    if (hasOrderedAggregates)
                        FlushOrderedBuffers(partGroups, context, in emitFrame);

                    foreach (GroupState pg in partGroups)
                    {
                        Row emitted = EmitGroupRow(pg, isGlobalAggregation: false, pool, ref outputLookup, in emitFrame);
                        outputBatch ??= pool.RentRowBatch(outputLookup!, context.BatchSize, operatorArena);
                        outputBatch.Add(emitted.RawValues);
                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }

                    pool.Backing.Return(partGroups, _aggregateColumns.Count);
                }
            }

            pool.Backing.Return(inMemoryGroups, _aggregateColumns.Count);

            if (outputBatch is not null)
            {
                RowBatch trailingBatch = outputBatch;
                outputBatch = null;
                yield return trailingBatch;
            }
        }
        finally
        {
            if (partitionBuffers is not null)
            {
                for (int p = 0; p < partitionBuffers.Length; p++)
                {
                    if (partitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(partitionBuffers[p]!);
                    }
                }
            }
            spiller?.Dispose();
            if (bufferArena is not null) pool.ReturnArena(bufferArena);
            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (operatorArena is not null) pool.ReturnArena(operatorArena);
        }
    }

    /// <summary>
    /// Merges a source group's accumulators and ordered buffers into a target group.
    /// For non-ordered aggregates, calls <see cref="IAggregateAccumulator.Merge"/>.
    /// For ordered aggregates, concatenates the ordered buffers (sorted at flush time).
    /// </summary>
    private void MergeGroupState(GroupState target, GroupState source)
    {
        for (int i = 0; i < _aggregateColumns.Count; i++)
        {
            AggregateColumn column = _aggregateColumns[i];

            if (column.OrderBy is not null
                && target.OrderedBuffers?[i] is not null
                && source.OrderedBuffers?[i] is not null)
            {
                target.OrderedBuffers[i]!.AddRange(source.OrderedBuffers[i]!);
            }
            else
            {
                target.Accumulators[i].Merge(source.Accumulators[i]);
            }
        }
    }

    /// <summary>
    /// Computes <see cref="_distinctMemoryBudgetBytes"/> and
    /// <see cref="_estimatedDistinctCountPerGroup"/> based on the operator's
    /// memory budget and estimated source cardinality. Read by
    /// <see cref="CreateGroupState"/> when wrapping accumulators in
    /// <see cref="DistinctAccumulatorDecorator"/>.
    /// </summary>
    private void InitializeDistinctBudgets(ExecutionContext context, bool isGlobalAggregation)
    {
        long? memoryBudget = context.MemoryBudgetBytes;
        long? estimatedSourceRows = isGlobalAggregation ? null : GetEstimatedSourceRowCount();
        int initialCapacity = estimatedSourceRows.HasValue
            ? (int)Math.Min(estimatedSourceRows.Value, int.MaxValue / 2)
            : 16;

        // For DISTINCT aggregates, compute a per-accumulator memory budget so the
        // DistinctAccumulatorDecorator can spill to disk when its hash set grows
        // beyond the limit.
        //
        // For global aggregation (no GROUP BY), the full budget is split across
        // the distinct aggregate count. For keyed aggregation, the budget is
        // divided by an assumed concurrent-DISTINCT-set count (256) so total
        // memory across groups stays within the overall budget while avoiding
        // the pathological case where a handful of groups accumulate millions
        // of entries.
        if (memoryBudget.HasValue)
        {
            int distinctAggregateCount = _aggregateColumns.Count(column => column.Distinct);

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
        if (_aggregateColumns.Any(c => c.Distinct) && estimatedSourceRows.HasValue)
        {
            long divisor = isGlobalAggregation ? 1 : Math.Max(initialCapacity, 256);
            _estimatedDistinctCountPerGroup = (int)Math.Min(
                estimatedSourceRows.Value / divisor, 1_000_000);
        }
    }

    /// <summary>
    /// Creates a new <see cref="GroupState"/> for a single aggregation group,
    /// optionally passing a memory budget to DISTINCT accumulator decorators.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="_distinctMemoryBudgetBytes"/> (computed once by
    /// <see cref="InitializeDistinctBudgets"/>) for per-group DISTINCT spill budgets.
    /// </remarks>
    /// <summary>
    /// Builds the schema used by spilled rows: <c>__key_0...__key_{N-1}</c> for the GROUP BY
    /// keys, then per aggregate <c>__arg_{i}_{j}</c> for each argument expression and
    /// <c>__sort_{i}_{k}</c> for each ORDER BY expression. CountStar contributes no args.
    /// Captured once on first spill and reused by every partition.
    /// </summary>
    private ColumnLookup BuildSpillSchema()
    {
        int keyCount = _groupByExpressions.Count;
        int extraCount = 0;
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn column = _aggregateColumns[aggregateIndex];
            if (!column.IsCountStar)
            {
                extraCount += column.ArgumentExpressions.Count;
            }
            if (column.OrderBy is not null)
            {
                extraCount += column.OrderBy.Count;
            }
        }

        string[] names = new string[keyCount + extraCount];
        int next = 0;
        for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
        {
            names[next++] = $"__key_{keyIndex}";
        }
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn column = _aggregateColumns[aggregateIndex];
            if (!column.IsCountStar)
            {
                for (int argIndex = 0; argIndex < column.ArgumentExpressions.Count; argIndex++)
                {
                    names[next++] = $"__arg_{aggregateIndex}_{argIndex}";
                }
            }
            if (column.OrderBy is not null)
            {
                for (int sortIndex = 0; sortIndex < column.OrderBy.Count; sortIndex++)
                {
                    names[next++] = $"__sort_{aggregateIndex}_{sortIndex}";
                }
            }
        }

        return new ColumnLookup(names);
    }

    private GroupState CreateGroupState(Pool pool, in InvocationFrame frame)
    {
        int count = _aggregateColumns.Count;
        GroupState state = pool.Backing.RentGroupState(count);
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

    /// <summary>
    /// Accumulates one input row's aggregate arguments into the given group state.
    /// </summary>
    private void AccumulateRow(
        GroupState group,
        DataValue[][] allArguments,
        DataValue[]?[]? allSortKeys,
        ExecutionContext context,
        in InvocationFrame frame)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

            if (aggregateColumn.OrderBy is not null && allSortKeys?[aggregateIndex] is DataValue[] sortKeys)
            {
                // Ordered aggregate: append argument and sort-key values into the
                // flat buffer. No per-row array allocation — the buffer stores
                // values contiguously with stride-based access.
                group.OrderedBuffers![aggregateIndex]!.Add(
                    allArguments[aggregateIndex], sortKeys);
            }
            else
            {
                group.Accumulators[aggregateIndex].Accumulate(allArguments[aggregateIndex], in frame);
                context.QueryMeter?.Add(aggregateColumn.Function.QueryUnitCost);
            }
        }
    }

    /// <summary>
    /// Flushes ordered aggregate buffers by sorting and accumulating deferred rows.
    /// </summary>
    private void FlushOrderedBuffers(IEnumerable<GroupState> groups, ExecutionContext context, in InvocationFrame frame)
    {
        foreach (GroupState groupState in groups)
        {
            FlushOrderedBuffersForGroup(groupState, context, in frame);
        }
    }

    /// <summary>
    /// Flushes ordered aggregate buffers for a single group by sorting and
    /// accumulating deferred rows.
    /// </summary>
    private void FlushOrderedBuffersForGroup(GroupState groupState, ExecutionContext context, in InvocationFrame frame)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];
            if (aggregateColumn.OrderBy is null) continue;

            OrderedAggregateBuffer buffer = groupState.OrderedBuffers![aggregateIndex]!;

            IReadOnlyList<OrderByItem> orderByItems = aggregateColumn.OrderBy;

            buffer.Sort((a, b) =>
            {
                ReadOnlySpan<DataValue> sortA = buffer.GetSortKeys(a);
                ReadOnlySpan<DataValue> sortB = buffer.GetSortKeys(b);
                for (int sortIndex = 0; sortIndex < orderByItems.Count; sortIndex++)
                {
                    int comparison = OrderByOperator.CompareDataValues(
                        sortA[sortIndex], sortB[sortIndex]);

                    if (orderByItems[sortIndex].Direction == SortDirection.Descending)
                    {
                        comparison = -comparison;
                    }

                    if (comparison != 0) return comparison;
                }
                return 0;
            });

            int rowCount = buffer.Count;
            for (int row = 0; row < rowCount; row++)
            {
                groupState.Accumulators[aggregateIndex].Accumulate(buffer.GetArguments(row), in frame);
                context.QueryMeter?.Add(aggregateColumn.Function.QueryUnitCost);
            }

            buffer.Clear();
        }
    }

    /// <summary>
    /// Creates reusable scratch buffers for aggregate argument and sort-key evaluation.
    /// The outer <see cref="DataValue"/>[][] and each inner <see cref="DataValue"/>[] are
    /// allocated once and reused across all input rows, eliminating per-row heap allocations.
    /// </summary>
    private (DataValue[][] Arguments, DataValue[]?[]? SortKeys) CreateAggregateArgumentScratch()
    {
        DataValue[][] arguments = new DataValue[_aggregateColumns.Count][];
        DataValue[]?[]? sortKeys = null;

        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

            if (aggregateColumn.IsCountStar)
            {
                arguments[aggregateIndex] = [];
            }
            else
            {
                arguments[aggregateIndex] = new DataValue[aggregateColumn.ArgumentExpressions.Count];

                if (aggregateColumn.OrderBy is not null)
                {
                    sortKeys ??= new DataValue[]?[_aggregateColumns.Count];
                    sortKeys[aggregateIndex] = new DataValue[aggregateColumn.OrderBy.Count];
                }
            }
        }

        return (arguments, sortKeys);
    }

    /// <summary>
    /// Evaluates all aggregate function arguments and optional sort keys for a single
    /// input row into pre-allocated scratch buffers. Callers must create the buffers
    /// once via <see cref="CreateAggregateArgumentScratch"/> and pass them on every row.
    /// </summary>
    private void EvaluateAggregateArgumentsInto(
        ExpressionEvaluator evaluator,
        Row row,
        DataValue[][] allArguments,
        DataValue[]?[]? allSortKeys)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

            if (aggregateColumn.IsCountStar)
            {
                continue;
            }

            DataValue[] arguments = allArguments[aggregateIndex];
            for (int argumentIndex = 0; argumentIndex < aggregateColumn.ArgumentExpressions.Count; argumentIndex++)
            {
                arguments[argumentIndex] = evaluator.Evaluate(
                    aggregateColumn.ArgumentExpressions[argumentIndex], row);
            }

            if (aggregateColumn.OrderBy is not null)
            {
                DataValue[] sortKeyBuffer = allSortKeys![aggregateIndex]!;
                for (int sortIndex = 0; sortIndex < aggregateColumn.OrderBy.Count; sortIndex++)
                {
                    sortKeyBuffer[sortIndex] = evaluator.Evaluate(
                        aggregateColumn.OrderBy[sortIndex].Expression, row);
                }
            }
        }
    }

    /// <summary>
    /// Compares two composite GROUP BY key arrays for equality.
    /// </summary>
    private static bool CompositeKeysEqual(DataValue[] a, DataValue[] b)
    {
        for (int index = 0; index < a.Length; index++)
        {
            if (!a[index].Equals(b[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Emits a single output row from a completed group state.
    /// </summary>
    private Row EmitGroupRow(
        GroupState group,
        bool isGlobalAggregation,
        Pool pool,
        ref ColumnLookup? outputLookup,
        in InvocationFrame frame)
    {
        int outputFieldCount = _groupByExpressions.Count + _aggregateColumns.Count;

        if (outputLookup is null)
        {
            string[] outputNames = new string[outputFieldCount];

            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                outputNames[index] = QueryExplainer.FormatExpression(_groupByExpressions[index]);
            }

            for (int index = 0; index < _aggregateColumns.Count; index++)
            {
                outputNames[_groupByExpressions.Count + index] = _aggregateColumns[index].OutputName;
            }

            outputLookup = new ColumnLookup(outputNames);
        }

        DataValue[] values = pool.RentDataValues(outputFieldCount);

        if (!isGlobalAggregation)
        {
            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                values[index] = group.KeyValues![index];
            }
        }

        for (int index = 0; index < _aggregateColumns.Count; index++)
        {
            values[_groupByExpressions.Count + index] = group.Accumulators[index].Result(in frame);
        }

        return new Row(outputLookup, values);
    }


    /// <inheritdoc />
    public void Dispose()
    {
        // SpillReaderWriter ownership is per-iterator (constructed inside
        // ExecuteHashSingleWorkerAsync, disposed in its finally block). No
        // operator-scoped spill state remains.
    }


}

/// <summary>
/// Describes a single aggregate function call in a GROUP BY query,
/// including the function, its argument expressions, and the output column name.
/// </summary>
/// <param name="Function">The aggregate function implementation.</param>
/// <param name="ArgumentExpressions">
/// The expressions to evaluate per row as arguments to the aggregate.
/// Empty for <c>COUNT(*)</c>.
/// </param>
/// <param name="OutputName">The output column name (e.g. <c>COUNT(*)</c>, <c>SUM(price)</c>).</param>
/// <param name="IsCountStar">Whether this is a <c>COUNT(*)</c> invocation with no arguments.</param>
/// <param name="Distinct">Whether the aggregate uses <c>DISTINCT</c> to deduplicate values before accumulation.</param>
/// <param name="OrderBy">
/// Optional intra-aggregate ORDER BY items for functions like
/// <c>STRING_AGG(expr, separator ORDER BY expr ASC)</c>. When non-null,
/// accumulated rows are sorted before being fed to the accumulator.
/// </param>
public sealed record AggregateColumn(
    IAggregateFunction Function,
    IReadOnlyList<Expression> ArgumentExpressions,
    string OutputName,
    bool IsCountStar = false,
    bool Distinct = false,
    IReadOnlyList<OrderByItem>? OrderBy = null);
