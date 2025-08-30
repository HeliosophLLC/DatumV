using System.Collections;
using System.Diagnostics;
using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using static DatumIngest.Execution.Operators.JoinOperator;

namespace DatumIngest.Execution;

/// <summary>
/// Executes a Grace hash join that partitions both build and probe sides by hash key,
/// spilling partitions to disk when estimated memory usage exceeds a configurable budget.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm has two phases:
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>Partition phase</strong>: Both sides are hashed into P partitions using the upper
/// bits of the join key hash. Partitions start in memory. When the memory estimator detects
/// that total build-side memory exceeds the budget, the largest partition is spilled to disk
/// via <see cref="SpillPartition"/>. Subsequent rows for a spilled partition are written
/// directly to the spill file.
/// </description></item>
/// <item><description>
/// <strong>Join phase</strong>: Each partition is joined independently using a standard
/// in-memory hash join (dictionary lookup on hash key lower bits). If a partition's build
/// side still exceeds the budget after the initial partitioning, it is recursively
/// re-partitioned with a different hash bit range.
/// </description></item>
/// </list>
/// <para>
/// <strong>Memory estimation strategy</strong>: For schemas where all columns are fixed-width
/// (Scalar, UInt8, Boolean, Date, DateTime, Time, Duration, Uuid), the row size is computed
/// exactly from the first row and no sampling is needed. For variable-width schemas (any
/// String, Vector, Matrix, Tensor, Image, UInt8Array, or JsonValue column), every 64th row is
/// sampled to maintain a running average row size. When the estimate crosses 75% of the budget,
/// every row is sampled until a spill decision fires.
/// </para>
/// </remarks>
internal sealed class GraceHashJoinExecutor
{
    /// <summary>Default number of rows between memory samples for variable-width schemas.</summary>
    private const int DefaultSampleInterval = 64;

    /// <summary>Maximum number of partitions (guards against degenerate hash distributions).</summary>
    private const int MaxPartitionCount = 256;

    /// <summary>Maximum recursion depth for re-partitioning.</summary>
    private const int MaxRecursionDepth = 3;

    private readonly JoinType _joinType;
    private readonly JoinKeyExtractionResult _extraction;
    private readonly long _memoryBudgetBytes;
    private readonly ExpressionEvaluator _evaluator;
    private readonly bool _nullSensitiveAntiSemi;
    private readonly bool _flipped;
    private readonly string _spillDirectory;

    /// <summary>Human-readable label used in trace output to identify this join's build side.</summary>
    private readonly string _label;

