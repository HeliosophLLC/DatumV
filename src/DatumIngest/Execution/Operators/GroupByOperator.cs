using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Diagnostics;
using DatumIngest.Execution.Operators.GroupBy;
using DatumIngest.Functions;
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
public sealed class GroupByOperator : QueryOperator, IDisposable
{
    private readonly QueryOperator _source;
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private readonly bool _streamingSorted;

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
        QueryOperator source,
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
    public QueryOperator Source => _source;

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
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        IReadOnlyList<Expression> rewrittenKeys = _groupByExpressions
            .Select(rewriter)
            .ToList();
        IReadOnlyList<AggregateColumn> rewrittenAggregates = _aggregateColumns
            .Select(ac => ac with
            {
                ArgumentExpressions = ac.ArgumentExpressions.Select(rewriter).ToList(),
                OrderBy = ac.OrderBy?
                    .Select(ob => ob with { Expression = rewriter(ob.Expression) })
                    .ToList(),
            })
            .ToList();
        return new GroupByOperator(
            _source.RewriteExpressions(rewriter),
            rewrittenKeys,
            rewrittenAggregates,
            _streamingSorted);
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
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
        ExpressionEvaluator evaluator = new(context);

        // Stabilize aggregate results and emit output rows into the per-query
        // Store rather than a private rented arena. See the SpillPartition
        // commit's docstring for the rationale: any operator that emits
        // DataValues into a private arena risks downstream "splice without
        // re-stabilize" reads (notably JoinSchema.CombinePooledValues)
        // resolving offsets against the wrong arena. Under one-arena-per-query,
        // every operator's output is mutually addressable in context.Store.
        OutputBatchWriter? writer = null;

        GroupState? currentGroup = null;
        StreamingGroupKey keys = new(_groupByExpressions);
        AggregateArgumentBinder binder = new(_aggregateColumns);
        GroupStateFactory groupStateFactory = new(pool, context, _aggregateColumns);

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (inputBatch.Count == 0) continue;

                    writer ??= new OutputBatchWriter(_groupByExpressions, _aggregateColumns, context);

                    InvocationFrame frame = new(
                        inputBatch.Arena,
                        context.Store,
                        context.SidecarRegistry);

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row row = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        await keys.EvaluateAsync(evaluator, row, context.CancellationToken).ConfigureAwait(false);

                        if (currentGroup is not null && keys.ScratchDiffersFromCurrent())
                        {
                            FlushOrderedBuffersForGroup(currentGroup, context, in frame);
                            RowBatch? ready = await writer.AddAsync(currentGroup, isGlobalAggregation: false, frame).ConfigureAwait(false);
                            pool.Backing.Return(currentGroup);
                            currentGroup = null;
                            if (ready is not null) yield return ready;
                        }

                        if (currentGroup is null)
                        {
                            currentGroup = groupStateFactory.Create(in frame);
                            currentGroup.KeyValues = keys.CaptureCurrent();
                        }

                        // Evaluate and accumulate aggregate arguments using reusable scratch buffers.
                        await binder.EvaluateAsync(evaluator, row, context.CancellationToken).ConfigureAwait(false);
                        binder.AccumulateInto(currentGroup, context, in frame);
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            // Emit the final group.
            if (currentGroup is not null)
            {
                writer ??= new OutputBatchWriter(_groupByExpressions, _aggregateColumns, context);
                InvocationFrame trailingFrame = new(context.Store, context.Store, context.SidecarRegistry);
                FlushOrderedBuffersForGroup(currentGroup, context, in trailingFrame);
                RowBatch? ready = await writer.AddAsync(currentGroup, isGlobalAggregation: false, trailingFrame).ConfigureAwait(false);
                pool.Backing.Return(currentGroup);
                currentGroup = null;
                if (ready is not null) yield return ready;
            }

