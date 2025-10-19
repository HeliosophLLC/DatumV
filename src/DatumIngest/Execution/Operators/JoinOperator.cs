using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Threading.Channels;
using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Indexing.Bloom;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Join operator supporting INNER, LEFT, RIGHT, FULL OUTER, CROSS,
/// LEFT SEMI, and LEFT ANTI-SEMI joins.
/// Uses expression-based hash join for any ON condition containing equality
/// conjuncts (including function calls and compound keys), with an optional
/// residual filter for non-equi parts. Falls back to nested-loop only when
/// no equalities can be extracted.
/// </summary>
public sealed class JoinOperator : IQueryOperator
{
    private readonly IQueryOperator _left;
    private readonly IQueryOperator _right;
    private readonly JoinType _joinType;
    private readonly Expression? _onCondition;
    private readonly bool _nullSensitiveAntiSemi;
    private readonly bool _flipped;
    private readonly bool _preferIndexNestedLoop;

    /// <summary>
    /// Creates a join operator.
    /// </summary>
    /// <param name="left">The left (probe) side operator.</param>
    /// <param name="right">The right (build) side operator.</param>
    /// <param name="joinType">The type of join.</param>
    /// <param name="onCondition">The ON condition expression (null for CROSS join).</param>
    /// <param name="nullSensitiveAntiSemi">
    /// When true and <paramref name="joinType"/> is <see cref="JoinType.LeftAntiSemi"/>,
    /// applies SQL-standard NOT IN null semantics: if any right-side key is NULL the
    /// entire result is empty, and left rows with a NULL key are excluded.
    /// </param>
    /// <param name="flipped">
    /// When <c>true</c>, the build and probe sides are physically swapped so the
    /// smaller side (left) is materialized into the hash table while the larger side
    /// (right) is streamed. Output column order is preserved as [left | right].
    /// </param>
    /// <param name="preferIndexNestedLoop">
    /// When <c>true</c>, the planner has determined at plan time that an index
    /// nested-loop join is preferred for this join (indexed build side + LIMIT
    /// detected). The <see cref="ExecutionContext.RowLimit"/> runtime guard is bypassed so the
    /// executor activates even before the <see cref="LimitOperator"/> propagates
    /// its context downstream.
    /// </param>
    public JoinOperator(
        IQueryOperator left,
        IQueryOperator right,
        JoinType joinType,
        Expression? onCondition,
        bool nullSensitiveAntiSemi = false,
        bool flipped = false,
        bool preferIndexNestedLoop = false)
    {
        _left = left;
        _right = right;
        _joinType = joinType;
        _onCondition = onCondition;
        _nullSensitiveAntiSemi = nullSensitiveAntiSemi;
        _flipped = flipped;
        _preferIndexNestedLoop = preferIndexNestedLoop;
    }

    /// <summary>The left (probe) side operator.</summary>
    public IQueryOperator Left => _left;

    /// <summary>The right (build) side operator.</summary>
    public IQueryOperator Right => _right;

    /// <summary>The type of join.</summary>
    public JoinType Type => _joinType;

    /// <summary>The ON condition expression.</summary>
    public Expression? OnCondition => _onCondition;

    /// <summary>
    /// When <c>true</c> and the join type is <see cref="JoinType.LeftAntiSemi"/>,
    /// applies SQL-standard NOT IN null semantics: if any right-side key is NULL the
    /// entire result is empty, and left rows with a NULL key are excluded.
    /// </summary>
    public bool NullSensitiveAntiSemi => _nullSensitiveAntiSemi;

    /// <summary>
    /// When <c>true</c>, the build and probe sides are physically swapped so the
    /// smaller side (left) is materialized into the hash table while the larger side
    /// (right) is streamed. Output column order is preserved as [left | right].
    /// </summary>
    public bool Flipped => _flipped;

    /// <summary>
    /// When <c>true</c>, the planner has flagged this join for index nested-loop
    /// execution at plan time. The runtime <see cref="ExecutionContext.RowLimit"/>
    /// guard in <see cref="TryCreateIndexNestedLoopExecutor"/> is bypassed so NLJ
    /// activates regardless of whether a <see cref="LimitOperator"/> has propagated
    /// its context hint yet.
    /// </summary>
    public bool PreferIndexNestedLoop => _preferIndexNestedLoop;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        string joinTypeName = _joinType switch
        {
            JoinType.Inner => "Inner",
            JoinType.Left => "Left",
            JoinType.Right => "Right",
            JoinType.FullOuter => "Full Outer",
            JoinType.Cross => "Cross",
            JoinType.LeftSemi => "Left Semi",
            JoinType.LeftAntiSemi => "Left Anti-Semi",
            _ => _joinType.ToString(),
        };

        Dictionary<string, string> properties = new()
        {
            ["type"] = joinTypeName,
        };

        if (_onCondition is not null)
        {
            properties["on"] = QueryExplainer.FormatExpression(_onCondition);
        }

        List<string> warnings = [];
        if (_joinType == JoinType.Cross)
        {
            warnings.Add("cross join — produces cartesian product");
        }
        else if (_joinType == JoinType.FullOuter)
        {
            warnings.Add("full outer join — materializes both sides");
        }

        if (_flipped)
        {
            properties["flipped"] = "true";
        }

        if (_preferIndexNestedLoop)
        {
            properties["index-nested-loop"] = "true";
        }