    /// <summary>
    /// Creates a new Grace hash join executor.
    /// </summary>
    /// <param name="joinType">The type of join (Inner, Left, Right, FullOuter, LeftSemi, LeftAntiSemi).</param>
    /// <param name="extraction">The extracted equi-join key pairs and optional residual filter.</param>
    /// <param name="memoryBudgetBytes">The memory budget in bytes for the build side.</param>
    /// <param name="evaluator">The expression evaluator for key and residual evaluation.</param>
    /// <param name="nullSensitiveAntiSemi">
    /// When true and <paramref name="joinType"/> is <see cref="JoinType.LeftAntiSemi"/>,
    /// applies SQL-standard NOT IN null semantics.
    /// </param>
    /// <param name="flipped">
    /// When <c>true</c>, the build and probe sides are physically swapped so the
    /// smaller side (left) is materialized while the larger side (right) is streamed.
    /// Output column order is preserved as [left | right].
    /// </param>
    /// <param name="label">Human-readable label for the build-side table, used in execution trace output.</param>
    internal GraceHashJoinExecutor(
        JoinType joinType,
        JoinKeyExtractionResult extraction,
        long memoryBudgetBytes,
        ExpressionEvaluator evaluator,
        bool nullSensitiveAntiSemi = false,
        bool flipped = false,
        string label = "")
    {
        _joinType = joinType;
        _extraction = extraction;
        _memoryBudgetBytes = memoryBudgetBytes;
        _evaluator = evaluator;
        _nullSensitiveAntiSemi = nullSensitiveAntiSemi;
        _flipped = flipped;
        _label = label;
        _spillDirectory = Path.Combine(Path.GetTempPath(), $"datum-join-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Executes the Grace hash join, streaming results as an async enumerable.
    /// </summary>
    /// <param name="leftOperator">The probe-side (left) operator.</param>
    /// <param name="rightOperator">The build-side (right) operator.</param>
    /// <param name="context">The execution context.</param>
    /// <returns>An async enumerable of joined rows.</returns>
    internal async IAsyncEnumerable<RowBatch> ExecuteAsync(
        IQueryOperator leftOperator,
        IQueryOperator rightOperator,
        ExecutionContext context)
    {
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;
        bool useSingleKey = keyPairs.Count == 1;

        // Physical side assignment. When flipped, left=build, right=probe.
        IQueryOperator buildOperator = _flipped ? leftOperator : rightOperator;
        IQueryOperator probeOperator = _flipped ? rightOperator : leftOperator;
        bool buildKeyIsRight = !_flipped;

        int partitionCount = ComputeInitialPartitionCount();
        SpillPartition[] partitions = CreatePartitions(partitionCount);

        ExecutionTracer.Initialize();
        long ph1aStart = Stopwatch.GetTimestamp();
        long buildRowCount = 0;
        long phase1bProbeCount = 0;

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.WriteSeparator();
            ExecutionTracer.Write($"JOIN Phase1a start  build={_label}  join={_joinType}  budget={ExecutionTracer.FormatBytes(_memoryBudgetBytes)}  partitions={partitionCount}");
        }

        try
        {
            // ── Phase 1: Partition the build side ──
            MemoryEstimator buildEstimator = new();
            Row? firstBuildRow = null;
            bool hasNullKey = false;
            long inMemoryRowCount = 0;

            await foreach (RowBatch buildBatch in buildOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int buildBatchIndex = 0; buildBatchIndex < buildBatch.Count; buildBatchIndex++)
                {
                Row buildRow = buildBatch[buildBatchIndex];
                buildRowCount++;
                firstBuildRow ??= buildRow;

                // Track null keys for NOT IN null semantics.
                if (_nullSensitiveAntiSemi && !hasNullKey)
                {
                    if (useSingleKey)
                    {
                        if (_evaluator.Evaluate(buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left, buildRow).IsNull)
                        {
                            hasNullKey = true;
                        }
                    }
                    else
                    {
                        DataValue[] parts = EvaluateKeyParts(keyPairs, buildRow, rightSide: buildKeyIsRight);
                        if (HasNull(parts))
                        {
                            hasNullKey = true;
                        }
                    }
                }

                int partitionIndex = AssignPartition(buildRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: buildKeyIsRight);
                partitions[partitionIndex].AddBuildRow(buildRow);

                // Only count rows that landed in memory (not appended to an already-spilled partition).
                if (!partitions[partitionIndex].IsBuildSpilled)
                {
                    inMemoryRowCount++;
                }

                // Memory monitoring with sampling.
                if (buildEstimator.ShouldSample())
                {
                    buildEstimator.RecordSample(buildRow);
                }

                buildEstimator.IncrementRowCount();

                // Estimate memory for in-memory rows only — spilled rows consume disk, not RAM.
                long estimatedMemory = buildEstimator.EstimateBytesForRowCount(inMemoryRowCount);

                if (estimatedMemory > _memoryBudgetBytes)
                {
                    inMemoryRowCount -= SpillLargestPartition(partitions);
                }
                else if (estimatedMemory > (long)(_memoryBudgetBytes * MemoryEstimator.EscalationThreshold))
                {
                    buildEstimator.EscalateToEveryRow();
                }
                }
                buildBatch.Return();
            }

            if (ExecutionTracer.IsEnabled)
            {
                int spilledPartitions = 0;
                foreach (SpillPartition p in partitions) if (p.IsBuildSpilled) spilledPartitions++;
                ExecutionTracer.Write($"JOIN Phase1a done   build_rows={buildRowCount:N0}  in_memory={inMemoryRowCount:N0}  estimated_total={ExecutionTracer.FormatBytes(buildEstimator.EstimateTotalBytes())}  estimated_inmem={ExecutionTracer.FormatBytes(buildEstimator.EstimateBytesForRowCount(inMemoryRowCount))}  spilled={spilledPartitions}/{partitionCount}  elapsed={Stopwatch.GetElapsedTime(ph1aStart).TotalMilliseconds:F0}ms");
            }

            // NOT IN null semantics: if any build-side key is NULL, the entire result is empty.
            if (_nullSensitiveAntiSemi && hasNullKey)
            {
                yield break;
            }

            // Null build template can be computed as soon as Phase 1a completes.
            // It is needed during hybrid Phase 1b for LEFT JOIN null extension.
            Row? nullBuildTemplate = firstBuildRow is not null ? CreateNullRow(firstBuildRow.Value) : null;

            bool leftMustAppear = _joinType is JoinType.Left or JoinType.FullOuter;
            bool rightMustAppear = _joinType is JoinType.Right or JoinType.FullOuter;
            bool needBuildUnmatched = _flipped ? leftMustAppear : rightMustAppear;
            bool useHybrid = !needBuildUnmatched;
            bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;

            // ── Phase 1b: Probe phase ──
            Row? firstProbeRow = null;
            long ph1bStart = Stopwatch.GetTimestamp();

            if (useHybrid)
            {
                // Hybrid streaming probe: probe rows are streamed one-at-a-time
                // against in-memory build partitions, yielding results immediately
                // so a LIMIT above can terminate without fully materialising the
                // probe stream.
                PartitionBuildTable?[] buildTables = new PartitionBuildTable?[partitionCount];

                for (int tableIndex = 0; tableIndex < partitionCount; tableIndex++)
                {
                    if (!partitions[tableIndex].IsBuildSpilled)
                    {
                        buildTables[tableIndex] = BuildPartitionTable(
                            partitions[tableIndex].GetInMemoryBuildRows(),
                            keyPairs,
                            useSingleKey);
                    }
                }

                RowBatch? outputBatch = null;

                await foreach (RowBatch probeBatch in probeOperator.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int probeBatchIndex = 0; probeBatchIndex < probeBatch.Count; probeBatchIndex++)
                    {
                    Row probeRow = probeBatch[probeBatchIndex];
                    bool isFirst = firstProbeRow is null;
                    firstProbeRow ??= probeRow;
                    phase1bProbeCount++;
                    int partitionIndex = AssignPartition(probeRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: !buildKeyIsRight);
                    SpillPartition partition = partitions[partitionIndex];

                    if (!partition.IsBuildSpilled)
                    {
                        // In-memory partition: join and yield immediately.
                        // A LIMIT operator above can stop iteration here, preventing the
                        // remaining probe rows from ever being read from the source.
                        foreach (Row result in ProbePartitionRow(
                            buildTables[partitionIndex]!, probeRow, keyPairs, useSingleKey, isSemiJoin, nullBuildTemplate, context.LocalBufferPool))
                        {
                            outputBatch ??= RowBatch.Rent(context.BatchSize);
                            outputBatch.Add(result);
                            if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                        }

                        // The probe row has been fully consumed — return it to the pool
                        // so that upstream operators (e.g. AliasOperator) can reuse it.
                        // Semi-joins yield the probe row itself, so it must not be returned.
                        // The first probe row is retained for null-template construction
                        // after the loop, so skip returning it.
                        if (!isSemiJoin && !isFirst)
                        {
                            context.LocalBufferPool.ReturnValues(probeRow);
                        }
                    }
                    else
                    {
                        // Spilled partition: buffer probe row to disk for Phase 2.
                        if (!partition.IsProbeSpilled)
                        {
                            partition.SpillProbeToDisk();
                        }

                        partition.AddProbeRow(probeRow);
                    }
                    }
                    probeBatch.Return();
                }

                if (outputBatch is not null) { yield return outputBatch; outputBatch = null; }
            }
            else
            {
                // Non-hybrid path: buffer all probe rows before joining.
                // Required when unmatched build rows must be emitted after the
                // complete probe set has been consumed.
                await foreach (RowBatch probeBatch in probeOperator.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int probeBatchIndex = 0; probeBatchIndex < probeBatch.Count; probeBatchIndex++)
                    {
                    Row probeRow = probeBatch[probeBatchIndex];
                    firstProbeRow ??= probeRow;
                    phase1bProbeCount++;
                    int partitionIndex = AssignPartition(probeRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: !buildKeyIsRight);
                    SpillPartition partition = partitions[partitionIndex];

                    if (partition.IsBuildSpilled)
                    {
                        if (!partition.IsProbeSpilled)
                        {
                            partition.SpillProbeToDisk();
                        }

                        partition.AddProbeRow(probeRow);
                    }
                    else
                    {
                        partition.AddProbeRow(probeRow);
                    }
                    }
                    probeBatch.Return();
                }
            }

