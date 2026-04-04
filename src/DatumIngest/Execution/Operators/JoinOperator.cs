using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Threading.Channels;
using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Execution.Operators.Joins;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
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
public sealed class JoinOperator : QueryOperator
{
    private readonly QueryOperator _left;
    private readonly QueryOperator _right;
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
        QueryOperator left,
        QueryOperator right,
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
    public QueryOperator Left => _left;

    /// <summary>The right (build) side operator.</summary>
    public QueryOperator Right => _right;

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
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
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

            DatumActivity.Operators.Trace($"JOIN execute  INLJ={indexNlj is not null}  budget={context.MemoryBudgetBytes}  rowLimit={context.RowLimit}  flipped={_flipped}");

            if (indexNlj is not null)
            {
                DatumActivity.Operators.Trace("JOIN INLJ trial starting");
                await foreach (RowBatch batch in indexNlj.ExecuteAsync(_left, _right, context).ConfigureAwait(false))
                {
                    DatumActivity.Operators.Trace($"JOIN INLJ yielded batch count={batch.Count}");
                    yield return batch;
                }

                DatumActivity.Operators.Trace($"JOIN INLJ done  circuitBreaker={indexNlj.CircuitBreakerTripped}");

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
                ExpressionEvaluator evaluator = new(context);
                QueryOperator buildSide = _flipped ? _left : _right;
                long? estimatedBuildRows = GetEstimatedRowCount(buildSide);
                DatumActivity.Operators.Trace($"JOIN GraceHash starting  build={GetOperatorLabel(buildSide)}  estimatedBuild={estimatedBuildRows}");

                // Bloom pruning is unsafe for LeftAntiSemi (would skip probe-side left rows
                // that must be emitted as unmatched). Allowed for inner/outer/LeftSemi.
                bool isSemiJoin = _joinType is JoinType.LeftSemi or JoinType.LeftAntiSemi;
                IReadOnlyList<int>? bloomKeyIndices = null;
                Action<IReadOnlyDictionary<int, HashSet<DataValue>>>? onBloomKeysReady = null;
                if (!isSemiJoin)
                {
                    List<BloomPruningPlanEntry> plan = ComputeBloomPruningPlan(extraction.KeyPairs);
                    if (plan.Count > 0)
                    {
                        int[] indices = new int[plan.Count];
                        for (int i = 0; i < plan.Count; i++)
                        {
                            indices[i] = plan[i].KeyIndex;
                        }
                        bloomKeyIndices = indices;
                        onBloomKeysReady = keys => PushBloomKeysToScans(plan, keys);
                    }
                }

                GraceHashJoinExecutor graceExecutor = new(
                    _joinType, extraction, memoryBudget, evaluator, _nullSensitiveAntiSemi, _flipped,
                    label: GetOperatorLabel(buildSide), estimatedBuildRows: estimatedBuildRows,
                    bloomKeyIndices: bloomKeyIndices, onBloomKeysReady: onBloomKeysReady);

                await foreach (RowBatch batch in graceExecutor.ExecuteAsync(_left, _right, context).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
            else
            {
                DatumActivity.Operators.Trace("JOIN InMemoryHash starting");
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
    private static string GetOperatorLabel(QueryOperator op)
    {
        QueryOperator current = op;
        while (true)
        {
            if (current is ScanOperator scan)
                return scan.TableProvider.QualifiedName.ToString();
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

        if (!buildScan.TableProvider.TryGetColumnIndex(indexColumnName, out IColumnIndex? columnIndex))
        {
            // Try unqualified name if qualified failed.
            if (buildColumnRef.QualifiedName is not null
                && !buildScan.TableProvider.TryGetColumnIndex(buildColumnRef.ColumnName, out columnIndex))
            {
                return null;
            }

            if (columnIndex is null)
            {
                return null;
            }
        }

        // Verify the provider supports seeking.
        if (!buildScan.TableProvider.Seekable)
        {
            return null;
        }
        
        // Extract the build-side alias so the executor can re-qualify rows
        // fetched directly from the seekable provider (which bypasses AliasOperator).
        string? buildAlias = FindBuildAlias(_right);

        ExpressionEvaluator evaluator = new(context);

        return new IndexNestedLoopJoinExecutor(
            buildScan.TableProvider,
            _joinType,
            extraction,
            columnIndex,
            buildScan.SourceIndex.Chunks,
            buildAlias,
            evaluator);
    }

    private async IAsyncEnumerable<RowBatch> ExecuteHashJoinAsync(
        ExecutionContext context, JoinKeyExtractionResult extraction)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = new(context);
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = extraction.KeyPairs;
        bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;

        // Physical side assignment. Normally right=build, left=probe.
        // When flipped, left=build (smaller side materialized), right=probe (larger, streamed).
        QueryOperator buildSource = _flipped ? _left : _right;
        QueryOperator probeSource = _flipped ? _right : _left;
        bool buildKeyIsRight = !_flipped;

        // Build phase: materialize the build side into a hash table.
        // For single-key joins, use DataValue directly as the key to avoid
        // the overhead of CompositeKey allocation.
        IJoinHashTable hashTable = keyPairs.Count == 1
            ? new SingleJoinHashTable(
                buildKey: buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left,
                probeKey: buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right)
            : new CompositeJoinHashTable(keyPairs, buildKeyIsRight);

        // Build rows are stabilised into operator-owned DataValue[] rentals so build
        // batches return to the pool immediately. Same-store fast path under one-
        // arena-per-query keeps this a fresh DataValue[] rental with no payload copy.
        BuildSideMaterializer buildRows = new(pool, context.Store);
        bool hasNullKey = false;

        DatumActivity.Operators.Trace($"HASH BUILD start  buildSide={GetOperatorLabel(buildSource)}  probeSide={GetOperatorLabel(probeSource)}");
        long buildStartTicks = Stopwatch.GetTimestamp();

        // Probe-phase scratch / state declared up front so the outer try/finally
        // can release them on early exit. Cached null pad rows hold DataValue[]
        // rentals that must round-trip through pool.ReturnRow at end-of-execute.
        DataValue[]? probeKeyScratch = (hashTable.KeyCount > 1)
            ? ArrayPool<DataValue>.Shared.Rent(hashTable.KeyCount)
            : null;
        JoinOutputWriter writer = new(context, pool);
        NullPadCache cachedNullBuild = new(pool);
        NullPadCache cachedNullProbe = new(pool);

        try
        {
            await buildRows.MaterializeAsync(buildSource, context).ConfigureAwait(false);

            for (int buildIndex = 0; buildIndex < buildRows.Count; buildIndex++)
            {
                bool inserted = await hashTable.TryEvaluateAndInsertAsync(
                    evaluator, buildRows[buildIndex], buildIndex, context.CancellationToken).ConfigureAwait(false);
                if (!inserted)
                {
                    hasNullKey = true;
                }
            }

            DatumActivity.Operators.Trace($"HASH BUILD done  rows={buildRows.Count}  keys={hashTable.Count}  elapsed={Stopwatch.GetElapsedTime(buildStartTicks).TotalMilliseconds:F0}ms");

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
                ApplyBloomPruning(keyPairs, hashTable);
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
                    DatumActivity.Operators.Trace($"HASH PROBE parallel  workers={context.DegreeOfParallelism}  estimatedProbe={estimatedProbeRows}");
                    await foreach (RowBatch batch in ExecuteParallelProbeAsync(
                        context, extraction, probeSource, hashTable,
                        buildRows.Rows, isSemiJoin, needProbeUnmatched).ConfigureAwait(false))
                    {
                        yield return batch;
                    }

                    yield break;
                }
            }

            BitArray? buildMatched = needBuildUnmatched ? new BitArray(buildRows.Count) : null;

            Row? residualCheckRow = null;
            DataValue[]? residualCheckBuffer = null;

            await foreach (RowBatch probeBatch in probeSource.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int probeIndex = 0; probeIndex < probeBatch.Count; probeIndex++)
                    {
                        Row probeRow = probeBatch[probeIndex];

                        JoinHashProbeResult probeResult = await hashTable.ProbeAsync(
                            evaluator, probeRow, probeKeyScratch, context.CancellationToken).ConfigureAwait(false);

                        // For null-sensitive anti-semi (NOT IN), NULL probe keys are excluded.
                        if (_nullSensitiveAntiSemi && probeResult.KeyIsNull)
                        {
                            continue;
                        }

                        bool hasMatch = false;
                        List<(int Index, Row Row)>? matches = probeResult.Matches;

                        if (matches is not null)
                        {
                            foreach ((int buildIndex, Row buildRow) in matches)
                            {
                                Row leftRow = _flipped ? buildRow : probeRow;
                                Row rightRow = _flipped ? probeRow : buildRow;

                                if (extraction.Residual is not null)
                                {
                                    JoinSchema schema = writer.GetCombinedSchema(leftRow, rightRow);
                                    if (residualCheckRow is null)
                                    {
                                        (residualCheckRow, residualCheckBuffer) = schema.CreateReusableRow();
                                    }

                                    schema.CombineInto(leftRow, rightRow, residualCheckBuffer!);
                                    if (!await evaluator.EvaluateAsBooleanAsync(extraction.Residual, residualCheckRow.Value, context.CancellationToken).ConfigureAwait(false))
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
                                    buildMatched[buildIndex] = true;
                                }

                                if (writer.EmitCombined(leftRow, rightRow) is RowBatch ready)
                                    yield return ready;
                            }
                        }

                        if (isSemiJoin)
                        {
                            if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                                (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                            {
                                if (writer.EmitPassThrough(probeRow, probeBatch.Arena) is RowBatch ready)
                                    yield return ready;
                            }
                        }
                        else if (!hasMatch && needProbeUnmatched)
                        {
                            if (buildRows.Count > 0)
                            {
                                Row nullBuild = cachedNullBuild.GetOrCreate(buildRows[0]);
                                Row leftRow = _flipped ? nullBuild : probeRow;
                                Row rightRow = _flipped ? probeRow : nullBuild;
                                if (writer.EmitCombined(leftRow, rightRow) is RowBatch ready)
                                    yield return ready;
                            }
                            else
                            {
                                if (writer.EmitPassThrough(probeRow, probeBatch.Arena) is RowBatch ready)
                                    yield return ready;
                            }
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(probeBatch);
                }
            }

            // Emit unmatched build rows when the build side must fully appear in the output.
            if (buildMatched is not null)
            {
                UnmatchedBuildEmitter unmatchedBuild = new(_flipped, writer, cachedNullProbe);
                await foreach (RowBatch ready in unmatchedBuild.EmitAsync(
                    buildMatched, buildRows.Rows, probeSource, context.Store, context).ConfigureAwait(false))
                {
                    yield return ready;
                }
            }

            if (writer.Flush() is RowBatch trailing) yield return trailing;
        }
        finally
        {
            if (writer.Flush() is RowBatch leftover) context.ReturnRowBatch(leftover);

            if (probeKeyScratch is not null)
            {
                ArrayPool<DataValue>.Shared.Return(probeKeyScratch);
            }

            // Release pool-rented null pad row buffers.
            cachedNullBuild.Return();
            cachedNullProbe.Return();

            // Release the stabilised build-row buffers.
            buildRows.Return();
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
        QueryOperator probeSource,
        IJoinHashTable hashTable,
        IReadOnlyList<Row> buildRows,
        bool isSemiJoin,
        bool needProbeUnmatched)
    {
        Pool pool = context.Pool;
        CancellationToken cancellationToken = context.CancellationToken;

        // Pre-create the null build row for LEFT join unmatched probes. Rented
        // from the pool — released in the outer finally below.
        NullPadCache nullBuildCache = new(pool);
        if (needProbeUnmatched && buildRows.Count > 0)
        {
            nullBuildCache.Initialize(buildRows[0]);
        }

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
                        context.ReturnRowBatch(batch);
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
            Task[] workers = new Task[workerCount];
            for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                workers[workerIndex] = Task.Run(async () =>
                {
                    ExpressionEvaluator workerEvaluator = new(context);
                    JoinSchema? workerSchema = null;
                    Row? workerResidualRow = null;
                    DataValue[]? workerResidualBuffer = null;

                    DataValue[]? workerKeyScratch = (hashTable.KeyCount > 1)
                        ? ArrayPool<DataValue>.Shared.Rent(hashTable.KeyCount)
                        : null;

                    try
                    {
                    await foreach (Row probeRow in probeInput.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        JoinHashProbeResult probeResult = await hashTable.ProbeAsync(
                            workerEvaluator, probeRow, workerKeyScratch, cancellationToken).ConfigureAwait(false);

                        // NOT IN null semantics: NULL probe keys are excluded.
                        if (_nullSensitiveAntiSemi && probeResult.KeyIsNull)
                        {
                            continue;
                        }

                        bool hasMatch = false;
                        List<(int Index, Row Row)>? matches = probeResult.Matches;

                        if (matches is not null)
                        {
                            foreach ((int _, Row buildRow) in matches)
                            {
                                Row leftRow = _flipped ? buildRow : probeRow;
                                Row rightRow = _flipped ? probeRow : buildRow;

                                if (extraction.Residual is not null)
                                {
                                    workerSchema ??= JoinSchema.Build(leftRow, rightRow);
                                    if (workerResidualRow is null)
                                    {
                                        (workerResidualRow, workerResidualBuffer) = workerSchema.CreateReusableRow();
                                    }

                                    workerSchema.CombineInto(leftRow, rightRow, workerResidualBuffer!);
                                    if (!await workerEvaluator.EvaluateAsBooleanAsync(extraction.Residual, workerResidualRow.Value, cancellationToken).ConfigureAwait(false))
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
                                    workerSchema ??= JoinSchema.Build(leftRow, rightRow);
                                }

                                await output.Writer.WriteAsync(
                                    workerSchema!.CombinePooled(leftRow, rightRow, pool), cancellationToken).ConfigureAwait(false);
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
                            if (nullBuildCache.HasValue)
                            {
                                Row nullBuild = nullBuildCache.Value;
                                Row leftRow = _flipped ? nullBuild : probeRow;
                                Row rightRow = _flipped ? probeRow : nullBuild;
                                workerSchema ??= JoinSchema.Build(leftRow, rightRow);
                                await output.Writer.WriteAsync(
                                    workerSchema.CombinePooled(leftRow, rightRow, pool), cancellationToken).ConfigureAwait(false);
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
            try
            {
                await foreach (Row row in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Lazy rent on first row so we can pick up the row's own
                    // ColumnLookup — workers may emit either combined-schema rows
                    // or pass-through probe rows depending on the join type.
                    outputBatch ??= context.RentRowBatch(row.ColumnLookup);
                    outputBatch.Add(row.RawValues);
                    if (outputBatch.IsFull)
                    {
                        RowBatch toYield = outputBatch;
                        outputBatch = null;
                        yield return toYield;
                    }
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
                if (outputBatch is not null)
                {
                    context.ReturnRowBatch(outputBatch);
                }
            }
        }
        finally
        {
            if (acquiredFromBudget > 0)
            {
                context.ParallelismBudget!.Release(acquiredFromBudget);
            }

            nullBuildCache.Return();
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
    private static long? GetEstimatedRowCount(QueryOperator operatorNode)
    {
        QueryOperator current = operatorNode;
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
                default:
                    return null;
            }
        }
    }

    private async IAsyncEnumerable<RowBatch> ExecuteNestedLoopJoinAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = new(context);
        bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;

        // Physical side assignment mirrors the hash join path.
        QueryOperator buildSource = _flipped ? _left : _right;
        QueryOperator probeSource = _flipped ? _right : _left;

        bool leftMustAppear = _joinType is JoinType.Left or JoinType.FullOuter;
        bool rightMustAppear = _joinType is JoinType.Right or JoinType.FullOuter;
        bool needBuildUnmatched = _flipped ? leftMustAppear : rightMustAppear;
        bool needProbeUnmatched = _flipped ? rightMustAppear : leftMustAppear;

        // Build rows are stabilised into operator-owned DataValue[] rentals so
        // build batches can return to the pool immediately. Same-store fast path
        // under one-arena-per-query keeps this allocation-only (no payload copy).
        BuildSideMaterializer buildRows = new(pool, context.Store);
        JoinOutputWriter writer = new(context, pool);
        NullPadCache cachedNullBuild = new(pool);
        NullPadCache cachedNullProbe = new(pool);
        Row? reusableFilterRow = null;
        DataValue[]? reusableFilterBuffer = null;

        try
        {
            await buildRows.MaterializeAsync(buildSource, context).ConfigureAwait(false);

            BitArray? buildMatched = needBuildUnmatched
                ? new BitArray(buildRows.Count)
                : null;

            await foreach (RowBatch probeBatch in probeSource.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int probeIndex = 0; probeIndex < probeBatch.Count; probeIndex++)
                    {
                        Row probeRow = probeBatch[probeIndex];
                        bool hasMatch = false;

                        for (int index = 0; index < buildRows.Count; index++)
                        {
                            Row leftRow = _flipped ? buildRows[index] : probeRow;
                            Row rightRow = _flipped ? probeRow : buildRows[index];

                            if (_onCondition is not null)
                            {
                                JoinSchema schema = writer.GetCombinedSchema(leftRow, rightRow);
                                if (reusableFilterBuffer is null)
                                {
                                    (reusableFilterRow, reusableFilterBuffer) = schema.CreateReusableRow();
                                }

                                schema.CombineInto(leftRow, rightRow, reusableFilterBuffer);

                                if (!await evaluator.EvaluateAsBooleanAsync(_onCondition, reusableFilterRow.GetValueOrDefault(), context.CancellationToken).ConfigureAwait(false))
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

                            if (writer.EmitCombined(leftRow, rightRow) is RowBatch ready)
                                yield return ready;
                        }

                        if (isSemiJoin)
                        {
                            if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                                (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                            {
                                if (writer.EmitPassThrough(probeRow, probeBatch.Arena) is RowBatch ready)
                                    yield return ready;
                            }
                        }
                        else if (!hasMatch && needProbeUnmatched)
                        {
                            if (buildRows.Count > 0)
                            {
                                Row nullBuild = cachedNullBuild.GetOrCreate(buildRows[0]);
                                Row leftRow = _flipped ? nullBuild : probeRow;
                                Row rightRow = _flipped ? probeRow : nullBuild;
                                if (writer.EmitCombined(leftRow, rightRow) is RowBatch ready)
                                    yield return ready;
                            }
                            else
                            {
                                if (writer.EmitPassThrough(probeRow, probeBatch.Arena) is RowBatch ready)
                                    yield return ready;
                            }
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(probeBatch);
                }
            }

            // Emit unmatched build rows for OUTER joins.
            if (buildMatched is not null)
            {
                UnmatchedBuildEmitter unmatchedBuild = new(_flipped, writer, cachedNullProbe);
                await foreach (RowBatch ready in unmatchedBuild.EmitAsync(
                    buildMatched, buildRows.Rows, probeSource, context.Store, context).ConfigureAwait(false))
                {
                    yield return ready;
                }
            }

            if (writer.Flush() is RowBatch trailing) yield return trailing;
        }
        finally
        {
            if (writer.Flush() is RowBatch leftover) context.ReturnRowBatch(leftover);

            // Release pool-rented null pad row buffers.
            cachedNullBuild.Return();
            cachedNullProbe.Return();

            // Release the stabilised build-row buffers.
            buildRows.Return();
        }
    }

    private async IAsyncEnumerable<RowBatch> ExecuteCrossJoinAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        JoinOutputWriter writer = new(context, pool);

        // Right side rows are held in an operator-local list across the entire
        // probe phase, so we need DataValue[] arrays we own — not slices of the
        // input batches we want to return. RentAndCopyDataValues stabilises each
        // row into context.Store; under one-arena-per-query that's a same-store
        // fast path (no payload copy, just a fresh DataValue[] rental).
        BuildSideMaterializer rightRows = new(pool, context.Store);

        try
        {
            await rightRows.MaterializeAsync(_right, context).ConfigureAwait(false);

            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int leftIndex = 0; leftIndex < leftBatch.Count; leftIndex++)
                    {
                        Row leftRow = leftBatch[leftIndex];
                        foreach (Row rightRow in rightRows.Rows)
                        {
                            if (writer.EmitCombined(leftRow, rightRow) is RowBatch ready)
                                yield return ready;
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(leftBatch);
                }
            }

            if (writer.Flush() is RowBatch trailing) yield return trailing;
        }
        finally
        {
            if (writer.Flush() is RowBatch leftover) context.ReturnRowBatch(leftover);

            // Return the stabilised right-side row buffers to the pool. Under
            // one-arena-per-query the underlying payloads live in context.Store
            // (owned by the QueryPlan), so this only releases the DataValue[]
            // rentals — not the values they reference.
            rightRows.Return();
        }
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

        return new Row(new ColumnLookup(names), values);
    }

    /// <summary>
    /// Pushes build-side key values to all reachable probe-side <see cref="ScanOperator"/>
    /// instances for bloom-filter-based and sorted-index-based chunk pruning. Traverses
    /// through intermediate joins, aliases, filters, and projections so that multi-table
    /// join trees can propagate keys to buried scans.
    /// </summary>
    private void ApplyBloomPruning(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        IJoinHashTable hashTable)
    {
        List<BloomPruningPlanEntry> plan = ComputeBloomPruningPlan(keyPairs);
        if (plan.Count == 0)
        {
            return;
        }

        Dictionary<int, HashSet<DataValue>> keysByIndex = new(plan.Count);
        foreach (BloomPruningPlanEntry entry in plan)
        {
            HashSet<DataValue> keys = new();
            hashTable.CollectDistinctKeysAt(entry.KeyIndex, keys);
            keysByIndex[entry.KeyIndex] = keys;
        }

        PushBloomKeysToScans(plan, keysByIndex);
    }

    /// <summary>
    /// One bloom/sorted-index pruning target: a join-key index, the corresponding
    /// probe-side column name, and the probe-side <see cref="ScanOperator"/> instances
    /// that have bloom filters and/or sorted indices on that column.
    /// </summary>
    private readonly record struct BloomPruningPlanEntry(
        int KeyIndex,
        string ColumnName,
        List<ScanOperator> ScansWithBloom,
        List<ScanOperator> ScansWithSortedIndex);

    /// <summary>
    /// Inspects the probe-side scan tree and returns a per-key-index plan of which
    /// columns can be pruned via bloom filter or sorted index. Returns an empty list
    /// when no probe scan exposes a prunable column for any equi-join key.
    /// </summary>
    private List<BloomPruningPlanEntry> ComputeBloomPruningPlan(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs)
    {
        List<BloomPruningPlanEntry> plan = new();

        QueryOperator probeOperator = _flipped ? _right : _left;
        List<ScanOperator> probeScans = new();
        CollectScanOperators(probeOperator, probeScans);

        if (probeScans.Count == 0)
        {
            return plan;
        }

        for (int keyIndex = 0; keyIndex < keyPairs.Count; keyIndex++)
        {
            Expression probeKeyExpression = _flipped ? keyPairs[keyIndex].Right : keyPairs[keyIndex].Left;
            if (probeKeyExpression is not ColumnReference columnReference)
            {
                continue;
            }

            string columnName = columnReference.ColumnName;
            List<ScanOperator>? bloomScans = null;
            List<ScanOperator>? sortedScans = null;

            foreach (ScanOperator probeScan in probeScans)
            {
                bool hasBloom = probeScan.SourceIndex?.BloomFilters is BloomFilterSet bloomFilters
                    && bloomFilters.HasColumn(columnName);
                bool hasSortedIndex = probeScan.TableProvider.TryGetColumnIndex(columnName, out _);

                if (hasBloom)
                {
                    (bloomScans ??= new()).Add(probeScan);
                }

                if (hasSortedIndex)
                {
                    (sortedScans ??= new()).Add(probeScan);
                }
            }

            if (bloomScans is not null || sortedScans is not null)
            {
                plan.Add(new BloomPruningPlanEntry(
                    keyIndex,
                    columnName,
                    bloomScans ?? new List<ScanOperator>(),
                    sortedScans ?? new List<ScanOperator>()));
            }
        }

        return plan;
    }

    /// <summary>
    /// Pushes the collected distinct build-side keys (by key-index) into each probe-side
    /// scan that the <paramref name="plan"/> identifies as having a bloom filter or
    /// sorted index for the corresponding column. Empty key sets are skipped.
    /// </summary>
    private static void PushBloomKeysToScans(
        List<BloomPruningPlanEntry> plan,
        IReadOnlyDictionary<int, HashSet<DataValue>> keysByIndex)
    {
        foreach (BloomPruningPlanEntry entry in plan)
        {
            if (!keysByIndex.TryGetValue(entry.KeyIndex, out HashSet<DataValue>? keys) || keys.Count == 0)
            {
                continue;
            }

            foreach (ScanOperator scan in entry.ScansWithBloom)
            {
                scan.AddBloomPruningKeys(entry.ColumnName, keys);
            }

            foreach (ScanOperator scan in entry.ScansWithSortedIndex)
            {
                scan.AddSortedIndexPruningKeys(entry.ColumnName, keys);
            }
        }
    }

    /// <summary>
    /// Walks the build-side operator chain to find the <see cref="AliasOperator"/> that
    /// qualifies column names with the table alias. Returns the alias string, or
    /// <c>null</c> if no alias wrapper exists. Traverses through
    /// <see cref="FilterOperator"/> which may be inserted by predicate pushdown.
    /// </summary>
    private static string? FindBuildAlias(QueryOperator operatorNode)
    {
        QueryOperator current = operatorNode;
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
    internal static void CollectScanOperators(QueryOperator operatorNode, List<ScanOperator> results)
    {
        QueryOperator current = operatorNode;
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