        return new OperatorPlanDescription($"{joinTypeName} Join")
        {
            Properties = properties,
            Children = _flipped
                ? [(Left, "build"), (Right, "probe")]
                : [(Left, "probe"), (Right, "build")],
            Warnings = warnings,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        if (_joinType == JoinType.Cross)
        {
            await foreach (RowBatch batch in ExecuteCrossJoinAsync(context).ConfigureAwait(false))
            {
                yield return batch;
            }
            yield break;
        }

        // Extract equi-join keys from the ON condition. Supports arbitrary
        // expressions (function calls, CAST, etc.) and compound AND keys.
        // Non-equality conjuncts become a residual filter applied after hash match.
        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(_onCondition);

        if (extraction is not null)
        {
            // Try index nested loop join when a sorted index exists on the build-side
            // join column. This is optimal under LIMIT with few probe rows.
            IndexNestedLoopJoinExecutor? indexNlj = TryCreateIndexNestedLoopExecutor(extraction, context);

            ExecutionTracer.Write($"JOIN execute  INLJ={indexNlj is not null}  budget={context.MemoryBudgetBytes}  rowLimit={context.RowLimit}  flipped={_flipped}");

            if (indexNlj is not null)
            {
                ExecutionTracer.Write("JOIN INLJ trial starting");
                await foreach (RowBatch batch in indexNlj.ExecuteAsync(_left, _right, context).ConfigureAwait(false))
                {
                    ExecutionTracer.Write($"JOIN INLJ yielded batch count={batch.Count}");
                    yield return batch;
                }

                ExecutionTracer.Write($"JOIN INLJ done  circuitBreaker={indexNlj.CircuitBreakerTripped}");

                if (!indexNlj.CircuitBreakerTripped)
                {
                    yield break;
                }

                // Circuit breaker tripped — NLJ exceeded its probe-row trial
                // budget and yielded nothing (output was buffered and discarded).
                // Fall through to hash join for correct execution.
            }

            if (context.MemoryBudgetBytes is long memoryBudget)
            {
                ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);
                IQueryOperator buildSide = _flipped ? _left : _right;
                long? estimatedBuildRows = GetEstimatedRowCount(buildSide);
                ExecutionTracer.Write($"JOIN GraceHash starting  build={GetOperatorLabel(buildSide)}  estimatedBuild={estimatedBuildRows}");
                GraceHashJoinExecutor graceExecutor = new(_joinType, extraction, memoryBudget, evaluator, _nullSensitiveAntiSemi, _flipped, label: GetOperatorLabel(buildSide), estimatedBuildRows: estimatedBuildRows);

                await foreach (RowBatch batch in graceExecutor.ExecuteAsync(_left, _right, context).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
            else
            {
                ExecutionTracer.Write("JOIN InMemoryHash starting");
                await foreach (RowBatch batch in ExecuteHashJoinAsync(context, extraction).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
        else
        {
            await foreach (RowBatch batch in ExecuteNestedLoopJoinAsync(context).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
    }

    /// <summary>
    /// Walks the operator tree to find the underlying <see cref="ScanOperator"/> and returns
    /// its table name, used as a human-readable label in execution trace output.
    /// Returns the operator's type name when no scan is found (e.g. a derived join result).
    /// </summary>
    private static string GetOperatorLabel(IQueryOperator op)
    {
        IQueryOperator current = op;
        while (true)
        {
            if (current is ScanOperator scan)
                return scan.Descriptor.Name;
            if (current is AliasOperator alias)
                current = alias.Source;
            else if (current is FilterOperator filter)
                current = filter.Source;
            else
                return current.GetType().Name;
        }
    }

    /// <summary>
    /// Attempts to create an <see cref="IndexNestedLoopJoinExecutor"/> for the current join.
    /// Returns <c>null</c> when the preconditions are not met (join type, single key,
    /// sorted index available, seekable provider, bounded row limit).
    /// </summary>
    private IndexNestedLoopJoinExecutor? TryCreateIndexNestedLoopExecutor(
        JoinKeyExtractionResult extraction, ExecutionContext context)
    {
        // Only use index NLJ when a LIMIT is active and small enough that
        // point-seeks are cheaper than building a full hash table — unless the
        // planner has already determined at plan time that NLJ is preferred
        // (PreferIndexNestedLoop), in which case the runtime RowLimit hint is
        // not required.
        const int IndexNestedLoopRowLimitThreshold = 1000;

        if (!_preferIndexNestedLoop
            && (context.RowLimit is not int rowLimit || rowLimit > IndexNestedLoopRowLimitThreshold))
        {
            return null;
        }

        // Index NLJ only supports INNER and LeftSemi.
        if (_joinType is not (JoinType.Inner or JoinType.LeftSemi))
        {
            return null;
        }

        // Only single-key equi-joins for now.
        if (extraction.KeyPairs.Count != 1)
        {
            return null;
        }

        // Build-side key must be a simple column reference to match against sorted index names.
        Expression buildKeyExpression = extraction.KeyPairs[0].Right;

        if (buildKeyExpression is not ColumnReference buildColumnRef)
        {
            return null;
        }

        // Find the build-side ScanOperator.
        List<ScanOperator> buildScans = new();
        CollectScanOperators(_right, buildScans);

        if (buildScans.Count != 1)
        {
            return null;
        }

        ScanOperator buildScan = buildScans[0];

        if (buildScan.SourceIndex is null)
        {
            return null;
        }

        // Try both qualified and unqualified column names against the column index.
        string? indexColumnName = buildColumnRef.QualifiedName ?? buildColumnRef.ColumnName;

        if (!buildScan.SourceIndex.TryGetColumnIndex(indexColumnName, out IColumnIndex? columnIndex))
        {
            // Try unqualified name if qualified failed.
            if (buildColumnRef.QualifiedName is not null
                && !buildScan.SourceIndex.TryGetColumnIndex(buildColumnRef.ColumnName, out columnIndex))
            {
                return null;
            }

            if (columnIndex is null)
            {
                return null;
            }
        }

        // Verify the provider supports seeking.
        ITableProvider provider = context.Catalog.CreateProvider(buildScan.Descriptor);
        if (provider is Catalog.Providers.DatumFileTableProvider datumProvider)
            datumProvider.Store = context.Store;

        if (provider is not ISeekableTableProvider)
        {
            return null;
        }

        // Extract the build-side alias so the executor can re-qualify rows
        // fetched directly from the seekable provider (which bypasses AliasOperator).
        string? buildAlias = FindBuildAlias(_right);

        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);

        return new IndexNestedLoopJoinExecutor(
            _joinType,
            extraction,
            columnIndex,
            buildScan.SourceIndex.Chunks,
            buildScan.Descriptor,
            buildAlias,
            evaluator);
    }

    private async IAsyncEnumerable<RowBatch> ExecuteHashJoinAsync(
        ExecutionContext context, JoinKeyExtractionResult extraction)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = extraction.KeyPairs;
        bool useSingleKey = keyPairs.Count == 1;
        bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;

        // Physical side assignment. Normally right=build, left=probe.
        // When flipped, left=build (smaller side materialized), right=probe (larger, streamed).
        IQueryOperator buildSource = _flipped ? _left : _right;
        IQueryOperator probeSource = _flipped ? _right : _left;
        bool buildKeyIsRight = !_flipped;

        // Build phase: materialize the build side into a hash table.
        // For single-key joins, use DataValue directly as the key to avoid
        // the overhead of CompositeKey allocation.
        Dictionary<DataValue, List<(int Index, Row Row)>>? singleKeyTable =
            useSingleKey ? new() : null;
        Dictionary<CompositeKey, List<(int Index, Row Row)>>? compositeKeyTable =
            useSingleKey ? null
            : new Dictionary<CompositeKey, List<(int Index, Row Row)>>(CompositeKeyComparer.Instance);

        List<Row> buildRows = new();
        bool hasNullKey = false;

        ExecutionTracer.Write($"HASH BUILD start  buildSide={GetOperatorLabel(buildSource)}  probeSide={GetOperatorLabel(probeSource)}");
        long buildStartTicks = Stopwatch.GetTimestamp();

        await foreach (RowBatch buildBatch in buildSource.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int batchIndex = 0; batchIndex < buildBatch.Count; batchIndex++)
            {
                Row buildRow = buildBatch[batchIndex];
                int buildIndex = buildRows.Count;
                buildRows.Add(buildRow);

            if (useSingleKey)
            {
                DataValue keyValue = evaluator.Evaluate(
                    buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left, buildRow);
                if (keyValue.IsNull)
                {
                    hasNullKey = true;
                    continue;
                }

                if (!singleKeyTable!.TryGetValue(keyValue, out List<(int, Row)>? bucket))
                {
                    bucket = new List<(int, Row)>();
                    singleKeyTable[keyValue] = bucket;
                }

                bucket.Add((buildIndex, buildRow));
            }
            else
            {
                DataValue[] parts = EvaluateKeyParts(evaluator, keyPairs, buildRow, rightSide: buildKeyIsRight);
                if (HasNull(parts))
                {
                    hasNullKey = true;
                    continue;
                }

                CompositeKey compositeKey = new(parts);
                if (!compositeKeyTable!.TryGetValue(compositeKey, out List<(int, Row)>? bucket))
                {
                    bucket = new List<(int, Row)>();
                    compositeKeyTable[compositeKey] = bucket;
                }

                bucket.Add((buildIndex, buildRow));
            }
            }
            buildBatch.Return();
        }

        ExecutionTracer.Write($"HASH BUILD done  rows={buildRows.Count}  keys={singleKeyTable?.Count ?? compositeKeyTable?.Count ?? 0}  elapsed={Stopwatch.GetElapsedTime(buildStartTicks).TotalMilliseconds:F0}ms");

        // NOT IN null semantics: if any build-side key is NULL, the entire result is empty.
        if (_nullSensitiveAntiSemi && hasNullKey)
        {
            yield break;
        }

        // Bloom pruning: if the probe side has a source index with bloom filters
        // and the join key is a simple column reference, push the build-side key
        // values down so entire chunks can be skipped.
        if (!isSemiJoin)
        {
            ApplyBloomPruning(keyPairs, singleKeyTable, compositeKeyTable, useSingleKey);
        }

        // Determine which physical sides need unmatched-row tracking.
        // Build rows are tracked via BitArray; unmatched probe rows are emitted inline.
        bool leftMustAppear = _joinType is JoinType.Left or JoinType.FullOuter;
        bool rightMustAppear = _joinType is JoinType.Right or JoinType.FullOuter;
        bool needBuildUnmatched = _flipped ? leftMustAppear : rightMustAppear;
        bool needProbeUnmatched = _flipped ? rightMustAppear : leftMustAppear;

        // Parallel probe dispatch: when build-side tracking is not needed (no BitArray
        // contention) and the probe side is large enough, fan out probe rows across
        // P concurrent workers sharing the read-only hash table.
        if (!needBuildUnmatched && context.DegreeOfParallelism > 1)
        {
            long? estimatedProbeRows = GetEstimatedRowCount(probeSource);
            if (estimatedProbeRows is null or >= 100_000)
            {
                ExecutionTracer.Write($"HASH PROBE parallel  workers={context.DegreeOfParallelism}  estimatedProbe={estimatedProbeRows}");
                await foreach (RowBatch batch in ExecuteParallelProbeAsync(
                    context, extraction, probeSource, singleKeyTable, compositeKeyTable,
                    buildRows, useSingleKey, isSemiJoin, needProbeUnmatched).ConfigureAwait(false))
                {
                    yield return batch;
                }

                yield break;
            }
        }

        BitArray? buildMatched = needBuildUnmatched ? new BitArray(buildRows.Count) : null;

        // Probe phase: stream the probe side.
        // Schema is built lazily from the first combined pair so that zero-match
        // joins never attempt to build it.
        CombinedRowSchema? schema = null;
        Row? cachedNullBuild = null;

        // Rent a reusable scratch buffer for composite-key probes so that every
        // probe row reuses the same DataValue[] instead of allocating a fresh one.
        // The AlternateLookup probes the dictionary directly from the span, avoiding
        // the per-row CompositeKey heap allocation.
        int keyCount = keyPairs.Count;
        DataValue[]? probeKeyScratch = (!useSingleKey)
            ? ArrayPool<DataValue>.Shared.Rent(keyCount)
            : null;
        Dictionary<CompositeKey, List<(int Index, Row Row)>>.AlternateLookup<ReadOnlySpan<DataValue>>
            compositeKeyLookup = default;
        if (compositeKeyTable is not null)
        {
            compositeKeyLookup = compositeKeyTable.GetAlternateLookup<ReadOnlySpan<DataValue>>();
        }

        // Pool for combined-row DataValue[] buffers. Buffers are returned by the
        // downstream consumer (e.g. GroupByOperator) after it has extracted all
        // needed values, not by this operator.
        LocalBufferPool bufferPool = context.LocalBufferPool;
        Row? residualCheckRow = null;
        DataValue[]? residualCheckBuffer = null;
        RowBatch? outputBatch = null;

        try
        {
        await foreach (RowBatch probeBatch in probeSource.ExecuteAsync(context).ConfigureAwait(false))
        {
        for (int probeIndex = 0; probeIndex < probeBatch.Count; probeIndex++)
        {
            Row probeRow = probeBatch[probeIndex];
            // For null-sensitive anti-semi (NOT IN), NULL probe keys are excluded.
            if (_nullSensitiveAntiSemi)
            {
                bool probeKeyIsNull;
                if (useSingleKey)
                {
                    probeKeyIsNull = evaluator.Evaluate(
                        buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow).IsNull;
                }
                else
                {
                    EvaluateKeyPartsInto(evaluator, keyPairs, probeRow, rightSide: !buildKeyIsRight, probeKeyScratch!);
                    probeKeyIsNull = HasNull(probeKeyScratch.AsSpan(0, keyCount));
                }

                if (probeKeyIsNull)
                {
                    continue;
                }
            }

            bool hasMatch = false;
            List<(int Index, Row Row)>? matches = null;

            if (useSingleKey)
            {
                DataValue probeKeyValue = evaluator.Evaluate(
                    buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow);
                if (!probeKeyValue.IsNull)
                {
                    singleKeyTable!.TryGetValue(probeKeyValue, out matches);
                }
            }
            else
            {
                // Reuse the scratch buffer and probe via AlternateLookup — zero allocation per row.
                EvaluateKeyPartsInto(evaluator, keyPairs, probeRow, rightSide: !buildKeyIsRight, probeKeyScratch!);
                if (!HasNull(probeKeyScratch.AsSpan(0, keyCount)))
                {
                    compositeKeyLookup.TryGetValue(probeKeyScratch.AsSpan(0, keyCount), out matches);
                }
            }

            if (matches is not null)
            {
                foreach ((int buildIndex, Row buildRow) in matches)
                {
                    // Combine in logical (left, right) order regardless of physical assignment.
                    Row leftRow = _flipped ? buildRow : probeRow;
                    Row rightRow = _flipped ? probeRow : buildRow;

                    // Apply residual filter for non-equi conjuncts.
                    if (extraction.Residual is not null)
                    {
                        schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                        if (residualCheckRow is null)
                        {
                            (residualCheckRow, residualCheckBuffer) = schema.CreateReusableRow();
                        }

                        schema.CombineInto(leftRow, rightRow, residualCheckBuffer!);
                        if (!evaluator.EvaluateAsBoolean(extraction.Residual, residualCheckRow.Value))
                        {
                            continue;
                        }
                    }

                    hasMatch = true;

                    if (isSemiJoin)
                    {
                        // Semi-join: emit left row on first match, skip the rest.
                        break;
                    }

                    if (buildMatched is not null)
                    {
                        buildMatched[buildIndex] = true;
                    }

                    if (extraction.Residual is null)
                    {
                        schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                    }

                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema!.CombinePooled(leftRow, rightRow, bufferPool));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
            }

            if (isSemiJoin)
            {
                // LeftSemi: emit only when matched. LeftAntiSemi: emit only when not matched.
                if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                    (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                {
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(probeRow);
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
            }
            else if (!hasMatch && needProbeUnmatched)
            {
                if (buildRows.Count > 0)
                {
                    cachedNullBuild ??= CreateNullRow(buildRows[0]);
                    // Null-pad the build side. Column order is always (left, right).
                    Row leftRow = _flipped ? cachedNullBuild.Value : probeRow;
                    Row rightRow = _flipped ? probeRow : cachedNullBuild.Value;
                    schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.CombinePooled(leftRow, rightRow, bufferPool));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
                else
                {
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(probeRow);
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
            }
        }
        probeBatch.Return();
        }
        }
        finally
        {
            if (probeKeyScratch is not null)
            {
                ArrayPool<DataValue>.Shared.Return(probeKeyScratch);
            }
        }

        // Emit unmatched build rows when the build side must fully appear in the output.
        if (buildMatched is not null)
        {
            Row? nullProbe = null;
            CombinedRowSchema? buildUnmatchedSchema = null;

            for (int index = 0; index < buildRows.Count; index++)
            {
                if (!buildMatched[index])
                {
                    nullProbe ??= await GetFirstRowForNullPadAsync(probeSource, context).ConfigureAwait(false);

                    if (nullProbe is not null)
                    {
                        Row nullProbeRow = CreateNullRow(nullProbe.Value);
                        // Column order is always (left, right).
                        Row leftRow = _flipped ? buildRows[index] : nullProbeRow;
                        Row rightRow = _flipped ? nullProbeRow : buildRows[index];
                        buildUnmatchedSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(buildUnmatchedSchema.CombinePooled(leftRow, rightRow, bufferPool));
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }
                    else
                    {
                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(buildRows[index]);
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }
                }
            }
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }

    /// <summary>
    /// Parallel probe phase for the in-memory hash join. Fans out probe rows
    /// from <paramref name="probeSource"/> across multiple concurrent workers,
    /// each probing the shared read-only hash table and writing matched rows
    /// to a bounded output channel. The caller yields from the output channel.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteParallelProbeAsync(
        ExecutionContext context,
        JoinKeyExtractionResult extraction,
        IQueryOperator probeSource,
        Dictionary<DataValue, List<(int Index, Row Row)>>? singleKeyTable,
        Dictionary<CompositeKey, List<(int Index, Row Row)>>? compositeKeyTable,
        List<Row> buildRows,
        bool useSingleKey,
        bool isSemiJoin,
        bool needProbeUnmatched)
    {
        bool buildKeyIsRight = !_flipped;
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = extraction.KeyPairs;
        CancellationToken cancellationToken = context.CancellationToken;

        // Pre-create the null build row for LEFT join unmatched probes.
        Row? nullBuildRow = needProbeUnmatched && buildRows.Count > 0
            ? CreateNullRow(buildRows[0])
            : null;

        // Acquire worker slots from the optional global budget.
        int desiredWorkers = context.DegreeOfParallelism;
        int acquiredFromBudget = 0;

        if (context.ParallelismBudget is ParallelismBudget budget)
        {
            acquiredFromBudget = budget.TryAcquire(desiredWorkers);
            desiredWorkers = Math.Max(1, acquiredFromBudget);
        }

        try
        {
            int workerCount = desiredWorkers;
            int channelCapacity = workerCount * 64;

            Channel<Row> probeInput = Channel.CreateBounded<Row>(
                new BoundedChannelOptions(channelCapacity)
                {
                    SingleWriter = true,
                    SingleReader = false,
                });

            Channel<Row> output = Channel.CreateBounded<Row>(
                new BoundedChannelOptions(channelCapacity)
                {
                    SingleWriter = false,
                    SingleReader = true,
                });

            // Feeder: reads probe source into the shared input channel.
            Task feederTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (RowBatch batch in probeSource.ExecuteAsync(context).ConfigureAwait(false))
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            await probeInput.Writer.WriteAsync(batch[i], cancellationToken).ConfigureAwait(false);
                        }
                        batch.Return();
                    }
                }
                finally
                {
                    probeInput.Writer.Complete();
                }
            }, cancellationToken);

            // Probe workers: each reads from the shared input channel, evaluates the
            // join key, probes the hash table, and writes matched rows to the output.
            // Each worker rents its own scratch buffer for composite-key probes.
            int keyCount = keyPairs.Count;
            Dictionary<CompositeKey, List<(int Index, Row Row)>>.AlternateLookup<ReadOnlySpan<DataValue>>
                compositeKeyLookup = default;
            if (compositeKeyTable is not null)
            {
                compositeKeyLookup = compositeKeyTable.GetAlternateLookup<ReadOnlySpan<DataValue>>();
            }

            // Shared buffer pool from the execution context. Workers rent from
            // the pool when producing combined rows; the downstream consumer
            // (e.g. GroupByOperator) returns buffers after processing each row.
            LocalBufferPool bufferPool = context.LocalBufferPool;

            Task[] workers = new Task[workerCount];
            for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                workers[workerIndex] = Task.Run(async () =>
                {
                    ExpressionEvaluator workerEvaluator = new(
                        context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);
                    CombinedRowSchema? workerSchema = null;
                    Row? workerResidualRow = null;
                    DataValue[]? workerResidualBuffer = null;

                    DataValue[]? workerKeyScratch = (!useSingleKey)
                        ? ArrayPool<DataValue>.Shared.Rent(keyCount)
                        : null;

                    try
                    {
                    await foreach (Row probeRow in probeInput.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // NOT IN null semantics: NULL probe keys are excluded.
                        if (_nullSensitiveAntiSemi)
                        {
                            bool probeKeyIsNull;
                            if (useSingleKey)
                            {
                                probeKeyIsNull = workerEvaluator.Evaluate(
                                    buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow).IsNull;
                            }
                            else
                            {
                                EvaluateKeyPartsInto(
                                    workerEvaluator, keyPairs, probeRow, rightSide: !buildKeyIsRight, workerKeyScratch!);
                                probeKeyIsNull = HasNull(workerKeyScratch.AsSpan(0, keyCount));
                            }

                            if (probeKeyIsNull)
                            {
                                continue;
                            }
                        }

                        bool hasMatch = false;
                        List<(int Index, Row Row)>? matches = null;

                        if (useSingleKey)
                        {
                            DataValue probeKeyValue = workerEvaluator.Evaluate(
                                buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow);
                            if (!probeKeyValue.IsNull)
                            {
                                singleKeyTable!.TryGetValue(probeKeyValue, out matches);
                            }
                        }
                        else
                        {
                            EvaluateKeyPartsInto(
                                workerEvaluator, keyPairs, probeRow, rightSide: !buildKeyIsRight, workerKeyScratch!);
                            if (!HasNull(workerKeyScratch.AsSpan(0, keyCount)))
                            {
                                compositeKeyLookup.TryGetValue(workerKeyScratch.AsSpan(0, keyCount), out matches);
                            }
                        }

                        if (matches is not null)
                        {
                            foreach ((int _, Row buildRow) in matches)
                            {
                                Row leftRow = _flipped ? buildRow : probeRow;
                                Row rightRow = _flipped ? probeRow : buildRow;

                                if (extraction.Residual is not null)
                                {
                                    workerSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                                    if (workerResidualRow is null)
                                    {
                                        (workerResidualRow, workerResidualBuffer) = workerSchema.CreateReusableRow();
                                    }

                                    workerSchema.CombineInto(leftRow, rightRow, workerResidualBuffer!);
                                    if (!workerEvaluator.EvaluateAsBoolean(extraction.Residual, workerResidualRow.Value))
                                    {
                                        continue;
                                    }
                                }

                                hasMatch = true;

                                if (isSemiJoin)
                                {
                                    break;
                                }

                                if (extraction.Residual is null)
                                {
                                    workerSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                                }

                                await output.Writer.WriteAsync(
                                    workerSchema!.CombinePooled(leftRow, rightRow, bufferPool), cancellationToken).ConfigureAwait(false);
                            }
                        }

                        if (isSemiJoin)
                        {
                            if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                                (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                            {
                                await output.Writer.WriteAsync(probeRow, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else if (!hasMatch && needProbeUnmatched)
                        {
                            if (nullBuildRow is not null)
                            {
                                Row leftRow = _flipped ? nullBuildRow.Value : probeRow;
                                Row rightRow = _flipped ? probeRow : nullBuildRow.Value;
                                workerSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                                await output.Writer.WriteAsync(
                                    workerSchema.CombinePooled(leftRow, rightRow, bufferPool), cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await output.Writer.WriteAsync(probeRow, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    }
                    finally
                    {
                        if (workerKeyScratch is not null)
                        {
                            ArrayPool<DataValue>.Shared.Return(workerKeyScratch);
                        }
                    }
                }, cancellationToken);
            }

            // Complete the output channel when all workers and the feeder finish.
            _ = CompleteOutputWhenDoneAsync(feederTask, workers, output.Writer);

            RowBatch? outputBatch = null;
            await foreach (Row row in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                outputBatch.Add(row);
                if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
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
        }
    }

    /// <summary>
    /// Completes the output channel writer when all probe workers and the feeder
    /// task have finished. Propagates exceptions to the channel reader.
    /// </summary>
    private static async Task CompleteOutputWhenDoneAsync(
        Task feederTask, Task[] workers, ChannelWriter<Row> writer)
    {
        try
        {
            await feederTask.ConfigureAwait(false);
            await Task.WhenAll(workers).ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception exception)
        {
            writer.Complete(exception);
        }
    }

    /// <summary>
    /// Walks through transparent operator wrappers (alias, filter, project) to find the
    /// underlying <see cref="ScanOperator"/> and returns its estimated row count.
    /// Returns <see langword="null"/> when the tree does not bottom out at a scan.
    /// </summary>
    private static long? GetEstimatedRowCount(IQueryOperator operatorNode)
    {
        IQueryOperator current = operatorNode;
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
                default:
                    return null;
            }
        }
    }

    private async IAsyncEnumerable<RowBatch> ExecuteNestedLoopJoinAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);
        bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;

        // Physical side assignment mirrors the hash join path.
        IQueryOperator buildSource = _flipped ? _left : _right;
        IQueryOperator probeSource = _flipped ? _right : _left;

        // Materialize build side.
        List<Row> buildRows = new();
        await foreach (RowBatch buildBatch in buildSource.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int batchIndex = 0; batchIndex < buildBatch.Count; batchIndex++)
            {
                buildRows.Add(buildBatch[batchIndex]);
            }
            buildBatch.Return();
        }

        bool leftMustAppear = _joinType is JoinType.Left or JoinType.FullOuter;
        bool rightMustAppear = _joinType is JoinType.Right or JoinType.FullOuter;
        bool needBuildUnmatched = _flipped ? leftMustAppear : rightMustAppear;
        bool needProbeUnmatched = _flipped ? rightMustAppear : leftMustAppear;
        BitArray? buildMatched = needBuildUnmatched
            ? new BitArray(buildRows.Count)
            : null;

        CombinedRowSchema? schema = null;
        Row? cachedNullBuild = null;
        LocalBufferPool bufferPool = context.LocalBufferPool;

        // Reusable buffer for ON condition evaluation — avoids renting a pooled
        // row for each candidate pair only to discard it when the filter fails.
        Row? reusableFilterRow = null;
        DataValue[]? reusableFilterBuffer = null;
        RowBatch? outputBatch = null;

        await foreach (RowBatch probeBatch in probeSource.ExecuteAsync(context).ConfigureAwait(false))
        {
        for (int probeIndex = 0; probeIndex < probeBatch.Count; probeIndex++)
        {
            Row probeRow = probeBatch[probeIndex];
            bool hasMatch = false;

            for (int index = 0; index < buildRows.Count; index++)
            {
                // Combine in logical (left, right) order.
                Row leftRow = _flipped ? buildRows[index] : probeRow;
                Row rightRow = _flipped ? probeRow : buildRows[index];

                schema ??= CombinedRowSchema.Build(leftRow, rightRow);

                // When there is an ON condition, evaluate it using a reusable
                // buffer so that non-matching combinations incur zero allocation.
                if (_onCondition is not null)
                {
                    if (reusableFilterBuffer is null)
                    {
                        (reusableFilterRow, reusableFilterBuffer) = schema.CreateReusableRow();
                    }

                    schema.CombineInto(leftRow, rightRow, reusableFilterBuffer);

                    if (!evaluator.EvaluateAsBoolean(_onCondition, reusableFilterRow.GetValueOrDefault()))
                    {
                        continue;
                    }
                }

                hasMatch = true;

                if (isSemiJoin)
                {
                    break;
                }

                if (buildMatched is not null)
                {
                    buildMatched[index] = true;
                }

                outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                outputBatch.Add(schema.CombinePooled(leftRow, rightRow, bufferPool));
                if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
            }

            if (isSemiJoin)
            {
                if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                    (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                {
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(probeRow);
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
            }
            else if (!hasMatch && needProbeUnmatched)
            {
                if (buildRows.Count > 0)
                {
                    cachedNullBuild ??= CreateNullRow(buildRows[0]);
                    Row leftRow = _flipped ? cachedNullBuild.Value : probeRow;
                    Row rightRow = _flipped ? probeRow : cachedNullBuild.Value;
                    schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.CombinePooled(leftRow, rightRow, bufferPool));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
                else
                {
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(probeRow);
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
            }
        }
        probeBatch.Return();
        }

        // Emit unmatched build rows.
        if (buildMatched is not null)
        {
            Row? nullProbe = null;
            CombinedRowSchema? buildUnmatchedSchema = null;

            for (int index = 0; index < buildRows.Count; index++)
            {
                if (!buildMatched[index])
                {
                    nullProbe ??= await GetFirstRowForNullPadAsync(probeSource, context).ConfigureAwait(false);

                    if (nullProbe is not null)
                    {
                        Row nullProbeRow = CreateNullRow(nullProbe.Value);
                        Row leftRow = _flipped ? buildRows[index] : nullProbeRow;
                        Row rightRow = _flipped ? nullProbeRow : buildRows[index];
                        buildUnmatchedSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(buildUnmatchedSchema.CombinePooled(leftRow, rightRow, bufferPool));
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }
                    else
                    {
                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(buildRows[index]);
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }
                }
            }
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }

    private async IAsyncEnumerable<RowBatch> ExecuteCrossJoinAsync(ExecutionContext context)
    {
        // Materialize right side.
        List<Row> rightRows = new();
        await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int batchIndex = 0; batchIndex < rightBatch.Count; batchIndex++)
            {
                rightRows.Add(rightBatch[batchIndex]);
            }
            rightBatch.Return();
        }

        CombinedRowSchema? schema = null;
        LocalBufferPool bufferPool = context.LocalBufferPool;
        RowBatch? outputBatch = null;

        await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int leftIndex = 0; leftIndex < leftBatch.Count; leftIndex++)
            {
                Row leftRow = leftBatch[leftIndex];
                foreach (Row rightRow in rightRows)
                {
                    schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.CombinePooled(leftRow, rightRow, bufferPool));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
            }
            leftBatch.Return();
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }

    private static async Task<Row?> GetFirstRowForNullPadAsync(IQueryOperator source, ExecutionContext context)
    {
        await foreach (RowBatch batch in source.ExecuteAsync(context).ConfigureAwait(false))
        {
            if (batch.Count > 0)
            {
                return batch[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates the key expressions for a single row, selecting either the left
    /// or right expression from each key pair.
    /// </summary>
    private static DataValue[] EvaluateKeyParts(
        ExpressionEvaluator evaluator,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Row row,
        bool rightSide)
    {
        DataValue[] parts = new DataValue[keyPairs.Count];
        EvaluateKeyPartsInto(evaluator, keyPairs, row, rightSide, parts);
        return parts;
    }

    /// <summary>
    /// Evaluates the key expressions for a single row into a caller-provided buffer,
    /// avoiding the per-row <see cref="DataValue"/> array heap allocation that the
    /// array-returning <see cref="EvaluateKeyParts"/> overload would otherwise incur.
    /// </summary>
    private static void EvaluateKeyPartsInto(
        ExpressionEvaluator evaluator,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Row row,
        bool rightSide,
        DataValue[] destination)
    {
        for (int index = 0; index < keyPairs.Count; index++)
        {
            Expression expression = rightSide ? keyPairs[index].Right : keyPairs[index].Left;
            destination[index] = evaluator.Evaluate(expression, row);
        }
    }

    /// <summary>
    /// Returns true if any element in the array is null. NULL keys never match in SQL semantics.
    /// </summary>
    private static bool HasNull(DataValue[] parts)
    {
        for (int index = 0; index < parts.Length; index++)
        {
            if (parts[index].IsNull)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if any element in the span is null. NULL keys never match in SQL semantics.
    /// </summary>
    private static bool HasNull(ReadOnlySpan<DataValue> parts)
    {
        for (int index = 0; index < parts.Length; index++)
        {
            if (parts[index].IsNull)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Combines two rows into one, using all columns from both sides.
    /// </summary>
    internal static Row CombineRows(Row left, Row right)
    {
        string[] names = new string[left.FieldCount + right.FieldCount];
        DataValue[] values = new DataValue[left.FieldCount + right.FieldCount];

        for (int index = 0; index < left.FieldCount; index++)
        {
            names[index] = left.ColumnNames[index];
            values[index] = left[index];
        }

        for (int index = 0; index < right.FieldCount; index++)
        {
            names[left.FieldCount + index] = right.ColumnNames[index];
            values[left.FieldCount + index] = right[index];
        }

        return new Row(names, values);
    }

    /// <summary>
    /// Creates a row with the same column names as the source but all null values.
    /// </summary>
    internal static Row CreateNullRow(Row template)
    {
        string[] names = new string[template.FieldCount];
        DataValue[] values = new DataValue[template.FieldCount];

        for (int index = 0; index < template.FieldCount; index++)
        {
            names[index] = template.ColumnNames[index];
            values[index] = DataValue.Null(template[index].Kind);
        }

        return new Row(names, values);
    }

    /// <summary>
    /// Pre-computed schema for combined rows in a join. Holds the shared column name
    /// array and name-index dictionary so that each combined row allocates only a
    /// <see cref="DataValue"/> array instead of rebuilding the full schema.
    /// </summary>
    internal sealed class CombinedRowSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly int _leftFieldCount;

        private CombinedRowSchema(string[] names, Dictionary<string, int> nameIndex, int leftFieldCount)
        {
            _names = names;
            _nameIndex = nameIndex;
            _leftFieldCount = leftFieldCount;
        }

        /// <summary>
        /// Builds a schema from the first left and right rows encountered in a join.
        /// </summary>
        internal static CombinedRowSchema Build(Row left, Row right)
        {
            int totalFields = left.FieldCount + right.FieldCount;
            string[] names = new string[totalFields];

            for (int index = 0; index < left.FieldCount; index++)
            {
                names[index] = left.ColumnNames[index];
            }

            for (int index = 0; index < right.FieldCount; index++)
            {
                names[left.FieldCount + index] = right.ColumnNames[index];
            }

            Dictionary<string, int> nameIndex = new(totalFields, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < totalFields; index++)
            {
                nameIndex[names[index]] = index;
            }

            // Add unqualified shortcuts for aliased columns so that expressions
            // like image_to_tensor_chw(image) can resolve unqualified names after
            // a JOIN.  Skip ambiguous names that appear on both sides.
            HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> unqualified = new(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < totalFields; index++)
            {
                int dotPosition = names[index].LastIndexOf('.');
                if (dotPosition < 0)
                {
                    continue;
                }

                string shortName = names[index][(dotPosition + 1)..];
                if (ambiguous.Contains(shortName))
                {
                    continue;
                }

                if (!unqualified.TryAdd(shortName, index))
                {
                    // Same unqualified name on both sides — remove and mark ambiguous.
                    unqualified.Remove(shortName);
                    ambiguous.Add(shortName);
                }
            }

            foreach (KeyValuePair<string, int> entry in unqualified)
            {
                nameIndex.TryAdd(entry.Key, entry.Value);
            }

            return new CombinedRowSchema(names, nameIndex, left.FieldCount);
        }

        /// <summary>
        /// Combines two rows using the shared schema. Only a <see cref="DataValue"/> array
        /// is allocated per call.
        /// </summary>
        internal Row Combine(Row left, Row right)
        {
            DataValue[] values = new DataValue[_names.Length];

            for (int index = 0; index < _leftFieldCount; index++)
            {
                values[index] = left[index];
            }

            for (int index = 0; index < _names.Length - _leftFieldCount; index++)
            {
                values[_leftFieldCount + index] = right[index];
            }

            return new Row(_names, values, _nameIndex);
        }

        /// <summary>
        /// Fills the target array with combined values from left and right rows.
        /// No heap allocation occurs; the caller provides the buffer.
        /// </summary>
        internal void CombineInto(Row left, Row right, DataValue[] target)
        {
            for (int index = 0; index < _leftFieldCount; index++)
            {
                target[index] = left[index];
            }

            for (int index = 0; index < _names.Length - _leftFieldCount; index++)
            {
                target[_leftFieldCount + index] = right[index];
            }
        }

        /// <summary>
        /// Combines two rows, renting the backing <see cref="DataValue"/> array from
        /// <paramref name="bufferPool"/> to avoid per-row heap allocation. The downstream
        /// consumer returns the array via <see cref="LocalBufferPool.Return"/> when it
        /// is no longer needed.
        /// </summary>
        internal Row CombinePooled(Row left, Row right, LocalBufferPool bufferPool)
        {
            DataValue[] values = bufferPool.Rent(_names.Length);

            for (int index = 0; index < _leftFieldCount; index++)
            {
                values[index] = left[index];
            }

            for (int index = 0; index < _names.Length - _leftFieldCount; index++)
            {
                values[_leftFieldCount + index] = right[index];
            }

            return new Row(_names, values, _nameIndex);
        }

        /// <summary>
        /// Creates a reusable row-plus-buffer pair for scenarios where the same
        /// row is filled repeatedly (e.g. residual filter evaluation). The caller
        /// keeps the buffer reference and calls <see cref="CombineInto"/> to
        /// overwrite its contents before each use.
        /// </summary>
        internal (Row Row, DataValue[] Buffer) CreateReusableRow()
        {
            DataValue[] buffer = new DataValue[_names.Length];
            Row row = new(_names, buffer, _nameIndex);
            return (row, buffer);
        }
    }

    /// <summary>
    /// Pushes build-side key values to all reachable probe-side <see cref="ScanOperator"/>
    /// instances for bloom-filter-based and sorted-index-based chunk pruning. Traverses
    /// through intermediate joins, aliases, filters, and projections so that multi-table
    /// join trees can propagate keys to buried scans.
    /// </summary>
    private void ApplyBloomPruning(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Dictionary<DataValue, List<(int Index, Row Row)>>? singleKeyTable,
        Dictionary<CompositeKey, List<(int Index, Row Row)>>? compositeKeyTable,
        bool useSingleKey)
    {
        IQueryOperator probeOperator = _flipped ? _right : _left;
        List<ScanOperator> probeScans = new();
        CollectScanOperators(probeOperator, probeScans);

        if (probeScans.Count == 0)
        {
            return;
        }

        for (int keyIndex = 0; keyIndex < keyPairs.Count; keyIndex++)
        {
            Expression probeKeyExpression = _flipped ? keyPairs[keyIndex].Right : keyPairs[keyIndex].Left;
            if (probeKeyExpression is not ColumnReference columnReference)
            {
                continue;
            }

            string columnName = columnReference.ColumnName;
            HashSet<DataValue>? distinctKeys = null;

            foreach (ScanOperator probeScan in probeScans)
            {
                bool hasBloom = probeScan.SourceIndex?.BloomFilters is BloomFilterSet bloomFilters
                    && bloomFilters.HasColumn(columnName);
                bool hasSortedIndex = probeScan.SourceIndex is not null
                    && probeScan.SourceIndex.TryGetColumnIndex(columnName, out _);

                if (!hasBloom && !hasSortedIndex)
                {
                    continue;
                }

                // Lazily collect distinct keys on first matching scan.
                if (distinctKeys is null)
                {
                    distinctKeys = new HashSet<DataValue>();

                    if (useSingleKey && singleKeyTable is not null)
                    {
                        foreach (DataValue key in singleKeyTable.Keys)
                        {
                            distinctKeys.Add(key);
                        }
                    }
                    else if (compositeKeyTable is not null)
                    {
                        foreach (CompositeKey compositeKey in compositeKeyTable.Keys)
                        {
                            DataValue partValue = compositeKey[keyIndex];
                            if (!partValue.IsNull)
                            {
                                distinctKeys.Add(partValue);
                            }
                        }
                    }

                    if (distinctKeys.Count == 0)
                    {
                        break;
                    }
                }

                if (hasBloom)
                {
                    probeScan.AddBloomPruningKeys(columnName, distinctKeys);
                }

                if (hasSortedIndex)
                {
                    probeScan.AddSortedIndexPruningKeys(columnName, distinctKeys);
                }
            }
        }
    }

    /// <summary>
    /// Walks the build-side operator chain to find the <see cref="AliasOperator"/> that
    /// qualifies column names with the table alias. Returns the alias string, or
    /// <c>null</c> if no alias wrapper exists. Traverses through
    /// <see cref="FilterOperator"/> which may be inserted by predicate pushdown.
    /// </summary>
    private static string? FindBuildAlias(IQueryOperator operatorNode)
    {
        IQueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case AliasOperator alias:
                    return alias.Alias;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Recursively collects all reachable <see cref="ScanOperator"/> instances
    /// in the operator tree, traversing through <see cref="AliasOperator"/>,
    /// <see cref="FilterOperator"/>, <see cref="ProjectOperator"/>, and
    /// <see cref="JoinOperator"/> (both sides). Stops at operators that
    /// break column identity (e.g. aggregation, DISTINCT).
    /// </summary>
    internal static void CollectScanOperators(IQueryOperator operatorNode, List<ScanOperator> results)
    {
        IQueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    results.Add(scan);
                    return;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                case JoinOperator join:
                    CollectScanOperators(join._left, results);
                    CollectScanOperators(join._right, results);
                    return;
                case MergeJoinOperator merge:
                    CollectScanOperators(merge.Left, results);
                    CollectScanOperators(merge.Right, results);
                    return;
                default:
                    return;
            }
        }
    }
}