            Row? nullProbeTemplate = firstProbeRow is not null ? CreateNullRow(firstProbeRow.Value) : null;

            if (ExecutionTracer.IsEnabled)
            {
                ExecutionTracer.Write($"JOIN Phase1b done   probe_rows={phase1bProbeCount:N0}  elapsed={Stopwatch.GetElapsedTime(ph1bStart).TotalMilliseconds:F0}ms");
            }

            long ph2Start = Stopwatch.GetTimestamp();
            if (ExecutionTracer.IsEnabled)
            {
                int spilledPartitions = 0;
                foreach (SpillPartition p in partitions) if (p.IsBuildSpilled) spilledPartitions++;
                ExecutionTracer.Write($"JOIN Phase2 start   spilled_partitions={spilledPartitions}");
            }

            // ── Phase 2: Join each partition ──
            // When hybrid mode is active, in-memory partitions were fully processed during
            // Phase 1b and their probe lists are empty. Pass skipInMemory=true so
            // JoinAllPartitionsAsync skips them, avoiding redundant work.
            // The template parameters use logical (left, right) naming, but nullBuildTemplate
            // and nullProbeTemplate use physical roles. Swap when flipped to preserve semantics.
            Row? nullLeftTemplate = _flipped ? nullBuildTemplate : nullProbeTemplate;
            Row? nullRightTemplate = _flipped ? nullProbeTemplate : nullBuildTemplate;

