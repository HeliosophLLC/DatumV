using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

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
    private string? _spillDirectory;

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

        // Parallel hash aggregation is only used for global aggregation (no GROUP BY
        // keys). The global path distributes rows round-robin with no per-row key
        // evaluation in the feeder, so workers accumulate truly in parallel.
        //
        // For keyed aggregation the feeder must evaluate GROUP BY keys on every row
        // to compute the routing hash, then each worker re-evaluates the same keys
        // for its own hash table lookup — doubling key evaluation cost. Combined with
        // bounded-channel overhead and a single-threaded feeder bottleneck, the
        // parallel keyed path is consistently slower than the serial path.
        if (context.DegreeOfParallelism > 1 && _groupByExpressions.Count == 0)
        {
            long? estimatedRows = GetEstimatedSourceRowCount();
            if (estimatedRows is null or >= 100_000)
            {
                return ExecuteParallelHashAsync(context);
            }
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
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        bool useSingleKey = _groupByExpressions.Count == 1;

        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;

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

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
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
                    FlushOrderedBuffersForGroup(currentGroup, context);
                    outputBatch ??= RowBatch.Rent(context.BatchSize);
                    outputBatch.Add(EmitGroupRow(currentGroup, isGlobalAggregation: false,
                        ref outputNames, ref outputNameIndex));
                    GlobalBufferPool.ReturnGroupState(currentGroup);
                    currentGroup = null;
                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }

                if (currentGroup is null)
                {
                    currentGroup = CreateGroupState();
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
                    FlushOrderedBuffersForGroup(currentGroup, context);
                    outputBatch ??= RowBatch.Rent(context.BatchSize);
                    outputBatch.Add(EmitGroupRow(currentGroup, isGlobalAggregation: false,
                        ref outputNames, ref outputNameIndex));
                    GlobalBufferPool.ReturnGroupState(currentGroup);
                    currentGroup = null;
                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }

                if (currentGroup is null)
                {
                    // Copy scratch into permanent storage only at group boundaries.
                    DataValue[] permanentKey = compositeKeyScratch!.AsSpan(0, keyCount).ToArray();
                    currentGroup = CreateGroupState();
                    currentGroup.KeyValues = permanentKey;
                    currentKeyValues = permanentKey;
                }
            }

            // Evaluate and accumulate aggregate arguments using reusable scratch buffers.
            EvaluateAggregateArgumentsInto(evaluator, row, argumentScratch, sortKeyScratch);
            AccumulateRow(currentGroup, argumentScratch, sortKeyScratch, context);

            // Row values have been fully extracted — return the row to
            // the pool so the upstream operator can reuse it.
            context.LocalBufferPool.ReturnValues(row);
            }

            inputBatch.Return();
        }

        // Emit the final group.
        if (currentGroup is not null)
        {
            FlushOrderedBuffersForGroup(currentGroup, context);
            outputBatch ??= RowBatch.Rent(context.BatchSize);
            outputBatch.Add(EmitGroupRow(currentGroup, isGlobalAggregation: false,
                ref outputNames, ref outputNameIndex));
            GlobalBufferPool.ReturnGroupState(currentGroup);
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }

    /// <summary>
    /// Hash-based aggregation path: materializes all groups in a hash table before
    /// emitting any output rows. Supports spill-to-disk when memory budget is exceeded.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteHashAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        bool useSingleKey = _groupByExpressions.Count == 1;
        bool isGlobalAggregation = _groupByExpressions.Count == 0;

        // Estimate the number of distinct groups so we can pre-size the hash maps
        // and avoid repeated LOH-crossing doublings that drive Gen2 GC pressure.
        // The source row count is an upper bound; true cardinality is usually lower
        // but we accept minor over-allocation to eliminate resize-induced Gen2 deaths.
        long? estimatedSourceRows = isGlobalAggregation ? null : GetEstimatedSourceRowCount();
        int initialCapacity = estimatedSourceRows.HasValue
            ? (int)Math.Min(estimatedSourceRows.Value, int.MaxValue / 2)
            : 16;

        // Custom open-addressing hash maps enable Sse.Prefetch0 hints that
        // hide L3 miss latency when the number of distinct groups far exceeds
        // L3 cache capacity (e.g. 6M groups × ~24-byte entry ≫ 19MB L3).
        DataValueHashMap<GroupState>? singleKeyGroups =
            useSingleKey ? new(initialCapacity) : null;
        CompositeKeyHashMap<GroupState>? compositeKeyGroups =
            !useSingleKey && !isGlobalAggregation ? new(initialCapacity) : null;

        // For global aggregation (no GROUP BY), use a single group.
        GroupState? globalGroup = isGlobalAggregation ? CreateGroupState() : null;

        // Shared column schema for output rows (built on first output).
        string[]? outputNames = null;
        Dictionary<string, int>? outputNameIndex = null;

        // Spill state — lazily initialised when the budget is exceeded.
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue && !isGlobalAggregation
            ? new MemoryEstimator() : null;
        BinaryWriter?[]? spillWriters = null;
        bool[]? spillSchemaWritten = null;
        string[]? spillPaths = null;
        bool spilling = false;

        // Tracks the spill row schema — built once on the first row that triggers spill.
        SpillSchemaState spillSchema = new();

        // Pre-allocate reusable scratch buffers for aggregate argument evaluation.
        (DataValue[][] argumentScratch, DataValue[]?[]? sortKeyScratch) = CreateAggregateArgumentScratch();

        int keyCount = _groupByExpressions.Count;

        // Pre-resolved ordinals: when all GROUP BY expressions are simple
        // ColumnReferences, we resolve their ordinals once on the first batch
        // and use direct Row.RawValues[ordinal] access in the hot loop —
        // eliminating per-row ExpressionEvaluator dispatch and Dictionary<string,int>
        // lookups in Row.TryGetValue.
        int[]? keyOrdinals = null;
        bool useDirectKeyAccess = false;
        bool ordinalsResolved = false;

        // Software-pipelined prefetch ring buffers. Keys for PrefetchDistance
        // future rows are evaluated ahead of their hash table probe so that
        // PrefetchEntry() can issue a cache-line hint well before the actual
        // lookup, hiding L3 miss latency behind useful key evaluation work.
        const int PrefetchDistance = 32;

        DataValue[]? singleKeyRing = useSingleKey
            ? new DataValue[PrefetchDistance] : null;
        int[]? singleHashRing = useSingleKey
            ? new int[PrefetchDistance] : null;

        DataValue[][]? compositeKeyRing = null;
        int[]? compositeHashRing = null;
        DataValue[]? compositeKeyScratch = null;

        if (!useSingleKey && !isGlobalAggregation)
        {
            compositeKeyRing = new DataValue[PrefetchDistance][];
            for (int ringIndex = 0; ringIndex < PrefetchDistance; ringIndex++)
                compositeKeyRing[ringIndex] = new DataValue[keyCount];

            compositeHashRing = new int[PrefetchDistance];

            // Scratch buffer for spill path key evaluation (not needed in
            // the prefetch pipeline which uses the ring buffers directly).
            compositeKeyScratch = new DataValue[keyCount];
        }

        try
        {
            // Counts input rows for throughput tracing (guarded by IsEnabled — zero cost when off).
            long inputRowCount = 0;

            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                int batchCount = inputBatch.Count;
                context.CancellationToken.ThrowIfCancellationRequested();
                context.QueryMeter?.ThrowIfExceeded();

                // Resolve key ordinals on the first batch.
                if (!ordinalsResolved && !isGlobalAggregation && batchCount > 0)
                {
                    ordinalsResolved = true;
                    Dictionary<string, int> nameIndex = inputBatch[0].RawNameIndex;
                    int[] candidateOrdinals = new int[keyCount];
                    bool allResolved = true;

                    for (int k = 0; k < keyCount; k++)
                    {
                        if (_groupByExpressions[k] is ColumnReference columnReference)
                        {
                            string lookupName = columnReference.QualifiedName ?? columnReference.ColumnName;
                            if (nameIndex.TryGetValue(lookupName, out int ordinal))
                            {
                                candidateOrdinals[k] = ordinal;
                            }
                            else if (columnReference.QualifiedName is not null
                                     && nameIndex.TryGetValue(columnReference.ColumnName, out ordinal))
                            {
                                candidateOrdinals[k] = ordinal;
                            }
                            else
                            {
                                allResolved = false;
                                break;
                            }
                        }
                        else
                        {
                            allResolved = false;
                            break;
                        }
                    }

                    if (allResolved)
                    {
                        keyOrdinals = candidateOrdinals;
                        useDirectKeyAccess = true;
                    }
                }

                if (ExecutionTracer.IsEnabled)
                {
                    inputRowCount += batchCount;

                    if (inputRowCount % 1_000_000 < (uint)batchCount)
                    {
                        long groupCount = isGlobalAggregation ? 1
                            : useSingleKey ? singleKeyGroups!.Count
                            : compositeKeyGroups!.Count;
                        ExecutionTracer.Write(
                            $"GroupBy  consumed {inputRowCount:N0} rows  groups: {groupCount:N0}");
                    }
                }

                if (isGlobalAggregation)
                {
                    // Global aggregation: no keys, no prefetch pipeline.
                    for (int i = 0; i < batchCount; i++)
                    {
                        Row row = inputBatch[i];
                        EvaluateAggregateArgumentsInto(evaluator, row, argumentScratch, sortKeyScratch);
                        AccumulateRow(globalGroup!, argumentScratch, sortKeyScratch, context);
                        context.LocalBufferPool.ReturnValues(row);
                    }
                }
                else if (spilling)
                {
                    // Already spilling — process row-by-row without prefetch.
                    for (int i = 0; i < batchCount; i++)
                    {
                        Row row = inputBatch[i];
                        DataValue singleKey = default;

                        if (useSingleKey)
                        {
                            singleKey = useDirectKeyAccess
                                ? row.RawValues[keyOrdinals![0]]
                                : evaluator.Evaluate(_groupByExpressions[0], row);
                        }
                        else
                        {
                            if (useDirectKeyAccess)
                            {
                                DataValue[] rawValues = row.RawValues;
                                for (int k = 0; k < keyCount; k++)
                                    compositeKeyScratch![k] = rawValues[keyOrdinals![k]];
                            }
                            else
                            {
                                for (int k = 0; k < keyCount; k++)
                                    compositeKeyScratch![k] = evaluator.Evaluate(
                                        _groupByExpressions[k], row);
                            }
                        }

                        EvaluateAggregateArgumentsInto(evaluator, row, argumentScratch, sortKeyScratch);

                        int hashCode = useSingleKey
                            ? singleKey.GetHashCode()
                            : CompositeKeyHashMap<GroupState>.ComputeHash(
                                compositeKeyScratch!.AsSpan(0, keyCount));

                        WriteSpillRow(
                            hashCode,
                            useSingleKey ? [singleKey] : compositeKeyScratch!.AsSpan(0, keyCount).ToArray(),
                            argumentScratch, sortKeyScratch, spillWriters!, spillSchemaWritten!, spillPaths!,
                            spillSchema, _spillDirectory!);

                        // Accumulate into pre-existing in-memory groups so pre-spill
                        // data is not lost. ReaggregatePartition skips these during drain.
                        GroupState? existingGroup = null;

                        if (useSingleKey)
                            singleKeyGroups!.TryGetValue(singleKey, out existingGroup);
                        else
                            compositeKeyGroups!.TryGetValue(
                                compositeKeyScratch!.AsSpan(0, keyCount), out existingGroup);

                        if (existingGroup is not null)
                            AccumulateRow(existingGroup, argumentScratch, sortKeyScratch, context);

                        context.LocalBufferPool.ReturnValues(row);
                    }
                }
                else
                {
                    // ─── Prefetch pipeline hot path ───
                    //
                    // Pre-resolved ordinals: GROUP BY keys that are simple ColumnReferences
                    // are extracted via direct Row.RawValues[ordinal] access, bypassing the
                    // ExpressionEvaluator dispatch and per-row Dictionary<string,int> lookup.
                    //
                    // Software-pipelined prefetch: keys for PrefetchDistance future rows are
                    // evaluated ahead of their hash table probe so PrefetchEntry() can issue
                    // a cache-line hint well before the actual lookup.

                    // Prologue: fill ring buffers for the first PrefetchDistance rows.
                    int prologueCount = Math.Min(PrefetchDistance, batchCount);
                    for (int j = 0; j < prologueCount; j++)
                    {
                        Row prologueRow = inputBatch[j];
                        if (useSingleKey)
                        {
                            singleKeyRing![j] = useDirectKeyAccess
                                ? prologueRow.RawValues[keyOrdinals![0]]
                                : evaluator.Evaluate(_groupByExpressions[0], prologueRow);
                            singleHashRing![j] = singleKeyRing[j].GetHashCode();
                            singleKeyGroups!.PrefetchEntry(singleHashRing[j]);
                        }
                        else
                        {
                            DataValue[] ringKeys = compositeKeyRing![j];
                            if (useDirectKeyAccess)
                            {
                                DataValue[] rawValues = prologueRow.RawValues;
                                for (int k = 0; k < keyCount; k++)
                                    ringKeys[k] = rawValues[keyOrdinals![k]];
                            }
                            else
                            {
                                for (int k = 0; k < keyCount; k++)
                                    ringKeys[k] = evaluator.Evaluate(
                                        _groupByExpressions[k], prologueRow);
                            }

                            compositeHashRing![j] = CompositeKeyHashMap<GroupState>.ComputeHash(
                                ringKeys.AsSpan(0, keyCount));
                            compositeKeyGroups!.PrefetchEntry(compositeHashRing[j]);
                        }
                    }

                    // Main loop: probe with pre-evaluated key, accumulate, prepare future.
                    for (int i = 0; i < batchCount; i++)
                    {
                        Row row = inputBatch[i];
                        int slot = i % PrefetchDistance;

                        // Probe the hash table with the key evaluated PrefetchDistance
                        // iterations ago (or in the prologue). The entry's cache line
                        // has had time to arrive from L3/DRAM.
                        GroupState group;

                        if (useSingleKey)
                        {
                            ref GroupState groupRef = ref singleKeyGroups!.GetOrAdd(
                                singleKeyRing![slot], singleHashRing![slot], out bool exists);
                            if (!exists)
                            {
                                groupRef = CreateGroupState();
                                groupRef.KeyValues = [singleKeyRing[slot]];
                            }

                            group = groupRef;
                        }
                        else
                        {
                            ReadOnlySpan<DataValue> keySpan =
                                compositeKeyRing![slot].AsSpan(0, keyCount);
                            ref GroupState groupRef = ref compositeKeyGroups!.GetOrAddDefault(
                                keySpan, compositeHashRing![slot],
                                out bool exists, out DataValue[] storedKey);
                            if (!exists)
                            {
                                groupRef = CreateGroupState();
                                groupRef.KeyValues = storedKey;
                            }

                            group = groupRef;
                        }

                        EvaluateAggregateArgumentsInto(evaluator, row, argumentScratch, sortKeyScratch);
                        AccumulateRow(group, argumentScratch, sortKeyScratch, context);

                        // Memory estimation.
                        if (estimator is not null)
                        {
                            if (estimator.ShouldSample())
                                estimator.RecordSample(row);

                            estimator.IncrementRowCount();
                            long groupCount = useSingleKey
                                ? singleKeyGroups!.Count
                                : compositeKeyGroups!.Count;
                            long estimatedMemory = estimator.EstimateBytesForRowCount(groupCount);

                            if (estimatedMemory > memoryBudget!.Value)
                            {
                                spilling = true;
                                _spillDirectory = Path.Combine(
                                    Path.GetTempPath(), $"datum-groupby-{Guid.NewGuid():N}");
                                Directory.CreateDirectory(_spillDirectory);
                                spillWriters = new BinaryWriter[SpillPartitionCount];
                                spillSchemaWritten = new bool[SpillPartitionCount];
                                spillPaths = new string[SpillPartitionCount];

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

                        // Pipeline: evaluate keys for a future row and prefetch its
                        // hash table entry so it will be cache-resident when we probe
                        // it PrefetchDistance iterations from now.
                        int futureIndex = i + PrefetchDistance;
                        if (futureIndex < batchCount)
                        {
                            Row futureRow = inputBatch[futureIndex];
                            if (useSingleKey)
                            {
                                singleKeyRing![slot] = useDirectKeyAccess
                                    ? futureRow.RawValues[keyOrdinals![0]]
                                    : evaluator.Evaluate(
                                        _groupByExpressions[0], futureRow);
                                singleHashRing![slot] = singleKeyRing[slot].GetHashCode();
                                singleKeyGroups!.PrefetchEntry(singleHashRing[slot]);
                            }
                            else
                            {
                                DataValue[] futureKeys = compositeKeyRing![slot];
                                if (useDirectKeyAccess)
                                {
                                    DataValue[] rawValues = futureRow.RawValues;
                                    for (int k = 0; k < keyCount; k++)
                                        futureKeys[k] = rawValues[keyOrdinals![k]];
                                }
                                else
                                {
                                    for (int k = 0; k < keyCount; k++)
                                        futureKeys[k] = evaluator.Evaluate(
                                            _groupByExpressions[k], futureRow);
                                }

                                compositeHashRing![slot] =
                                    CompositeKeyHashMap<GroupState>.ComputeHash(
                                        futureKeys.AsSpan(0, keyCount));
                                compositeKeyGroups!.PrefetchEntry(compositeHashRing[slot]);
                            }
                        }

                        // Return the row to the pool so upstream can reuse it.
                        context.LocalBufferPool.ReturnValues(row);
                    }
                }

                // Re-check the budget after processing the batch so that query-unit
                // costs incurred by function calls within the batch are caught
                // before we move on to the next one.
                context.QueryMeter?.ThrowIfExceeded();

                inputBatch.Return();
            }

            if (ExecutionTracer.IsEnabled)
            {
                long groupCount = isGlobalAggregation ? 1
                    : useSingleKey ? singleKeyGroups!.Count
                    : compositeKeyGroups!.Count;
                ExecutionTracer.Write(
                    $"GroupBy  done consuming  inputRows={inputRowCount:N0}  groups={groupCount:N0}");
            }

            // Flush ordered buffers for in-memory groups.
            bool hasOrderedAggregates = _aggregateColumns.Any(c => c.OrderBy is not null);

            if (hasOrderedAggregates)
            {
                IEnumerable<GroupState> groupsToFlush = isGlobalAggregation
                    ? [globalGroup!]
                    : useSingleKey
                        ? singleKeyGroups!.Values
                        : compositeKeyGroups!.Values;

                FlushOrderedBuffers(groupsToFlush, context);
            }

            // Emit in-memory groups.
            IEnumerable<GroupState> allGroups = isGlobalAggregation
                ? [globalGroup!]
                : useSingleKey
                    ? singleKeyGroups!.Values
                    : compositeKeyGroups!.Values;

            RowBatch? outputBatch = null;
            foreach (GroupState group in allGroups)
            {
                outputBatch ??= RowBatch.Rent(context.BatchSize);
                outputBatch.Add(EmitGroupRow(group, isGlobalAggregation, ref outputNames, ref outputNameIndex));
                if (outputBatch.IsFull)
                {
                    yield return outputBatch;
                    outputBatch = null;
                }
            }

            // Return completed in-memory GroupState objects to the static pool.
            if (!isGlobalAggregation)
            {
                GlobalBufferPool.ReturnGroupStates(
                    useSingleKey
                        ? singleKeyGroups!.Values
                        : compositeKeyGroups!.Values,
                    _aggregateColumns.Count);
            }

            // Drain phase: process spilled partitions.
            if (spilling)
            {
                FlushSpillWriters(spillWriters!);

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spillPaths![partition] is null)
                    {
                        continue;
                    }

                    // Re-aggregate this partition's rows.
                    foreach (Row groupRow in ReaggregatePartition(
                        spillPaths[partition], useSingleKey,
                        singleKeyGroups, compositeKeyGroups, hasOrderedAggregates, context,
                        ref outputNames, ref outputNameIndex))
                    {
                        outputBatch ??= RowBatch.Rent(context.BatchSize);
                        outputBatch.Add(groupRow);
                        if (outputBatch.IsFull)
                        {
                            yield return outputBatch;
                            outputBatch = null;
                        }
                    }
                }
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
        finally
        {
            CleanupSpillFiles(spillWriters);
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
                    return scan.EstimatedRowCount;
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
    /// Parallel hash-based aggregation using key-partitioned fan-out.
    /// <para>
    /// For keyed aggregation the feeder evaluates each row's GROUP BY keys and routes the
    /// row to the worker whose partition index matches <c>(uint)keyHash % workerCount</c>.
    /// Each worker therefore maintains a hash table that covers a disjoint subset of the
    /// key space, so no cross-worker group duplication occurs. Peak memory is proportional
    /// to the total number of distinct groups rather than <c>DOP × distinct groups</c>.
    /// The merge phase is unnecessary because every group key lands in exactly one worker.
    /// </para>
    /// <para>
    /// Global aggregation (no GROUP BY keys) has no routing key to distribute on, so it
    /// falls back to a shared channel with round-robin fan-out and merges the partial
    /// per-worker global groups at the end.
    /// </para>
    /// <para>
    /// Supports spill-to-disk: each worker is allocated <c>MemoryBudgetBytes / workerCount</c>
    /// bytes. When a worker exceeds its share it writes raw input rows to hash-partitioned
    /// per-worker disk files and re-aggregates them in a drain phase after input is exhausted,
    /// skipping groups already complete in the worker's in-memory table.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteParallelHashAsync(ExecutionContext context)
    {
        bool useSingleKey = _groupByExpressions.Count == 1;
        bool isGlobalAggregation = _groupByExpressions.Count == 0;
        CancellationToken cancellationToken = context.CancellationToken;

        // Acquire worker slots from the optional global budget.
        int desiredWorkers = context.DegreeOfParallelism;
        int acquiredFromBudget = 0;

        if (context.ParallelismBudget is ParallelismBudget budget)
        {
            acquiredFromBudget = budget.TryAcquire(desiredWorkers);
            desiredWorkers = Math.Max(1, acquiredFromBudget);
        }

        // Per-worker spill arrays allocated by the keyed path and cleaned up in finally.
        BinaryWriter?[]?[]? workerSpillWriters = null;
        string?[]? workerSpillDirectories = null;

        try
        {
            int workerCount = desiredWorkers;

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

                GroupState[] workerGlobalGroups = new GroupState[workerCount];
                for (int i = 0; i < workerCount; i++)
                {
                    workerGlobalGroups[i] = CreateGroupState();
                }

                Task globalFeeder = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
                        {
                            for (int i = 0; i < inputBatch.Count; i++)
                            {
                                await globalChannel.Writer.WriteAsync(inputBatch[i], cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            inputBatch.Return();
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
                            context.FunctionRegistry, context.QueryMeter, context.OuterRow);

                        (DataValue[][] workerArgScratch, DataValue[]?[]? workerSortScratch) =
                            CreateAggregateArgumentScratch();

                        await foreach (Row row in globalChannel.Reader.ReadAllAsync(cancellationToken)
                            .ConfigureAwait(false))
                        {
                            EvaluateAggregateArgumentsInto(
                                workerEvaluator, row, workerArgScratch, workerSortScratch);
                            AccumulateRow(workerGlobalGroups[wi], workerArgScratch, workerSortScratch, context);
                            context.LocalBufferPool.ReturnValues(row);
                        }
                    }, cancellationToken);
                }

                await globalFeeder.ConfigureAwait(false);
                await Task.WhenAll(globalWorkers).ConfigureAwait(false);

                for (int i = 1; i < workerCount; i++)
                {
                    MergeGroupState(workerGlobalGroups[0], workerGlobalGroups[i]);
                }

                string[]? globalOutputNames = null;
                Dictionary<string, int>? globalOutputNameIndex = null;

                bool globalHasOrderedAggregates = _aggregateColumns.Any(
                    column => column.OrderBy is not null);

                if (globalHasOrderedAggregates)
                {
                    FlushOrderedBuffers([workerGlobalGroups[0]], context);
                }

                RowBatch globalOutputBatch = RowBatch.Rent(context.BatchSize);
                globalOutputBatch.Add(EmitGroupRow(
                    workerGlobalGroups[0], isGlobalAggregation: true,
                    ref globalOutputNames, ref globalOutputNameIndex));
                yield return globalOutputBatch;

                yield break;
            }

            // ----------------------------------------------------------------
            // Keyed aggregation path: partitioned fan-out by GROUP BY key hash.
            //
            // The feeder evaluates the GROUP BY keys for each input row and
            // writes the row to inputChannels[(uint)keyHash % workerCount].
            // Because every row that maps to a given group key always routes to
            // the same worker, each worker's hash table contains a disjoint
            // subset of the full key space. Total memory across all workers
            // equals the single-threaded cost; no merge phase is needed.
            // ----------------------------------------------------------------
            Channel<Row>[] inputChannels = new Channel<Row>[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                inputChannels[i] = Channel.CreateBounded<Row>(
                    new BoundedChannelOptions(64)
                    {
                        SingleWriter = true,
                        SingleReader = true,
                    });
            }

            // Allocate only the table array that matches the key arity.
            Dictionary<DataValue, GroupState>[] singleKeyTables =
                new Dictionary<DataValue, GroupState>[useSingleKey ? workerCount : 0];
            for (int i = 0; i < singleKeyTables.Length; i++)
            {
                singleKeyTables[i] = new();
            }

            Dictionary<CompositeKey, GroupState>[] compositeKeyTables =
                new Dictionary<CompositeKey, GroupState>[useSingleKey ? 0 : workerCount];
            for (int i = 0; i < compositeKeyTables.Length; i++)
            {
                compositeKeyTables[i] = new Dictionary<CompositeKey, GroupState>(CompositeKeyComparer.Instance);
            }

            // Per-worker spill infrastructure. Workers own disjoint key partitions so their
            // spill files are also disjoint — no cross-worker coordination is needed during drain.
            long? memoryBudget = context.MemoryBudgetBytes;
            long? workerBudget = memoryBudget.HasValue ? memoryBudget.Value / workerCount : null;

            MemoryEstimator?[] workerEstimators = new MemoryEstimator?[workerCount];
            bool[] workerSpilling = new bool[workerCount];
            bool[]?[] workerSchemaWritten = new bool[]?[workerCount];
            string[]?[] workerLocalSpillPaths = new string[]?[workerCount];
            SpillSchemaState[] workerSchemaStates = new SpillSchemaState[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                workerSchemaStates[i] = new SpillSchemaState();
                if (workerBudget.HasValue)
                {
                    workerEstimators[i] = new MemoryEstimator();
                }
            }

            // Initialise the outer cleanup arrays before any worker runs.
            workerSpillWriters = new BinaryWriter?[]?[workerCount];
            workerSpillDirectories = new string?[workerCount];

            // Feeder: evaluates GROUP BY keys and routes each row to the owning partition.
            Task feederTask = Task.Run(async () =>
            {
                ExpressionEvaluator routingEvaluator = new(
                    context.FunctionRegistry, context.QueryMeter, context.OuterRow);
                try
                {
                    await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
                    {
                        for (int batchIndex = 0; batchIndex < inputBatch.Count; batchIndex++)
                        {
                        Row row = inputBatch[batchIndex];
                        int partition;

                        if (useSingleKey)
                        {
                            DataValue key = routingEvaluator.Evaluate(_groupByExpressions[0], row);
                            partition = (int)((uint)key.GetHashCode() % (uint)workerCount);
                        }
                        else
                        {
                            HashCode keyHash = new();
                            for (int k = 0; k < _groupByExpressions.Count; k++)
                            {
                                keyHash.Add(routingEvaluator.Evaluate(_groupByExpressions[k], row));
                            }

                            partition = (int)((uint)keyHash.ToHashCode() % (uint)workerCount);
                        }

                        await inputChannels[partition].Writer.WriteAsync(row, cancellationToken)
                            .ConfigureAwait(false);
                        }

                        inputBatch.Return();
                    }
                }
                finally
                {
                    foreach (Channel<Row> channel in inputChannels)
                    {
                        channel.Writer.Complete();
                    }
                }
            }, cancellationToken);

            // Workers: each reads its dedicated channel and accumulates its disjoint key partition.
            Task[] workers = new Task[workerCount];
            for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                int wi = workerIndex;
                workers[wi] = Task.Run(async () =>
                {
                    ExpressionEvaluator workerEvaluator = new(
                        context.FunctionRegistry, context.QueryMeter, context.OuterRow);
                    MemoryEstimator? estimator = workerEstimators[wi];

                    (DataValue[][] workerArgScratch, DataValue[]?[]? workerSortScratch) =
                        CreateAggregateArgumentScratch();

                    await foreach (Row row in inputChannels[wi].Reader.ReadAllAsync(cancellationToken)
                        .ConfigureAwait(false))
                    {
                        // Evaluate group keys once for both accumulation and spill routing.
                        DataValue singleKeyValue = default;
                        DataValue[]? keyValues = null;

                        if (useSingleKey)
                        {
                            singleKeyValue = workerEvaluator.Evaluate(_groupByExpressions[0], row);
                        }
                        else
                        {
                            keyValues = new DataValue[_groupByExpressions.Count];
                            for (int k = 0; k < _groupByExpressions.Count; k++)
                            {
                                keyValues[k] = workerEvaluator.Evaluate(_groupByExpressions[k], row);
                            }
                        }

                        EvaluateAggregateArgumentsInto(
                            workerEvaluator, row, workerArgScratch, workerSortScratch);

                        if (workerSpilling[wi])
                        {
                            // Route to a per-worker spill partition file.
                            int hashCode = useSingleKey
                                ? singleKeyValue.GetHashCode()
                                : new CompositeKey(keyValues!).GetHashCode();

                            WriteSpillRow(
                                hashCode, useSingleKey ? [singleKeyValue] : keyValues!,
                                workerArgScratch, workerSortScratch,
                                workerSpillWriters![wi]!, workerSchemaWritten[wi]!,
                                workerLocalSpillPaths[wi]!, workerSchemaStates[wi],
                                workerSpillDirectories![wi]!);

                            // Also accumulate into any pre-existing in-memory group so that
                            // rows arriving after spill starts are not lost for those keys.
                            // ReaggregatePartition skips these keys during drain because
                            // they are already present in the worker's in-memory table.
                            GroupState? existingGroup = null;

                            if (useSingleKey)
                            {
                                singleKeyTables[wi].TryGetValue(singleKeyValue, out existingGroup);
                            }
                            else
                            {
                                compositeKeyTables[wi].TryGetValue(
                                    new CompositeKey(keyValues!), out existingGroup);
                            }

                            if (existingGroup is not null)
                            {
                                AccumulateRow(existingGroup, workerArgScratch, workerSortScratch, context);
                            }
                        }
                        else
                        {
                            // Accumulate in memory.
                            GroupState group;

                            if (useSingleKey)
                            {
                                if (!singleKeyTables[wi].TryGetValue(singleKeyValue, out GroupState? existing))
                                {
                                    existing = CreateGroupState();
                                    existing.KeyValues = [singleKeyValue];
                                    singleKeyTables[wi][singleKeyValue] = existing;
                                }

                                group = existing!;
                            }
                            else
                            {
                                CompositeKey compositeKey = new(keyValues!);

                                if (!compositeKeyTables[wi].TryGetValue(compositeKey, out GroupState? existing))
                                {
                                    existing = CreateGroupState();
                                    existing.KeyValues = keyValues;
                                    compositeKeyTables[wi][compositeKey] = existing;
                                }

                                group = existing;
                            }

                            AccumulateRow(group, workerArgScratch, workerSortScratch, context);

                            // Per-worker memory estimation against the worker's share of the budget.
                            if (estimator is not null)
                            {
                                if (estimator.ShouldSample())
                                {
                                    estimator.RecordSample(row);
                                }

                                estimator.IncrementRowCount();
                                long groupCount = useSingleKey
                                    ? (long)singleKeyTables[wi].Count
                                    : (long)compositeKeyTables[wi].Count;
                                long estimatedMemory = estimator.EstimateBytesForRowCount(groupCount);

                                if (estimatedMemory > workerBudget!.Value)
                                {
                                    workerSpilling[wi] = true;
                                    workerSpillDirectories![wi] = Path.Combine(
                                        Path.GetTempPath(), $"datum-groupby-{Guid.NewGuid():N}-w{wi}");
                                    Directory.CreateDirectory(workerSpillDirectories![wi]!);
                                    workerSpillWriters![wi] = new BinaryWriter?[SpillPartitionCount];
                                    workerSchemaWritten[wi] = new bool[SpillPartitionCount];
                                    workerLocalSpillPaths[wi] = new string[SpillPartitionCount];

                                    if (ExecutionTracer.IsEnabled)
                                    {
                                        ExecutionTracer.Write(
                                            $"GROUP BY parallel spill start  worker={wi}  budget={ExecutionTracer.FormatBytes(workerBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}  groups={groupCount}");
                                    }
                                }
                                else if (estimatedMemory > (long)(workerBudget!.Value * MemoryEstimator.EscalationThreshold))
                                {
                                    estimator.EscalateToEveryRow();
                                }
                            }
                        }
                    }
                }, cancellationToken);
            }

            await feederTask.ConfigureAwait(false);
            await Task.WhenAll(workers).ConfigureAwait(false);

            // No merge needed — every group key was routed to exactly one worker.
            // Flush ordered buffers, emit in-memory groups, then drain any spill files per worker.
            string[]? outputNames = null;
            Dictionary<string, int>? outputNameIndex = null;

            bool hasOrderedAggregates = _aggregateColumns.Any(
                column => column.OrderBy is not null);

            RowBatch? outputBatch = null;

            for (int i = 0; i < workerCount; i++)
            {
                IEnumerable<GroupState> workerGroups = useSingleKey
                    ? (IEnumerable<GroupState>)singleKeyTables[i].Values
                    : compositeKeyTables[i].Values;

                if (hasOrderedAggregates)
                {
                    FlushOrderedBuffers(workerGroups, context);
                }

                foreach (GroupState group in workerGroups)
                {
                    outputBatch ??= RowBatch.Rent(context.BatchSize);
                    outputBatch.Add(EmitGroupRow(
                        group, isGlobalAggregation: false, ref outputNames, ref outputNameIndex));
                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }

                // Return completed in-memory GroupState objects to the static pool.
                GlobalBufferPool.ReturnGroupStates(workerGroups, _aggregateColumns.Count);

                // Drain phase: re-aggregate spill files for this worker, skipping keys
                // already complete in the worker's in-memory table.
                if (workerSpilling[i])
                {
                    FlushSpillWriters(workerSpillWriters![i]!);

                    for (int partition = 0; partition < SpillPartitionCount; partition++)
                    {
                        if (workerLocalSpillPaths[i] is null || workerLocalSpillPaths[i]![partition] is null)
                        {
                            continue;
                        }

                        foreach (Row groupRow in ReaggregatePartition(
                            workerLocalSpillPaths[i]![partition],
                            useSingleKey,
                            useSingleKey ? singleKeyTables[i] : null,
                            useSingleKey ? null : compositeKeyTables[i],
                            hasOrderedAggregates, context,
                            ref outputNames, ref outputNameIndex))
                        {
                            outputBatch ??= RowBatch.Rent(context.BatchSize);
                            outputBatch.Add(groupRow);
                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                }
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
        finally
        {
            if (acquiredFromBudget > 0)
            {
                context.ParallelismBudget!.Release(acquiredFromBudget);
            }

            // Best-effort cleanup of any per-worker spill state created by the keyed path.
            if (workerSpillWriters is not null)
            {
                for (int i = 0; i < workerSpillWriters.Length; i++)
                {
                    CleanupSpillFiles(workerSpillWriters[i]);
                }
            }

            CleanupWorkerSpillDirectories(workerSpillDirectories);
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

    private GroupState CreateGroupState()
    {
        int count = _aggregateColumns.Count;
        GroupState state = GlobalBufferPool.RentGroupState(count);
        RuntimeTypeHandle[]? innerTypes = _accumulatorInnerTypes;

        for (int index = 0; index < count; index++)
        {
            AggregateColumn column = _aggregateColumns[index];
            IAggregateAccumulator? existing = state.Accumulators[index];
            IAggregateAccumulator? accumulator = null;

            // Try to reuse the accumulator left in the pooled array from the
            // previous owner. A type-handle comparison avoids creating fresh
            // objects for the overwhelmingly common same-operator-shape case.
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
                        accumulator, column.ArgumentExpressions.Count);
                }
            }

            state.Accumulators[index] = accumulator;

            if (column.OrderBy is not null)
            {
                state.OrderedBuffers ??= new List<(DataValue[], DataValue[])>?[count];
                state.OrderedBuffers[index] = [];
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
        ExecutionContext context)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

            if (aggregateColumn.OrderBy is not null && allSortKeys?[aggregateIndex] is DataValue[] sortKeys)
            {
                // Ordered aggregate buffers retain tuples until flush — copy the
                // scratch arrays so the next row doesn't overwrite buffered data.
                group.OrderedBuffers![aggregateIndex]!.Add(
                    (allArguments[aggregateIndex].ToArray(), sortKeys.ToArray()));
            }
            else
            {
                group.Accumulators[aggregateIndex].Accumulate(allArguments[aggregateIndex]);
                context.QueryMeter?.Add(aggregateColumn.Function.QueryUnitCost);
            }
        }
    }

    /// <summary>
    /// Flushes ordered aggregate buffers by sorting and accumulating deferred rows.
    /// </summary>
    private void FlushOrderedBuffers(IEnumerable<GroupState> groups, ExecutionContext context)
    {
        foreach (GroupState groupState in groups)
        {
            FlushOrderedBuffersForGroup(groupState, context);
        }
    }

    /// <summary>
    /// Flushes ordered aggregate buffers for a single group by sorting and
    /// accumulating deferred rows.
    /// </summary>
    private void FlushOrderedBuffersForGroup(GroupState groupState, ExecutionContext context)
    {
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];
            if (aggregateColumn.OrderBy is null) continue;

            List<(DataValue[] Arguments, DataValue[] SortKeys)> buffer =
                groupState.OrderedBuffers![aggregateIndex]!;

            IReadOnlyList<OrderByItem> orderByItems = aggregateColumn.OrderBy;

            buffer.Sort((a, b) =>
            {
                for (int sortIndex = 0; sortIndex < orderByItems.Count; sortIndex++)
                {
                    int comparison = OrderByOperator.CompareDataValues(
                        a.SortKeys[sortIndex], b.SortKeys[sortIndex]);

                    if (orderByItems[sortIndex].Direction == SortDirection.Descending)
                    {
                        comparison = -comparison;
                    }

                    if (comparison != 0) return comparison;
                }
                return 0;
            });

            foreach ((DataValue[] arguments, _) in buffer)
            {
                groupState.Accumulators[aggregateIndex].Accumulate(arguments);
                context.QueryMeter?.Add(aggregateColumn.Function.QueryUnitCost);
            }
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
        ref string[]? outputNames,
        ref Dictionary<string, int>? outputNameIndex)
    {
        int outputFieldCount = _groupByExpressions.Count + _aggregateColumns.Count;

        if (outputNames is null)
        {
            outputNames = new string[outputFieldCount];

            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                outputNames[index] = QueryExplainer.FormatExpression(_groupByExpressions[index]);
            }

            for (int index = 0; index < _aggregateColumns.Count; index++)
            {
                outputNames[_groupByExpressions.Count + index] = _aggregateColumns[index].OutputName;
            }

            outputNameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < outputNames.Length; index++)
            {
                outputNameIndex[outputNames[index]] = index;
            }
        }

        DataValue[] values = GlobalBufferPool.Rent(outputFieldCount);

        if (!isGlobalAggregation)
        {
            for (int index = 0; index < _groupByExpressions.Count; index++)
            {
                values[index] = group.KeyValues![index];
            }
        }

        for (int index = 0; index < _aggregateColumns.Count; index++)
        {
            values[_groupByExpressions.Count + index] = group.Accumulators[index].Result;
        }

        return new Row(outputNames, values, outputNameIndex!);
    }

    // ---------------------------------------------------------------
    //  Spill-to-disk infrastructure
    // ---------------------------------------------------------------

    /// <summary>
    /// Writes a spill row containing group keys and all aggregate arguments.
    /// The schema is: key_0, ..., key_N, arg_0_0, ..., arg_M_K, [sort_0_0, ...].
    /// </summary>
    private void WriteSpillRow(
        int hashCode,
        DataValue[] keyValues,
        DataValue[][] allArguments,
        DataValue[]?[]? allSortKeys,
        BinaryWriter?[] writers,
        bool[] schemaWritten,
        string[] paths,
        SpillSchemaState schemaState,
        string spillDirectory)
    {
        int partition = AssignPartition(hashCode);

        if (writers[partition] is null)
        {
            paths[partition] = Path.Combine(spillDirectory, $"groupby_{partition}.spill");
            FileStream fileStream = new(paths[partition], FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            writers[partition] = new BinaryWriter(fileStream);
        }

        // Build the schema once (all spill rows share the same layout).
        if (schemaState.SchemaNames is null)
        {
            List<string> names = new();

            for (int index = 0; index < keyValues.Length; index++)
            {
                names.Add($"__key_{index}");
            }

            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                for (int argIndex = 0; argIndex < allArguments[aggregateIndex].Length; argIndex++)
                {
                    names.Add($"__arg_{aggregateIndex}_{argIndex}");
                }

                if (allSortKeys?[aggregateIndex] is DataValue[] sortKeys)
                {
                    for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                    {
                        names.Add($"__sort_{aggregateIndex}_{sortIndex}");
                    }
                }
            }

            schemaState.SchemaNames = names.ToArray();
            schemaState.ColumnCount = names.Count;
            schemaState.FlatValues = new DataValue[names.Count];
            schemaState.SpillRow = new Row(schemaState.SchemaNames, schemaState.FlatValues);
        }

        // Build the flat values array (reuses the buffer cached on schemaState).
        DataValue[] flatValues = schemaState.FlatValues!;
        int offset = 0;

        for (int index = 0; index < keyValues.Length; index++)
        {
            flatValues[offset++] = keyValues[index];
        }

        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            for (int argIndex = 0; argIndex < allArguments[aggregateIndex].Length; argIndex++)
            {
                flatValues[offset++] = allArguments[aggregateIndex][argIndex];
            }

            if (allSortKeys?[aggregateIndex] is DataValue[] sortKeys)
            {
                for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                {
                    flatValues[offset++] = sortKeys[sortIndex];
                }
            }
        }

        Row spillRow = schemaState.SpillRow.GetValueOrDefault();

        if (!schemaWritten[partition])
        {
            RowSerializer.WriteSchema(writers[partition]!, spillRow);
            schemaWritten[partition] = true;
        }

        RowSerializer.WriteRow(writers[partition]!, spillRow);
    }

    /// <summary>
    /// Reads back a spill partition and re-aggregates its rows, returning one output
    /// row per group. Groups that were already aggregated in memory are skipped.
    /// </summary>
    /// <summary>
    /// Re-aggregates a single spill partition file, skipping groups that
    /// already exist in the caller's in-memory hash table (Dictionary-based).
    /// Used by the parallel aggregation path.
    /// </summary>
    private List<Row> ReaggregatePartition(
        string path,
        bool useSingleKey,
        Dictionary<DataValue, GroupState>? inMemorySingleKeyGroups,
        Dictionary<CompositeKey, GroupState>? inMemoryCompositeKeyGroups,
        bool hasOrderedAggregates,
        ExecutionContext context,
        ref string[]? outputNames,
        ref Dictionary<string, int>? outputNameIndex)
    {
        Func<DataValue[], bool> isKeyInMemory = useSingleKey
            ? keyValues => inMemorySingleKeyGroups!.ContainsKey(keyValues[0])
            : keyValues => inMemoryCompositeKeyGroups!.ContainsKey(new CompositeKey(keyValues));

        return ReaggregatePartitionCore(
            path, useSingleKey, isKeyInMemory, hasOrderedAggregates,
            context, ref outputNames, ref outputNameIndex);
    }

    /// <summary>
    /// Re-aggregates a single spill partition file, skipping groups that
    /// already exist in the caller's in-memory custom hash maps.
    /// Used by the serial aggregation path.
    /// </summary>
    private List<Row> ReaggregatePartition(
        string path,
        bool useSingleKey,
        DataValueHashMap<GroupState>? inMemorySingleKeyGroups,
        CompositeKeyHashMap<GroupState>? inMemoryCompositeKeyGroups,
        bool hasOrderedAggregates,
        ExecutionContext context,
        ref string[]? outputNames,
        ref Dictionary<string, int>? outputNameIndex)
    {
        Func<DataValue[], bool> isKeyInMemory = useSingleKey
            ? keyValues => inMemorySingleKeyGroups!.ContainsKey(keyValues[0])
            : keyValues => inMemoryCompositeKeyGroups!.ContainsKey(keyValues.AsSpan());

        return ReaggregatePartitionCore(
            path, useSingleKey, isKeyInMemory, hasOrderedAggregates,
            context, ref outputNames, ref outputNameIndex);
    }

    /// <summary>
    /// Core spill-partition re-aggregation logic shared by both the
    /// <see cref="Dictionary{TKey,TValue}"/>-based and custom hash map overloads.
    /// </summary>
    private List<Row> ReaggregatePartitionCore(
        string path,
        bool useSingleKey,
        Func<DataValue[], bool> isKeyInMemory,
        bool hasOrderedAggregates,
        ExecutionContext context,
        ref string[]? outputNames,
        ref Dictionary<string, int>? outputNameIndex)
    {
        int keyCount = _groupByExpressions.Count;
        Dictionary<DataValue, GroupState>? partitionSingleGroups =
            useSingleKey ? new() : null;
        Dictionary<CompositeKey, GroupState>? partitionCompositeGroups =
            !useSingleKey ? new() : null;

        using FileStream fileStream = new(path, FileMode.Open, FileAccess.Read, FileShare.None, 65536);
        using BinaryReader reader = new(fileStream);

        RowSerializer.ReadSchema(reader, out string[] schemaNames, out Dictionary<string, int> schemaNameIndex);

        while (fileStream.Position < fileStream.Length)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            Row spillRow = RowSerializer.ReadRow(reader, schemaNames, schemaNameIndex);

            // Extract group keys from the spill row.
            DataValue[] keyValues = new DataValue[keyCount];
            for (int index = 0; index < keyCount; index++)
            {
                keyValues[index] = spillRow[index];
            }

            // Skip rows whose group was already aggregated in memory.
            if (isKeyInMemory(keyValues))
            {
                continue;
            }

            // Resolve or create the group for this partition.
            GroupState group;

            if (useSingleKey)
            {
                if (!partitionSingleGroups!.TryGetValue(keyValues[0], out group!))
                {
                    group = CreateGroupState();
                    group.KeyValues = keyValues;
                    partitionSingleGroups[keyValues[0]] = group;
                }
            }
            else
            {
                CompositeKey compositeKey = new(keyValues);

                if (!partitionCompositeGroups!.TryGetValue(compositeKey, out group!))
                {
                    group = CreateGroupState();
                    group.KeyValues = keyValues;
                    partitionCompositeGroups[compositeKey] = group;
                }
            }

            // Extract aggregate arguments from the spill row and accumulate.
            int offset = keyCount;

            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                AggregateColumn aggregateColumn = _aggregateColumns[aggregateIndex];

                if (aggregateColumn.IsCountStar)
                {
                    group.Accumulators[aggregateIndex].Accumulate(ReadOnlySpan<DataValue>.Empty);
                }
                else
                {
                    int argCount = aggregateColumn.ArgumentExpressions.Count;
                    DataValue[] arguments = new DataValue[argCount];
                    for (int argIndex = 0; argIndex < argCount; argIndex++)
                    {
                        arguments[argIndex] = spillRow[offset++];
                    }

                    if (aggregateColumn.OrderBy is not null)
                    {
                        int sortCount = aggregateColumn.OrderBy.Count;
                        DataValue[] sortKeys = new DataValue[sortCount];
                        for (int sortIndex = 0; sortIndex < sortCount; sortIndex++)
                        {
                            sortKeys[sortIndex] = spillRow[offset++];
                        }

                        group.OrderedBuffers![aggregateIndex]!.Add((arguments, sortKeys));
                    }
                    else
                    {
                        group.Accumulators[aggregateIndex].Accumulate(arguments);
                    }
                }
            }
        }

        // Flush ordered buffers for this partition.
        if (hasOrderedAggregates)
        {
            IEnumerable<GroupState> partitionGroups = useSingleKey
                ? partitionSingleGroups!.Values
                : partitionCompositeGroups!.Values;

            FlushOrderedBuffers(partitionGroups, context);
        }

        // Emit one row per group in this partition.
        IEnumerable<GroupState> allPartitionGroups = useSingleKey
            ? partitionSingleGroups!.Values
            : partitionCompositeGroups!.Values;

        List<Row> results = new();
        foreach (GroupState group in allPartitionGroups)
        {
            results.Add(EmitGroupRow(group, isGlobalAggregation: false, ref outputNames, ref outputNameIndex));
        }

        // Return partition GroupState objects to the static pool.
        GlobalBufferPool.ReturnGroupStates(allPartitionGroups, _aggregateColumns.Count);

        return results;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AssignPartition(int hashCode)
    {
        return (int)((uint)hashCode % SpillPartitionCount);
    }

    private static void FlushSpillWriters(BinaryWriter?[] writers)
    {
        for (int index = 0; index < writers.Length; index++)
        {
            if (writers[index] is not null)
            {
                writers[index]!.Flush();
                writers[index]!.Dispose();
                writers[index] = null;
            }
        }
    }

    private void CleanupSpillFiles(BinaryWriter?[]? writers)
    {
        if (writers is not null)
        {
            for (int index = 0; index < writers.Length; index++)
            {
                try
                {
                    writers[index]?.Dispose();
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }

        CleanupSpillDirectory();
    }

    private void CleanupSpillDirectory()
    {
        if (_spillDirectory is not null && Directory.Exists(_spillDirectory))
        {
            try
            {
                Directory.Delete(_spillDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }

            _spillDirectory = null;
        }
    }

    /// <summary>
    /// Deletes each non-null directory in <paramref name="directories"/> created by
    /// <see cref="ExecuteParallelHashAsync"/> workers during spill-to-disk.
    /// Called from the method's <see langword="finally"/> block; errors are silently swallowed.
    /// </summary>
    private static void CleanupWorkerSpillDirectories(string?[]? directories)
    {
        if (directories is null)
        {
            return;
        }

        for (int i = 0; i < directories.Length; i++)
        {
            if (directories[i] is not null && Directory.Exists(directories[i]))
            {
                try
                {
                    Directory.Delete(directories[i]!, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CleanupSpillDirectory();
    }

    /// <summary>
    /// Mutable spill-row schema state for one spill file set. Built lazily on the first
    /// row written and reused for all subsequent rows in the same spill context.
    /// </summary>
    private sealed class SpillSchemaState
    {
        /// <summary>Column names for the flat spill row layout.</summary>
        public string[]? SchemaNames;

        /// <summary>Total number of columns in the spill row layout.</summary>
        public int ColumnCount;

        /// <summary>Reusable flat values buffer, allocated once when schema is built.</summary>
        public DataValue[]? FlatValues;

        /// <summary>Reusable spill row, allocated once when schema is built.</summary>
        public Row? SpillRow;
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