            if (writer?.Flush() is RowBatch trailing) yield return trailing;
        }
        finally
        {
            if (currentGroup is not null) pool.Backing.Return(currentGroup);
            if (writer?.Flush() is RowBatch leftover) context.ReturnRowBatch(leftover);
        }
    }


    /// <summary>
    /// Walks through transparent operator wrappers to find the underlying
    /// <see cref="ScanOperator"/> and returns its estimated row count.
    /// Returns <see langword="null"/> when the tree does not bottom out at a scan.
    /// </summary>
    private long? GetEstimatedSourceRowCount()
    {
        QueryOperator current = _source;
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
                    context, pool, isGlobalAggregation).ConfigureAwait(false))
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

                GroupStateFactory globalGroupStateFactory = new(
                    pool, context, _aggregateColumns,
                    memoryBudget: context.MemoryBudgetBytes,
                    estimatedSourceRowCount: null,
                    isGlobalAggregation: true);

                GroupState[] workerGlobalGroups = new GroupState[workerCount];
                for (int i = 0; i < workerCount; i++)
                {
                    workerGlobalGroups[i] = globalGroupStateFactory.Create(in workerAccumFrame);
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

                            context.ReturnRowBatch(inputBatch);
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
                        ExpressionEvaluator workerEvaluator = new(context);
                        AggregateArgumentBinder workerBinder = new(_aggregateColumns);

                        await foreach (Row row in globalChannel.Reader.ReadAllAsync(cancellationToken)
                            .ConfigureAwait(false))
                        {
                            await workerBinder.EvaluateAsync(
                                workerEvaluator, row, cancellationToken).ConfigureAwait(false);
                            workerBinder.AccumulateInto(workerGlobalGroups[wi], context, in workerAccumFrame);
                            // Row values extracted — batch-level ReturnBatch handles array return.
                        }
                    }, cancellationToken);
                }

                await globalFeeder.ConfigureAwait(false);
                await Task.WhenAll(globalWorkers).ConfigureAwait(false);

                for (int i = 1; i < workerCount; i++)
                {
                    await MergeGroupStateAsync(workerGlobalGroups[0], workerGlobalGroups[i], workerAccumFrame).ConfigureAwait(false);
                }

                bool globalHasOrderedAggregates = _aggregateColumns.Any(
                    column => column.OrderBy is not null);

                InvocationFrame globalEmitFrame = new(
                    context.Store, context.Store, context.SidecarRegistry);

                if (globalHasOrderedAggregates)
                {
                    FlushOrderedBuffers([workerGlobalGroups[0]], context, in globalEmitFrame);
                }

                OutputBatchWriter globalWriter = new(_groupByExpressions, _aggregateColumns, context);
                if (await globalWriter.AddAsync(workerGlobalGroups[0], isGlobalAggregation: true, globalEmitFrame).ConfigureAwait(false) is RowBatch globalReady)
                    yield return globalReady;
                if (globalWriter.Flush() is RowBatch globalTrailing)
                    yield return globalTrailing;

                yield break;
            }

        }
        finally
        {
            if (acquiredFromBudget > 0)
            {
                context.ParallelismBudget!.Release(acquiredFromBudget);
            }
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
        bool isGlobalAggregation)
    {
        ExpressionEvaluator evaluator = new(context);

        IHashGroupTable? hashTable = isGlobalAggregation
            ? null
            : _groupByExpressions.Count == 1
                ? new SingleHashGroupTable(_groupByExpressions[0])
                : new CompositeHashGroupTable(_groupByExpressions);

        // Long-lived frame captured by accumulators that need a stable Target arena
        // (e.g. DistinctAccumulatorDecorator's _capturedFrame for replay merges).
        // context.Store survives the query's lifetime.
        InvocationFrame initFrame = InvocationFrame.Symmetric(context.Store, context.SidecarRegistry);

        long? memoryBudget = context.MemoryBudgetBytes;

        GroupStateFactory groupStateFactory = new(
            pool, context, _aggregateColumns,
            memoryBudget: memoryBudget,
            estimatedSourceRowCount: isGlobalAggregation ? null : GetEstimatedSourceRowCount(),
            isGlobalAggregation: isGlobalAggregation);

        GroupState? globalGroup = isGlobalAggregation ? groupStateFactory.Create(in initFrame) : null;

        SpillCoordinator? spillCoordinator = (memoryBudget.HasValue && !isGlobalAggregation)
            ? new SpillCoordinator(pool, context, _groupByExpressions, _aggregateColumns)
            : null;

        AggregateArgumentBinder binder = new(_aggregateColumns);

        KeyedHashAggregator? aggregator = isGlobalAggregation ? null : new KeyedHashAggregator(
            context,
            hashTable!,
            binder,
            spillCoordinator,
            groupByKeyCount: _groupByExpressions.Count,
            aggregateCount: _aggregateColumns.Count,
            groupStateFactory);

        OutputBatchWriter? writer = null;

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

                        if (isGlobalAggregation)
                        {
                            await binder.EvaluateAsync(evaluator, row, context.CancellationToken).ConfigureAwait(false);
                            binder.AccumulateInto(globalGroup!, context, in accumFrame);
                            continue;
                        }

                        await aggregator!.ConsumeRowAsync(
                            row, evaluator, inputBatch.Arena, accumFrame, context.CancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            spillCoordinator?.FlushPartitionBuffers();

            // Emit phase.
            bool hasOrderedAggregates = _aggregateColumns.Any(c => c.OrderBy is not null);

            // Output frame: Source = context.Store (where accumulator state lives),
            // Target = context.Store (where result-batch values land). Under one-arena-
            // per-query, output bytes and accumulator bytes live in the same arena;
            // downstream operators reading via batch.Arena resolve offsets correctly
            // without depending on any consumer's re-stabilize behaviour.
            InvocationFrame emitFrame = new(context.Store, context.Store, context.SidecarRegistry);
            writer = new OutputBatchWriter(_groupByExpressions, _aggregateColumns, context);

            if (isGlobalAggregation)
            {
                if (hasOrderedAggregates)
                    FlushOrderedBuffers([globalGroup!], context, in emitFrame);

                if (await writer.AddAsync(globalGroup!, isGlobalAggregation: true, emitFrame).ConfigureAwait(false) is RowBatch globalReady)
                    yield return globalReady;
                if (writer.Flush() is RowBatch globalTrailing)
                    yield return globalTrailing;
                yield break;
            }

            if (hasOrderedAggregates)
                FlushOrderedBuffers(hashTable!.AllGroups, context, in emitFrame);

            foreach (GroupState g in hashTable!.AllGroups)
            {
                if (await writer.AddAsync(g, isGlobalAggregation: false, emitFrame).ConfigureAwait(false) is RowBatch ready)
                    yield return ready;
            }

            // Drain spilled partitions: rebuild partition-local hash tables from the
            // replayed spill rows, skipping keys already represented in the in-memory
            // table (those rows have already been accumulated into the in-memory group
            // via the during-spill side-channel).
            if (spillCoordinator is not null && spillCoordinator.IsSpilling)
            {
                // Drain frame: replayed batches' values resolve against the spiller's
                // consolidated arena (Source). Accumulator state still lives in
                // context.Store (Target) so it can outlive the replayed batches.
                InvocationFrame drainFrame = new(
                    spillCoordinator.ConsolidatedArena, context.Store, context.SidecarRegistry);

                await foreach (IHashGroupTable partTable in spillCoordinator.DrainPartitionsAsync(
                    hashTable!, binder, drainFrame, groupStateFactory).ConfigureAwait(false))
                {
                    if (hasOrderedAggregates)
                        FlushOrderedBuffers(partTable.AllGroups, context, in emitFrame);

                    foreach (GroupState pg in partTable.AllGroups)
                    {
                        if (await writer.AddAsync(pg, isGlobalAggregation: false, emitFrame).ConfigureAwait(false) is RowBatch ready)
                            yield return ready;
                    }

                    pool.Backing.Return(partTable.AllGroups, _aggregateColumns.Count);
                }
            }

            pool.Backing.Return(hashTable!.AllGroups, _aggregateColumns.Count);

            if (writer.Flush() is RowBatch trailingBatch) yield return trailingBatch;
        }
        finally
        {
            // Release the hash-table residency back to the plan-wide
            // accountant — the in-memory groups survived until now (see
            // KeyedHashAggregator's "post-spill memory stays flat" doc), and
            // become unreachable as the operator returns.
            if (aggregator is not null && aggregator.ResidentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(aggregator.ResidentBytesNotified);
            }
            spillCoordinator?.Dispose();
            if (writer?.Flush() is RowBatch leftover) context.ReturnRowBatch(leftover);
        }
    }

    /// <summary>
    /// Merges a source group's accumulators and ordered buffers into a target group.
    /// For non-ordered aggregates, calls <see cref="IAggregateAccumulator.MergeAsync"/>.
    /// For ordered aggregates, concatenates the ordered buffers (sorted at flush time).
    /// </summary>
    private async ValueTask MergeGroupStateAsync(GroupState target, GroupState source, InvocationFrame frame)
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
                await target.Accumulators[i].MergeAsync(source.Accumulators[i], frame).ConfigureAwait(false);
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

            // Sort keys in OrderedAggregateBuffer were stabilised to frame.Source
            // (the per-query store / accumulator arena depending on the path); both
            // sides of the comparison live in the same store.
            IValueStore sortKeyStore = frame.Source;
            SidecarRegistry? sortKeyRegistry = frame.SidecarRegistry;
            buffer.Sort((a, b) =>
            {
                ReadOnlySpan<DataValue> sortA = buffer.GetSortKeys(a);
                ReadOnlySpan<DataValue> sortB = buffer.GetSortKeys(b);
                for (int sortIndex = 0; sortIndex < orderByItems.Count; sortIndex++)
                {
                    int comparison = OrderByOperator.CompareDataValues(
                        sortA[sortIndex], sortKeyStore, sortKeyRegistry,
                        sortB[sortIndex], sortKeyStore, sortKeyRegistry);

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
