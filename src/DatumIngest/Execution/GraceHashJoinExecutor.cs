using System.Collections;
using System.Diagnostics;
using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
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
/// String, Vector, Image, byte-array, or Struct column), every 64th row is
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

    /// <summary>Estimated build-side row count for pre-sizing partition lists.</summary>
    private readonly long? _estimatedBuildRows;

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
    /// <param name="estimatedBuildRows">
    /// Optional estimated build-side row count. Used to pre-size partition lists
    /// and avoid LOH-crossing doublings that drive Gen2 GC pressure.
    /// </param>
    internal GraceHashJoinExecutor(
        JoinType joinType,
        JoinKeyExtractionResult extraction,
        long memoryBudgetBytes,
        ExpressionEvaluator evaluator,
        bool nullSensitiveAntiSemi = false,
        bool flipped = false,
        string label = "",
        long? estimatedBuildRows = null)
    {
        _joinType = joinType;
        _extraction = extraction;
        _memoryBudgetBytes = memoryBudgetBytes;
        _evaluator = evaluator;
        _nullSensitiveAntiSemi = nullSensitiveAntiSemi;
        _flipped = flipped;
        _label = label;
        _estimatedBuildRows = estimatedBuildRows;
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
        Pool pool = context.Pool;
        LocalBufferPool bufferPool = context.LocalBufferPool;

        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;
        bool useSingleKey = keyPairs.Count == 1;

        // Physical side assignment. When flipped, left=build, right=probe.
        IQueryOperator buildOperator = _flipped ? leftOperator : rightOperator;
        IQueryOperator probeOperator = _flipped ? rightOperator : leftOperator;
        bool buildKeyIsRight = !_flipped;

        int partitionCount = ComputeInitialPartitionCount();
        SpillPartition[] partitions = CreatePartitions(partitionCount, pool, bufferPool, context);

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
            DataValue[] keyScratch = useSingleKey ? [] : new DataValue[keyPairs.Count];

            ExecutionTracer.Write($"JOIN Phase1a iterating buildOperator type={buildOperator.GetType().Name}");
            await foreach (RowBatch buildBatch in buildOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                if (buildRowCount == 0)
                {
                    ExecutionTracer.Write($"JOIN Phase1a first build batch  count={buildBatch.Count}");
                }

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
                            if ((await _evaluator.EvaluateAsync(buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left, buildRow, context.CancellationToken).ConfigureAwait(false)).IsNull)
                            {
                                hasNullKey = true;
                            }
                        }
                        else
                        {
                            await EvaluateKeyPartsIntoAsync(keyPairs, buildRow, buildKeyIsRight, keyScratch, context.CancellationToken).ConfigureAwait(false);
                            if (HasNull(keyScratch))
                            {
                                hasNullKey = true;
                            }
                        }
                    }

                    int partitionIndex = await AssignPartitionAsync(buildRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: buildKeyIsRight, keyScratch, context.CancellationToken).ConfigureAwait(false);
                    partitions[partitionIndex].AddBuildRow(buildRow, buildBatch.Arena);

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
                context.ReturnRowBatch(buildBatch);
            }

            if (ExecutionTracer.IsEnabled)
            {
                int spilledPartitions = 0;
                foreach (SpillPartition p in partitions) if (p.IsBuildSpilled) spilledPartitions++;
                long processMemory = GC.GetTotalMemory(forceFullCollection: false);
                ExecutionTracer.Write($"JOIN Phase1a done   build_rows={buildRowCount:N0}  in_memory={inMemoryRowCount:N0}  estimated_total={ExecutionTracer.FormatBytes(buildEstimator.EstimateTotalBytes())}  estimated_inmem={ExecutionTracer.FormatBytes(buildEstimator.EstimateBytesForRowCount(inMemoryRowCount))}  spilled={spilledPartitions}/{partitionCount}  process_mem={ExecutionTracer.FormatBytes(processMemory)}  elapsed={Stopwatch.GetElapsedTime(ph1aStart).TotalMilliseconds:F0}ms");
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
                        buildTables[tableIndex] = await BuildPartitionTableAsync(
                            partitions[tableIndex].GetInMemoryBuildRows(),
                            keyPairs,
                            useSingleKey,
                            context.CancellationToken).ConfigureAwait(false);
                    }
                }

                RowBatch? outputBatch = null;

                await foreach (RowBatch probeBatch in probeOperator.ExecuteAsync(context).ConfigureAwait(false))
                {
                    try
                    {
                        for (int probeBatchIndex = 0; probeBatchIndex < probeBatch.Count; probeBatchIndex++)
                        {
                            Row probeRow = probeBatch[probeBatchIndex];
                            firstProbeRow ??= probeRow;
                            phase1bProbeCount++;
                            int partitionIndex = await AssignPartitionAsync(probeRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: !buildKeyIsRight, keyScratch, context.CancellationToken).ConfigureAwait(false);
                            SpillPartition partition = partitions[partitionIndex];

                            if (!partition.IsBuildSpilled)
                            {
                                // In-memory partition: join and yield immediately.
                                // A LIMIT operator above can stop iteration here, preventing the
                                // remaining probe rows from ever being read from the source.
                                await foreach (Row result in ProbePartitionRowAsync(
                                    buildTables[partitionIndex]!, probeRow, keyPairs, useSingleKey, isSemiJoin, nullBuildTemplate, context, keyScratch, context.CancellationToken).ConfigureAwait(false))
                                {
                                    outputBatch ??= context.RentRowBatch(result.ColumnLookup);
                                    outputBatch.Add(result.RawValues);
                                    if (outputBatch.IsFull)
                                    {
                                        RowBatch toYield = outputBatch;
                                        outputBatch = null;
                                        yield return toYield;
                                    }
                                }
                            }
                            else
                            {
                                // Spilled partition: buffer probe row to disk for Phase 2.
                                if (!partition.IsProbeSpilled)
                                {
                                    partition.SpillProbeToDisk();
                                }

                                partition.AddProbeRow(probeRow, probeBatch.Arena);
                            }
                        }
                    }
                    finally
                    {
                        context.ReturnRowBatch(probeBatch);
                    }
                }

                if (outputBatch is not null)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }
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
                        int partitionIndex = await AssignPartitionAsync(probeRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: !buildKeyIsRight, keyScratch, context.CancellationToken).ConfigureAwait(false);
                        SpillPartition partition = partitions[partitionIndex];

                        if (partition.IsBuildSpilled)
                        {
                            if (!partition.IsProbeSpilled)
                            {
                                partition.SpillProbeToDisk();
                            }

                            partition.AddProbeRow(probeRow, probeBatch.Arena);
                        }
                        else
                        {
                            partition.AddProbeRow(probeRow, probeBatch.Arena);
                        }
                    }
                    context.ReturnRowBatch(probeBatch);
                }
            }

            Row? nullProbeTemplate = firstProbeRow is not null ? CreateNullRow(firstProbeRow.Value) : null;

            if (ExecutionTracer.IsEnabled)
            {
                long probeProcessMemory = GC.GetTotalMemory(forceFullCollection: false);
                ExecutionTracer.Write($"JOIN Phase1b done   probe_rows={phase1bProbeCount:N0}  process_mem={ExecutionTracer.FormatBytes(probeProcessMemory)}  elapsed={Stopwatch.GetElapsedTime(ph1bStart).TotalMilliseconds:F0}ms");
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
        internal readonly DataValueHashMap<List<(int Index, Row Row)>>? SingleKeyTable;
        internal readonly CompositeKeyHashMap<List<(int Index, Row Row)>>? CompositeKeyTable;
        internal CombinedRowSchema? JoinSchema;
        internal Row? CachedNullBuild;
        /// <summary>Reusable scratch buffer for residual evaluation — allocated once on first use.</summary>
        internal DataValue[]? ResidualScratch;
        internal Row ResidualScratchRow;

        internal PartitionBuildTable(List<Row> buildRows, bool useSingleKey, int estimatedCapacity = 0)
        {
            BuildRows = buildRows;
            SingleKeyTable = useSingleKey ? new(estimatedCapacity) : null;
            CompositeKeyTable = useSingleKey ? null : new(estimatedCapacity);
        }
    }

    /// <summary>
    /// Builds an in-memory <see cref="PartitionBuildTable"/> from the given build-side rows,
    /// hashing each row into the appropriate lookup structure for fast probe-phase lookups.
    /// </summary>
    private async ValueTask<PartitionBuildTable> BuildPartitionTableAsync(
        IReadOnlyList<Row> buildRows,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        bool useSingleKey,
        CancellationToken cancellationToken)
    {
        bool buildKeyIsRight = !_flipped;
        List<Row> buildList = new(buildRows.Count);
        PartitionBuildTable table = new(buildList, useSingleKey, buildRows.Count);

        foreach (Row buildRow in buildRows)
        {
            int buildIndex = buildList.Count;
            buildList.Add(buildRow);

            if (useSingleKey)
            {
                DataValue key = await _evaluator.EvaluateAsync(buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left, buildRow, cancellationToken).ConfigureAwait(false);
                if (!key.IsNull)
                {
                    ref List<(int, Row)> bucket = ref table.SingleKeyTable!.GetOrAdd(key, out bool exists);
                    if (!exists)
                    {
                        bucket = new List<(int, Row)>();
                    }

                    bucket.Add((buildIndex, buildRow));
                }
            }
            else
            {
                DataValue[] parts = await EvaluateKeyPartsAsync(keyPairs, buildRow, rightSide: buildKeyIsRight, cancellationToken).ConfigureAwait(false);
                if (!HasNull(parts))
                {
                    ref List<(int, Row)> bucket = ref table.CompositeKeyTable!.GetOrAddDefault(parts, out bool exists);
                    if (!exists)
                    {
                        bucket = new List<(int, Row)>();
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
    private async IAsyncEnumerable<Row> ProbePartitionRowAsync(
        PartitionBuildTable table,
        Row probeRow,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        bool useSingleKey,
        bool isSemiJoin,
        Row? nullBuildTemplate,
        ExecutionContext context,
        DataValue[] keyScratch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Pool pool = context.Pool;
        LocalBufferPool bufferPool = context.LocalBufferPool;
        bool buildKeyIsRight = !_flipped;
        List<(int Index, Row Row)>? matches = null;

        if (useSingleKey)
        {
            DataValue probeKey = await _evaluator.EvaluateAsync(
                buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow, cancellationToken).ConfigureAwait(false);
            if (!probeKey.IsNull)
            {
                table.SingleKeyTable!.TryGetValue(probeKey, out matches);
            }
        }
        else
        {
            await EvaluateKeyPartsIntoAsync(keyPairs, probeRow, !buildKeyIsRight, keyScratch, cancellationToken).ConfigureAwait(false);
            if (!HasNull(keyScratch))
            {
                table.CompositeKeyTable!.TryGetValue(keyScratch.AsSpan(), out matches);
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
                    if (table.ResidualScratch is null)
                    {
                        (table.ResidualScratchRow, table.ResidualScratch) = table.JoinSchema.CreateReusableRow();
                    }

                    table.JoinSchema.CombineInto(leftRow, rightRow, table.ResidualScratch);

                    if (!await _evaluator.EvaluateAsBooleanAsync(_extraction.Residual, table.ResidualScratchRow, cancellationToken).ConfigureAwait(false))
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
                // probeRow's RawValues are owned by the upstream batch; copy into a
                // fresh pool-rented array so the yielded Row can be added to the
                // caller's outputBatch without aliasing.
                DataValue[] copy = pool.RentAndCopyDataValues(probeRow, context.Store, context.Store);
                yield return new Row(probeRow.ColumnLookup, copy);
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
                    DataValue[] copy = pool.RentAndCopyDataValues(probeRow, context.Store, context.Store);
                    yield return new Row(probeRow.ColumnLookup, copy);
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
        Pool pool = context.Pool;
        LocalBufferPool bufferPool = context.LocalBufferPool;
        RowBatch? outputBatch = null;
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;
        bool buildKeyIsRight = !_flipped;
        DataValue[] keyScratch = useSingleKey ? [] : new DataValue[keyPairs.Count];

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
            int buildRowEstimate = partition.IsBuildSpilled
                ? partition.TotalBuildRowCount
                : partition.InMemoryBuildRowCount;
            List<Row> buildRowList = new(Math.Max(buildRowEstimate, 4));
            // Cap the initial hash-map capacity to avoid >64 MB Entry[] pre-allocations
            // when buildRowEstimate is large due to hash skew. Both maps grow dynamically
            // via Resize() as rows are inserted, so this is only an initial hint.
            int initialHashCapacity = Math.Min(buildRowEstimate, 1 << 16);
            DataValueHashMap<List<(int Index, Row Row)>>? singleKeyTable =
                useSingleKey ? new(initialHashCapacity) : null;
            CompositeKeyHashMap<List<(int Index, Row Row)>>? compositeKeyTable =
                useSingleKey ? null : new(initialHashCapacity);

            long buildSizeEstimate = 0;
            MemoryEstimator partitionEstimator = new();
            bool partitionBudgetExceeded = false;

            // Load build rows and build the hash table simultaneously.
            // Once the memory estimate crosses the budget, hash-table construction is
            // skipped for remaining rows — they are appended to buildRowList only.
            // This avoids doubling the partition footprint with a hash table that
            // would immediately be discarded on re-partition, cutting peak memory
            // roughly in half compared to the previous post-loop check.
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

                if (partitionBudgetExceeded)
                {
                    // Already over budget — drain remaining rows into the list without
                    // building hash table entries; they will be passed to re-partitioning.
                    continue;
                }

                if (buildSizeEstimate > _memoryBudgetBytes && recursionDepth < MaxRecursionDepth)
                {
                    partitionBudgetExceeded = true;
                    continue;
                }

                if (buildSizeEstimate > (long)(_memoryBudgetBytes * MemoryEstimator.EscalationThreshold))
                {
                    partitionEstimator.EscalateToEveryRow();
                }

                if (useSingleKey)
                {
                    DataValue keyValue = await _evaluator.EvaluateAsync(
                        buildKeyIsRight ? keyPairs[0].Right : keyPairs[0].Left, buildRow, context.CancellationToken).ConfigureAwait(false);
                    if (!keyValue.IsNull)
                    {
                        ref List<(int, Row)> bucket = ref singleKeyTable!.GetOrAdd(keyValue, out bool exists);
                        if (!exists)
                        {
                            bucket = new List<(int, Row)>();
                        }

                        bucket.Add((buildIndex, buildRow));
                    }
                }
                else
                {
                    DataValue[] parts = await EvaluateKeyPartsAsync(keyPairs, buildRow, rightSide: buildKeyIsRight, context.CancellationToken).ConfigureAwait(false);
                    if (!HasNull(parts))
                    {
                        ref List<(int, Row)> bucket = ref compositeKeyTable!.GetOrAddDefault(parts, out bool exists);
                        if (!exists)
                        {
                            bucket = new List<(int, Row)>();
                        }

                        bucket.Add((buildIndex, buildRow));
                    }
                }
            }

            // If partition exceeded budget during load, re-partition recursively.
            // Null the partial hash tables before recursing so they can be collected
            // during re-partitioning rather than pinned in the async state machine.
            if (partitionBudgetExceeded)
            {
                singleKeyTable = null;
                compositeKeyTable = null;

                if (outputBatch is not null)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }

                await foreach (RowBatch recursionBatch in RecursivelyRepartitionAsync(
                    buildRowList, probeRows, partition.RetentionArena, useSingleKey, recursionDepth + 1,
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
            DataValue[]? residualScratch = null;
            Row residualScratchRow = default;

            foreach (Row probeRow in probeRows)
            {
                bool hasMatch = false;
                List<(int Index, Row Row)>? matches = null;

                if (useSingleKey)
                {
                    DataValue probeKeyValue = await _evaluator.EvaluateAsync(
                        buildKeyIsRight ? keyPairs[0].Left : keyPairs[0].Right, probeRow, context.CancellationToken).ConfigureAwait(false);
                    if (!probeKeyValue.IsNull)
                    {
                        singleKeyTable!.TryGetValue(probeKeyValue, out matches);
                    }
                }
                else
                {
                    await EvaluateKeyPartsIntoAsync(keyPairs, probeRow, !buildKeyIsRight, keyScratch, context.CancellationToken).ConfigureAwait(false);
                    if (!HasNull(keyScratch))
                    {
                        compositeKeyTable!.TryGetValue(keyScratch.AsSpan(), out matches);
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
                            if (residualScratch is null)
                            {
                                (residualScratchRow, residualScratch) = schema.CreateReusableRow();
                            }

                            schema.CombineInto(leftRow, rightRow, residualScratch);
                            if (!await _evaluator.EvaluateAsBooleanAsync(_extraction.Residual, residualScratchRow, context.CancellationToken).ConfigureAwait(false))
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

                        outputBatch ??= context.RentRowBatch(schema!.ColumnLookup);
                        outputBatch.Add(schema!.CombinePooledValues(leftRow, rightRow, bufferPool));
                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }
                }

                if (isSemiJoin)
                {
                    if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                        (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                    {
                        outputBatch ??= context.RentRowBatch(probeRow.ColumnLookup);
                        outputBatch.Add(pool.RentAndCopyDataValues(probeRow, context.Store, context.Store));
                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }

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
                        outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                        outputBatch.Add(schema.CombinePooledValues(leftRow, rightRow, bufferPool));
                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }
                    else
                    {
                        outputBatch ??= context.RentRowBatch(probeRow.ColumnLookup);
                        outputBatch.Add(pool.RentAndCopyDataValues(probeRow, context.Store, context.Store));
                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }
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
                            outputBatch ??= context.RentRowBatch(buildUnmatchedSchema.ColumnLookup);
                            outputBatch.Add(buildUnmatchedSchema.CombinePooledValues(leftRow, rightRow, bufferPool));
                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                        else
                        {
                            outputBatch ??= context.RentRowBatch(buildRowList[index].ColumnLookup);
                            outputBatch.Add(pool.RentAndCopyDataValues(buildRowList[index], context.Store, context.Store));
                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                }
            }
        }

        if (outputBatch is not null)
        {
            RowBatch toYield = outputBatch;
            outputBatch = null;
            yield return toYield;
        }
    }

    private async IAsyncEnumerable<RowBatch> RecursivelyRepartitionAsync(
        List<Row> buildRows,
        IEnumerable<Row> probeRows,
        Arena? sourceArena,
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
            subPartitions[index] = new SpillPartition(subSpillDir, index, context.Pool, context, pool: context.LocalBufferPool);
        }

        DataValue[] keyScratch = useSingleKey ? [] : new DataValue[keyPairs.Count];

        try
        {
            // Re-partition build rows with shifted hash bits.
            MemoryEstimator estimator = new();

            foreach (Row buildRow in buildRows)
            {
                int partitionIndex = await AssignPartitionAsync(buildRow, keyPairs, useSingleKey, subPartitionCount, recursionDepth, rightSide: buildKeyIsRight, keyScratch, context.CancellationToken).ConfigureAwait(false);
                subPartitions[partitionIndex].AddBuildRow(buildRow, sourceArena);

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
                int partitionIndex = await AssignPartitionAsync(probeRow, keyPairs, useSingleKey, subPartitionCount, recursionDepth, rightSide: !buildKeyIsRight, keyScratch, context.CancellationToken).ConfigureAwait(false);
                SpillPartition partition = subPartitions[partitionIndex];

                if (partition.IsBuildSpilled && !partition.IsProbeSpilled)
                {
                    partition.SpillProbeToDisk();
                }

                partition.AddProbeRow(probeRow, sourceArena);
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

    private async ValueTask<int> AssignPartitionAsync(
        Row row,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        bool useSingleKey,
        int partitionCount,
        int recursionDepth,
        bool rightSide,
        DataValue[] keyScratch,
        CancellationToken cancellationToken)
    {
        int hash;

        if (useSingleKey)
        {
            Expression keyExpression = rightSide ? keyPairs[0].Right : keyPairs[0].Left;
            DataValue keyValue = await _evaluator.EvaluateAsync(keyExpression, row, cancellationToken).ConfigureAwait(false);
            if (keyValue.IsNull)
            {
                // Null keys go to partition 0 (they won't match anything).
                return 0;
            }

            hash = keyValue.GetHashCode();
        }
        else
        {
            await EvaluateKeyPartsIntoAsync(keyPairs, row, rightSide, keyScratch, cancellationToken).ConfigureAwait(false);
            if (HasNull(keyScratch))
            {
                return 0;
            }

            CompositeKey compositeKey = new(keyScratch);
            hash = compositeKey.GetHashCode();
        }

        // Mix the hash before extracting partition bits. Without mixing, small
        // sequential integers (e.g. order_id = 1..65535) have hash == value,
        // so (uint)hash >> 16 == 0 for all of them, routing every row to partition 0.
        // The multiplier 0x45d9f3b (Wang's integer hash) avalanches all input
        // bits into the full 32-bit output, giving uniform partition assignments.
        uint mixed = (uint)hash;
        mixed ^= mixed >> 16;
        mixed *= 0x45d9f3b;
        mixed ^= mixed >> 16;
        // Use a different 8-bit window at each recursion depth so that rows
        // which collide at depth N are spread across sub-partitions at depth N+1.
        return (int)((mixed >> (recursionDepth * 8)) % (uint)partitionCount);
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

    private SpillPartition[] CreatePartitions(int count, Pool arenaPool, LocalBufferPool pool, ExecutionContext context)
    {
        int perPartitionEstimate = _estimatedBuildRows.HasValue
            ? (int)Math.Min(_estimatedBuildRows.Value / count, int.MaxValue)
            : 0;

        SpillPartition[] partitions = new SpillPartition[count];
        for (int index = 0; index < count; index++)
        {
            partitions[index] = new SpillPartition(_spillDirectory, index, arenaPool, context, perPartitionEstimate, pool);
        }

        return partitions;
    }

    /// <summary>
    /// Allocates and returns a new key-parts array. Use only when the array must
    /// be stored (e.g. as a dictionary key in the build-side hash table).
    /// </summary>
    private async ValueTask<DataValue[]> EvaluateKeyPartsAsync(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Row row,
        bool rightSide,
        CancellationToken cancellationToken)
    {
        DataValue[] parts = new DataValue[keyPairs.Count];
        await EvaluateKeyPartsIntoAsync(keyPairs, row, rightSide, parts, cancellationToken).ConfigureAwait(false);
        return parts;
    }

    /// <summary>
    /// Fills a caller-provided scratch buffer with evaluated key parts.
    /// Avoids per-row allocation when the array is only used transiently
    /// (probing, hashing, null checks).
    /// </summary>
    private async ValueTask EvaluateKeyPartsIntoAsync(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Row row,
        bool rightSide,
        DataValue[] scratch,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < keyPairs.Count; index++)
        {
            Expression expression = rightSide ? keyPairs[index].Right : keyPairs[index].Left;
            scratch[index] = await _evaluator.EvaluateAsync(expression, row, cancellationToken).ConfigureAwait(false);
        }
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
    /// Reuses the template's <see cref="ColumnLookup"/> so the call doesn't pay
    /// for a fresh dictionary per null pad.
    /// </summary>
    private static Row CreateNullRow(Row template)
    {
        DataValue[] values = new DataValue[template.FieldCount];

        for (int index = 0; index < template.FieldCount; index++)
        {
            values[index] = DataValue.Null(template[index].Kind);
        }

        return new Row(template.ColumnLookup, values);
    }
}