            await foreach (RowBatch joinBatch in JoinAllPartitionsAsync(
                partitions, useSingleKey, recursionDepth: 0, nullLeftTemplate, nullRightTemplate, context,
                skipInMemory: useHybrid)
                .ConfigureAwait(false))
            {
                yield return joinBatch;
            }
        }
        finally
        {
            if (ExecutionTracer.IsEnabled)
            {
                ExecutionTracer.Write($"JOIN complete       build={_label}  total_build={buildRowCount:N0}  total_probe={phase1bProbeCount:N0}  total_elapsed={Stopwatch.GetElapsedTime(ph1aStart).TotalMilliseconds:F0}ms");
                ExecutionTracer.WriteSeparator();
            }

            foreach (SpillPartition partition in partitions)
            {
                partition.Dispose();
            }

            CleanupSpillDirectory();
        }
    }

    /// <summary>
    /// Holds the in-memory hash table for a single Grace hash join partition,
    /// built after Phase 1a to enable the hybrid streaming probe in Phase 1b.
    /// Caches the <see cref="CombinedRowSchema"/> and null-build template so they are
    /// allocated at most once per partition across many probe row lookups.
    /// </summary>
    private sealed class PartitionBuildTable
    {
        internal readonly List<Row> BuildRows;
        internal readonly Dictionary<DataValue, List<(int Index, Row Row)>>? SingleKeyTable;
        internal readonly Dictionary<CompositeKey, List<(int Index, Row Row)>>? CompositeKeyTable;
        internal CombinedRowSchema? JoinSchema;
        internal Row? CachedNullBuild;

        internal PartitionBuildTable(List<Row> buildRows, bool useSingleKey)
        {
            BuildRows = buildRows;
            SingleKeyTable = useSingleKey ? new() : null;
            CompositeKeyTable = useSingleKey ? null : new();
        }
    }

    /// <summary>
    /// Builds an in-memory <see cref="PartitionBuildTable"/> from the given build-side rows,
    /// hashing each row into the appropriate lookup structure for fast probe-phase lookups.
    /// </summary>
    private PartitionBuildTable BuildPartitionTable(
        IReadOnlyList<Row> buildRows,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        bool useSingleKey)
    {
        bool buildKeyIsRight = !_flipped;
        List<Row> buildList = new(buildRows.Count);
        PartitionBuildTable table = new(buildList, useSingleKey);

        foreach (Row buildRow in buildRows)
        {
            int buildIndex = buildList.Count;
            buildList.Add(buildRow);

            if (useSingleKey)
            {
                DataValue key = _evaluator.Evaluate(buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left, buildRow);
                if (!key.IsNull)
                {
                    if (!table.SingleKeyTable!.TryGetValue(key, out List<(int, Row)>? bucket))
                    {
                        bucket = new List<(int, Row)>();
                        table.SingleKeyTable[key] = bucket;
                    }

                    bucket.Add((buildIndex, buildRow));
                }
            }
            else
            {
                DataValue[] parts = EvaluateKeyParts(keyPairs, buildRow, rightSide: buildKeyIsRight);
                if (!HasNull(parts))
                {
                    CompositeKey compositeKey = new(parts);
                    if (!table.CompositeKeyTable!.TryGetValue(compositeKey, out List<(int, Row)>? bucket))
                    {
                        bucket = new List<(int, Row)>();
                        table.CompositeKeyTable[compositeKey] = bucket;
                    }

                    bucket.Add((buildIndex, buildRow));
                }
            }
        }

        return table;
    }

    /// <summary>
    /// Probes a single row against a <see cref="PartitionBuildTable"/>, yielding every
    /// matching combined row. Handles INNER, LEFT outer, RIGHT outer (when flipped),
    /// LeftSemi, and LeftAntiSemi join types.
    /// </summary>
    private IEnumerable<Row> ProbePartitionRow(
        PartitionBuildTable table,
        Row probeRow,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        bool useSingleKey,
        bool isSemiJoin,
        Row? nullBuildTemplate,
        LocalBufferPool bufferPool)
    {
        bool buildKeyIsRight = !_flipped;
        List<(int Index, Row Row)>? matches = null;

        if (useSingleKey)
        {
            DataValue probeKey = _evaluator.Evaluate(
                buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow);
            if (!probeKey.IsNull)
            {
                table.SingleKeyTable!.TryGetValue(probeKey, out matches);
            }
        }
        else
        {
            DataValue[] parts = EvaluateKeyParts(keyPairs, probeRow, rightSide: !buildKeyIsRight);
            if (!HasNull(parts))
            {
                CompositeKey compositeKey = new(parts);
                table.CompositeKeyTable!.TryGetValue(compositeKey, out matches);
            }
        }

        bool hasMatch = false;

        if (matches is not null)
        {
            foreach ((int _, Row buildRow) in matches)
            {
                // Combine in logical (left, right) order regardless of physical assignment.
                Row leftRow = _flipped ? buildRow : probeRow;
                Row rightRow = _flipped ? probeRow : buildRow;

                if (_extraction.Residual is not null)
                {
                    table.JoinSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                    Row combinedRow = table.JoinSchema.Combine(leftRow, rightRow);

                    if (!_evaluator.EvaluateAsBoolean(_extraction.Residual, combinedRow))
                    {
                        continue;
                    }
                }

                hasMatch = true;

                if (isSemiJoin)
                {
                    break;
                }

                if (_extraction.Residual is null)
                {
                    table.JoinSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                }

                yield return table.JoinSchema!.CombinePooled(leftRow, rightRow, bufferPool);
            }
        }

        if (isSemiJoin)
        {
            if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                (_joinType == JoinType.LeftAntiSemi && !hasMatch))
            {
                yield return probeRow;
            }
        }
        else if (!hasMatch)
        {
            // Unmatched probe row: emit when the probe side must fully appear in output.
            bool needProbeUnmatched = _flipped
                ? (_joinType is JoinType.Right or JoinType.FullOuter)
                : (_joinType is JoinType.Left or JoinType.FullOuter);

            if (needProbeUnmatched)
            {
                Row? nullBuild = table.CachedNullBuild;

                if (nullBuild is null && table.BuildRows.Count > 0)
                {
                    table.CachedNullBuild = CreateNullRow(table.BuildRows[0]);
                    nullBuild = table.CachedNullBuild;
                }

                nullBuild ??= nullBuildTemplate;

                if (nullBuild is not null)
                {
                    Row leftRow = _flipped ? nullBuild.Value : probeRow;
                    Row rightRow = _flipped ? probeRow : nullBuild.Value;
                    table.JoinSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                    yield return table.JoinSchema.CombinePooled(leftRow, rightRow, bufferPool);
                }
                else
                {
                    yield return probeRow;
                }
            }
        }
    }

    private async IAsyncEnumerable<RowBatch> JoinAllPartitionsAsync(
        SpillPartition[] partitions,
        bool useSingleKey,
        int recursionDepth,
        Row? nullLeftTemplate,
        Row? nullRightTemplate,
        ExecutionContext context,
        bool skipInMemory = false)
    {
        RowBatch? outputBatch = null;
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;
        bool buildKeyIsRight = !_flipped;

        for (int partitionIndex = 0; partitionIndex < partitions.Length; partitionIndex++)
        {
            SpillPartition partition = partitions[partitionIndex];

            // When skipInMemory is true (hybrid probe mode), in-memory partitions were
            // already joined and yielded during Phase 1b streaming. Skip them here to
            // avoid re-processing their build rows against an empty probe list.
            if (skipInMemory && !partition.IsBuildSpilled)
            {
                continue;
            }

            // Get build rows (from memory or disk).
            IEnumerable<Row> buildRows = partition.IsBuildSpilled
                ? partition.ReadSpilledBuildRows()
                : partition.GetInMemoryBuildRows();

            // Get probe rows (from memory or disk).
            IEnumerable<Row> probeRows = partition.IsProbeSpilled
                ? partition.ReadSpilledProbeRows()
                : partition.GetInMemoryProbeRows();

            // Materialize build side into a hash table for this partition.
            List<Row> buildRowList = new();
            Dictionary<DataValue, List<(int Index, Row Row)>>? singleKeyTable =
                useSingleKey ? new() : null;
            Dictionary<CompositeKey, List<(int Index, Row Row)>>? compositeKeyTable =
                useSingleKey ? null : new();

            long buildSizeEstimate = 0;
            MemoryEstimator partitionEstimator = new();

            foreach (Row buildRow in buildRows)
            {
                int buildIndex = buildRowList.Count;
                buildRowList.Add(buildRow);

                if (partitionEstimator.ShouldSample())
                {
                    partitionEstimator.RecordSample(buildRow);
                }

                partitionEstimator.IncrementRowCount();
                buildSizeEstimate = partitionEstimator.EstimateTotalBytes();

                if (useSingleKey)
                {
                    DataValue keyValue = _evaluator.Evaluate(
                        buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left, buildRow);
                    if (!keyValue.IsNull)
                    {
                        if (!singleKeyTable!.TryGetValue(keyValue, out List<(int, Row)>? bucket))
                        {
                            bucket = new List<(int, Row)>();
                            singleKeyTable[keyValue] = bucket;
                        }

                        bucket.Add((buildIndex, buildRow));
                    }
                }
                else
                {
                    DataValue[] parts = EvaluateKeyParts(keyPairs, buildRow, rightSide: buildKeyIsRight);
                    if (!HasNull(parts))
                    {
                        CompositeKey compositeKey = new(parts);
                        if (!compositeKeyTable!.TryGetValue(compositeKey, out List<(int, Row)>? bucket))
                        {
                            bucket = new List<(int, Row)>();
                            compositeKeyTable[compositeKey] = bucket;
                        }

                        bucket.Add((buildIndex, buildRow));
                    }
                }
            }

            // If partition is still too large after initial partitioning, recursively re-partition.
            if (buildSizeEstimate > _memoryBudgetBytes && recursionDepth < MaxRecursionDepth)
            {
                if (outputBatch is not null) { yield return outputBatch; outputBatch = null; }

                await foreach (RowBatch recursionBatch in RecursivelyRepartitionAsync(
                    buildRowList, probeRows, useSingleKey, recursionDepth + 1,
                    nullLeftTemplate, nullRightTemplate, context).ConfigureAwait(false))
                {
                    yield return recursionBatch;
                }

                continue;
            }

            // Probe the hash table with probe-side rows.
            bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;
            bool leftMustAppear = _joinType is JoinType.Left or JoinType.FullOuter;
            bool rightMustAppear = _joinType is JoinType.Right or JoinType.FullOuter;
            bool needBuildUnmatched = _flipped ? leftMustAppear : rightMustAppear;
            bool needProbeUnmatched = _flipped ? rightMustAppear : leftMustAppear;
            BitArray? buildMatched = needBuildUnmatched ? new BitArray(buildRowList.Count) : null;
            CombinedRowSchema? schema = null;
            Row? cachedNullBuild = null;

            foreach (Row probeRow in probeRows)
            {
                bool hasMatch = false;
                List<(int Index, Row Row)>? matches = null;

                if (useSingleKey)
                {
                    DataValue probeKeyValue = _evaluator.Evaluate(
                        buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow);
                    if (!probeKeyValue.IsNull)
                    {
                        singleKeyTable!.TryGetValue(probeKeyValue, out matches);
                    }
                }
                else
                {
                    DataValue[] parts = EvaluateKeyParts(keyPairs, probeRow, rightSide: !buildKeyIsRight);
                    if (!HasNull(parts))
                    {
                        CompositeKey compositeKey = new(parts);
                        compositeKeyTable!.TryGetValue(compositeKey, out matches);
                    }
                }

                if (matches is not null)
                {
                    foreach ((int buildIndex, Row buildRow) in matches)
                    {
                        // Combine in logical (left, right) order.
                        Row leftRow = _flipped ? buildRow : probeRow;
                        Row rightRow = _flipped ? probeRow : buildRow;

                        if (_extraction.Residual is not null)
                        {
                            schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                            Row combinedRow = schema.Combine(leftRow, rightRow);
                            if (!_evaluator.EvaluateAsBoolean(_extraction.Residual, combinedRow))
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

                        if (_extraction.Residual is null)
                        {
                            schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                        }

                        Row combinedResult = schema!.CombinePooled(leftRow, rightRow, context.LocalBufferPool);
                        outputBatch ??= RowBatch.Rent(context.BatchSize);
                        outputBatch.Add(combinedResult);
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }
                }

                if (isSemiJoin)
                {
                    if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                        (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                    {
                        outputBatch ??= RowBatch.Rent(context.BatchSize);
                        outputBatch.Add(probeRow);
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }

                    // Semi-join may yield probeRow directly — skip the ReturnRow below.
                    continue;
                }
                else if (!hasMatch && needProbeUnmatched)
                {
                    Row? nullBuild = cachedNullBuild;
                    if (nullBuild is null && buildRowList.Count > 0)
                    {
                        cachedNullBuild = CreateNullRow(buildRowList[0]);
                        nullBuild = cachedNullBuild;
                    }

                    nullBuild ??= nullRightTemplate;

                    if (nullBuild is not null)
                    {
                        Row leftRow = _flipped ? nullBuild.Value : probeRow;
                        Row rightRow = _flipped ? probeRow : nullBuild.Value;
                        schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                        Row unmatchedProbeResult = schema.CombinePooled(leftRow, rightRow, context.LocalBufferPool);
                        outputBatch ??= RowBatch.Rent(context.BatchSize);
                        outputBatch.Add(unmatchedProbeResult);
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }
                    else
                    {
                        // No null template — yield the probe row directly.
                        // Cannot return it to the pool since the caller still owns it.
                        outputBatch ??= RowBatch.Rent(context.BatchSize);
                        outputBatch.Add(probeRow);
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                        continue;
                    }
                }

                // Probe row fully consumed — return it to the pool. Semi-joins
                // and the null-template fallback yield probeRow directly (handled
                // with continue above), so we only reach here when probeRow was
                // consumed by value through CombinePooled.
                if (!isSemiJoin)
                {
                    context.LocalBufferPool.ReturnValues(probeRow);
                }
            }

            // Emit unmatched build rows when the build side must fully appear.
            if (buildMatched is not null)
            {
                CombinedRowSchema? buildUnmatchedSchema = null;

                for (int index = 0; index < buildRowList.Count; index++)
                {
                    if (!buildMatched[index])
                    {
                        Row? nullPad = _flipped ? nullRightTemplate : nullLeftTemplate;

                        if (nullPad is not null)
                        {
                            Row leftRow = _flipped ? buildRowList[index] : nullPad.Value;
                            Row rightRow = _flipped ? nullPad.Value : buildRowList[index];
                            buildUnmatchedSchema ??= CombinedRowSchema.Build(leftRow, rightRow);
                            Row unmatchedBuildResult = buildUnmatchedSchema.CombinePooled(leftRow, rightRow, context.LocalBufferPool);
                            outputBatch ??= RowBatch.Rent(context.BatchSize);
                            outputBatch.Add(unmatchedBuildResult);
                            if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                        }
                        else
                        {
                            outputBatch ??= RowBatch.Rent(context.BatchSize);
                            outputBatch.Add(buildRowList[index]);
                            if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
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

    private async IAsyncEnumerable<RowBatch> RecursivelyRepartitionAsync(
        List<Row> buildRows,
        IEnumerable<Row> probeRows,
        bool useSingleKey,
        int recursionDepth,
        Row? nullLeftTemplate,
        Row? nullRightTemplate,
        ExecutionContext context)
    {
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;
        bool buildKeyIsRight = !_flipped;
        int subPartitionCount = Math.Min(MaxPartitionCount, Math.Max(4, buildRows.Count / 1000));
        string subSpillDir = Path.Combine(_spillDirectory, $"recurse_{recursionDepth}_{Guid.NewGuid():N}");
        SpillPartition[] subPartitions = new SpillPartition[subPartitionCount];

        for (int index = 0; index < subPartitionCount; index++)
        {
            subPartitions[index] = new SpillPartition(subSpillDir, index);
        }

        try
        {
            // Re-partition build rows with shifted hash bits.
            MemoryEstimator estimator = new();

            foreach (Row buildRow in buildRows)
            {
                int partitionIndex = AssignPartition(buildRow, keyPairs, useSingleKey, subPartitionCount, recursionDepth, rightSide: buildKeyIsRight);
                subPartitions[partitionIndex].AddBuildRow(buildRow);

                if (estimator.ShouldSample())
                {
                    estimator.RecordSample(buildRow);
                }

                estimator.IncrementRowCount();

                if (estimator.EstimateTotalBytes() > _memoryBudgetBytes)
                {
                    SpillLargestPartition(subPartitions);
                }
            }

            // Re-partition probe rows.
            foreach (Row probeRow in probeRows)
            {
                int partitionIndex = AssignPartition(probeRow, keyPairs, useSingleKey, subPartitionCount, recursionDepth, rightSide: !buildKeyIsRight);
                SpillPartition partition = subPartitions[partitionIndex];

                if (partition.IsBuildSpilled && !partition.IsProbeSpilled)
                {
                    partition.SpillProbeToDisk();
                }

                partition.AddProbeRow(probeRow);
            }

            await foreach (RowBatch joinBatch in JoinAllPartitionsAsync(
                subPartitions, useSingleKey, recursionDepth, nullLeftTemplate, nullRightTemplate, context)
                .ConfigureAwait(false))
            {
                yield return joinBatch;
            }
        }
        finally
        {
            foreach (SpillPartition partition in subPartitions)
            {
                partition.Dispose();
            }

            if (Directory.Exists(subSpillDir))
            {
                Directory.Delete(subSpillDir, recursive: true);
            }
        }
    }

    private int AssignPartition(
        Row row,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        bool useSingleKey,
        int partitionCount,
        int recursionDepth,
        bool rightSide)
    {
        int hash;

        if (useSingleKey)
        {
            Expression keyExpression = rightSide ? keyPairs[0].Right : keyPairs[0].Left;
            DataValue keyValue = _evaluator.Evaluate(keyExpression, row);
            if (keyValue.IsNull)
            {
                // Null keys go to partition 0 (they won't match anything).
                return 0;
            }

            hash = keyValue.GetHashCode();
        }
        else
        {
            DataValue[] parts = EvaluateKeyParts(keyPairs, row, rightSide);
            if (HasNull(parts))
            {
                return 0;
            }

            CompositeKey compositeKey = new(parts);
            hash = compositeKey.GetHashCode();
        }

        // Shift bits based on recursion depth to get different partition assignments.
        int shifted = (int)((uint)hash >> (16 + recursionDepth * 4));
        return Math.Abs(shifted) % partitionCount;
    }

    private int ComputeInitialPartitionCount()
    {
        // Without upfront row count estimates, start with a reasonable default.
        // The partition count is capped at MaxPartitionCount.
        return Math.Min(MaxPartitionCount, Math.Max(4, (int)(_memoryBudgetBytes / (1024 * 1024))));
    }

    /// <summary>
    /// Spills the largest non-spilled partition to disk and returns the number of
    /// in-memory build rows that were flushed, so callers can adjust their in-memory
    /// row count accordingly.
    /// </summary>
    private static long SpillLargestPartition(SpillPartition[] partitions)
    {
        int largestIndex = -1;
        int largestCount = 0;

        for (int index = 0; index < partitions.Length; index++)
        {
            if (!partitions[index].IsBuildSpilled && partitions[index].InMemoryBuildRowCount > largestCount)
            {
                largestCount = partitions[index].InMemoryBuildRowCount;
                largestIndex = index;
            }
        }

        if (largestIndex >= 0)
        {
            partitions[largestIndex].SpillBuildToDisk();
            return largestCount;
        }

        return 0;
    }

    private SpillPartition[] CreatePartitions(int count)
    {
        SpillPartition[] partitions = new SpillPartition[count];
        for (int index = 0; index < count; index++)
        {
            partitions[index] = new SpillPartition(_spillDirectory, index);
        }

        return partitions;
    }

    private DataValue[] EvaluateKeyParts(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Row row,
        bool rightSide)
    {
        DataValue[] parts = new DataValue[keyPairs.Count];
        for (int index = 0; index < keyPairs.Count; index++)
        {
            Expression expression = rightSide ? keyPairs[index].Right : keyPairs[index].Left;
            parts[index] = _evaluator.Evaluate(expression, row);
        }

        return parts;
    }

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

    private void CleanupSpillDirectory()
    {
        if (Directory.Exists(_spillDirectory))
        {
            try
            {
                Directory.Delete(_spillDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup — temp files will be cleaned up by the OS.
            }
        }
    }

    /// <summary>
    /// Creates a row with the same column names as the template but all null values.
    /// </summary>
    private static Row CreateNullRow(Row template)
    {
        string[] names = new string[template.FieldCount];
        DataValue[] values = new DataValue[template.FieldCount];

        for (int index = 0; index < template.FieldCount; index++)
        {
            names[index] = template.ColumnNames[index];
            values[index] = DataValue.Null(DataKind.Float32);
        }

        return new Row(names, values);
    }
}
