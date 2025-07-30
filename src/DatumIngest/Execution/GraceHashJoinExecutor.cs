using System.Collections;
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

    /// <summary>Budget threshold fraction at which sampling switches to every row.</summary>
    private const double EscalationThreshold = 0.75;

    /// <summary>Maximum number of partitions (guards against degenerate hash distributions).</summary>
    private const int MaxPartitionCount = 256;

    /// <summary>Maximum recursion depth for re-partitioning.</summary>
    private const int MaxRecursionDepth = 3;

    /// <summary>
    /// Per-DataValue object overhead estimate in bytes (reference, kind, isNull, payload pointer, shape pointer).
    /// </summary>
    private const long DataValueOverheadBytes = 40;

    /// <summary>Per-dictionary entry overhead estimate in bytes (hash, key ref, value ref, next).</summary>
    private const long DictionaryEntryOverheadBytes = 48;

    private readonly JoinType _joinType;
    private readonly JoinKeyExtractionResult _extraction;
    private readonly long _memoryBudgetBytes;
    private readonly ExpressionEvaluator _evaluator;
    private readonly bool _nullSensitiveAntiSemi;
    private readonly string _spillDirectory;

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
    internal GraceHashJoinExecutor(
        JoinType joinType,
        JoinKeyExtractionResult extraction,
        long memoryBudgetBytes,
        ExpressionEvaluator evaluator,
        bool nullSensitiveAntiSemi = false)
    {
        _joinType = joinType;
        _extraction = extraction;
        _memoryBudgetBytes = memoryBudgetBytes;
        _evaluator = evaluator;
        _nullSensitiveAntiSemi = nullSensitiveAntiSemi;
        _spillDirectory = Path.Combine(Path.GetTempPath(), $"datum-join-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Executes the Grace hash join, streaming results as an async enumerable.
    /// </summary>
    /// <param name="leftOperator">The probe-side (left) operator.</param>
    /// <param name="rightOperator">The build-side (right) operator.</param>
    /// <param name="context">The execution context.</param>
    /// <returns>An async enumerable of joined rows.</returns>
    internal async IAsyncEnumerable<Row> ExecuteAsync(
        IQueryOperator leftOperator,
        IQueryOperator rightOperator,
        ExecutionContext context)
    {
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;
        bool useSingleKey = keyPairs.Count == 1;

        int partitionCount = ComputeInitialPartitionCount();
        SpillPartition[] partitions = CreatePartitions(partitionCount);

        try
        {
            // ── Phase 1: Partition the build side ──
            MemoryEstimator buildEstimator = new();
            Row? firstBuildRow = null;
            bool hasNullKey = false;

            await foreach (Row buildRow in rightOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                firstBuildRow ??= buildRow;

                // Track null keys for NOT IN null semantics.
                if (_nullSensitiveAntiSemi && !hasNullKey)
                {
                    if (useSingleKey)
                    {
                        if (_evaluator.Evaluate(keyPairs[0].Right, buildRow).IsNull)
                        {
                            hasNullKey = true;
                        }
                    }
                    else
                    {
                        DataValue[] parts = EvaluateKeyParts(keyPairs, buildRow, rightSide: true);
                        if (HasNull(parts))
                        {
                            hasNullKey = true;
                        }
                    }
                }

                int partitionIndex = AssignPartition(buildRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: true);
                partitions[partitionIndex].AddBuildRow(buildRow);

                // Memory monitoring with sampling.
                if (buildEstimator.ShouldSample())
                {
                    buildEstimator.RecordSample(buildRow);
                }

                buildEstimator.IncrementRowCount();
                long estimatedMemory = buildEstimator.EstimateTotalBytes();

                if (estimatedMemory > _memoryBudgetBytes)
                {
                    SpillLargestPartition(partitions);
                }
                else if (estimatedMemory > (long)(_memoryBudgetBytes * EscalationThreshold))
                {
                    buildEstimator.EscalateToEveryRow();
                }
            }

            // NOT IN null semantics: if any build-side key is NULL, the entire result is empty.
            if (_nullSensitiveAntiSemi && hasNullKey)
            {
                yield break;
            }

            // ── Phase 1b: Partition the probe side ──
            // Only rows whose corresponding build partition is spilled need to be spilled too.
            // In-memory partitions' probe rows are collected for later streaming.
            Row? firstProbeRow = null;
            await foreach (Row probeRow in leftOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                firstProbeRow ??= probeRow;
                int partitionIndex = AssignPartition(probeRow, keyPairs, useSingleKey, partitionCount, recursionDepth: 0, rightSide: false);
                SpillPartition partition = partitions[partitionIndex];

                if (partition.IsBuildSpilled)
                {
                    // Build side was spilled, so probe side must also be captured.
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

            // Pre-compute null templates from first-seen rows for outer join support.
            Row? nullBuildTemplate = firstBuildRow is not null ? CreateNullRow(firstBuildRow) : null;
            Row? nullProbeTemplate = firstProbeRow is not null ? CreateNullRow(firstProbeRow) : null;

            // ── Phase 2: Join each partition ──
            await foreach (Row row in JoinAllPartitionsAsync(
                partitions, useSingleKey, recursionDepth: 0, nullProbeTemplate, nullBuildTemplate, context)
                .ConfigureAwait(false))
            {
                yield return row;
            }
        }
        finally
        {
            foreach (SpillPartition partition in partitions)
            {
                partition.Dispose();
            }

            CleanupSpillDirectory();
        }
    }

    private async IAsyncEnumerable<Row> JoinAllPartitionsAsync(
        SpillPartition[] partitions,
        bool useSingleKey,
        int recursionDepth,
        Row? nullLeftTemplate,
        Row? nullRightTemplate,
        ExecutionContext context)
    {
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;

        for (int partitionIndex = 0; partitionIndex < partitions.Length; partitionIndex++)
        {
            SpillPartition partition = partitions[partitionIndex];

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
                    DataValue keyValue = _evaluator.Evaluate(keyPairs[0].Right, buildRow);
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
                    DataValue[] parts = EvaluateKeyParts(keyPairs, buildRow, rightSide: true);
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
                await foreach (Row row in RecursivelyRepartitionAsync(
                    buildRowList, probeRows, useSingleKey, recursionDepth + 1,
                    nullLeftTemplate, nullRightTemplate, context).ConfigureAwait(false))
                {
                    yield return row;
                }

                continue;
            }

            // Probe the hash table with probe-side rows.
            bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;
            bool needRightUnmatched = _joinType == JoinType.Right || _joinType == JoinType.FullOuter;
            BitArray? rightMatched = needRightUnmatched ? new BitArray(buildRowList.Count) : null;
            CombinedRowSchema? schema = null;
            Row? cachedNullRight = null;

            foreach (Row probeRow in probeRows)
            {
                bool hasMatch = false;
                List<(int Index, Row Row)>? matches = null;

                if (useSingleKey)
                {
                    DataValue leftKeyValue = _evaluator.Evaluate(keyPairs[0].Left, probeRow);
                    if (!leftKeyValue.IsNull)
                    {
                        singleKeyTable!.TryGetValue(leftKeyValue, out matches);
                    }
                }
                else
                {
                    DataValue[] parts = EvaluateKeyParts(keyPairs, probeRow, rightSide: false);
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
                        if (_extraction.Residual is not null)
                        {
                            schema ??= CombinedRowSchema.Build(probeRow, buildRow);
                            Row combinedRow = schema.Combine(probeRow, buildRow);
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

                        if (rightMatched is not null)
                        {
                            rightMatched[buildIndex] = true;
                        }

                        if (_extraction.Residual is null)
                        {
                            schema ??= CombinedRowSchema.Build(probeRow, buildRow);
                        }

                        yield return schema!.Combine(probeRow, buildRow);
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
                else if (!hasMatch && (_joinType == JoinType.Left || _joinType == JoinType.FullOuter))
                {
                    // Use a null template from this partition's build rows, or fall back to the global template.
                    Row? nullRight = cachedNullRight;
                    if (nullRight is null && buildRowList.Count > 0)
                    {
                        cachedNullRight = CreateNullRow(buildRowList[0]);
                        nullRight = cachedNullRight;
                    }

                    nullRight ??= nullRightTemplate;

                    if (nullRight is not null)
                    {
                        schema ??= CombinedRowSchema.Build(probeRow, nullRight);
                        yield return schema.Combine(probeRow, nullRight);
                    }
                    else
                    {
                        yield return probeRow;
                    }
                }
            }

            // Emit unmatched build rows for RIGHT/FULL OUTER.
            if (rightMatched is not null)
            {
                CombinedRowSchema? rightUnmatchedSchema = null;

                for (int index = 0; index < buildRowList.Count; index++)
                {
                    if (!rightMatched[index])
                    {
                        if (nullLeftTemplate is not null)
                        {
                            rightUnmatchedSchema ??= CombinedRowSchema.Build(nullLeftTemplate, buildRowList[index]);
                            yield return rightUnmatchedSchema.Combine(nullLeftTemplate, buildRowList[index]);
                        }
                        else
                        {
                            yield return buildRowList[index];
                        }
                    }
                }
            }
        }
    }

    private async IAsyncEnumerable<Row> RecursivelyRepartitionAsync(
        List<Row> buildRows,
        IEnumerable<Row> probeRows,
        bool useSingleKey,
        int recursionDepth,
        Row? nullLeftTemplate,
        Row? nullRightTemplate,
        ExecutionContext context)
    {
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = _extraction.KeyPairs;
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
                int partitionIndex = AssignPartition(buildRow, keyPairs, useSingleKey, subPartitionCount, recursionDepth, rightSide: true);
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
                int partitionIndex = AssignPartition(probeRow, keyPairs, useSingleKey, subPartitionCount, recursionDepth, rightSide: false);
                SpillPartition partition = subPartitions[partitionIndex];

                if (partition.IsBuildSpilled && !partition.IsProbeSpilled)
                {
                    partition.SpillProbeToDisk();
                }

                partition.AddProbeRow(probeRow);
            }

            await foreach (Row row in JoinAllPartitionsAsync(
                subPartitions, useSingleKey, recursionDepth, nullLeftTemplate, nullRightTemplate, context)
                .ConfigureAwait(false))
            {
                yield return row;
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

    private static void SpillLargestPartition(SpillPartition[] partitions)
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
        }
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
            values[index] = DataValue.Null(DataKind.Scalar);
        }

        return new Row(names, values);
    }

    /// <summary>
    /// Lightweight memory estimator that tracks row sizes via sampling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For fixed-width schemas (all Scalar, UInt8, Boolean, Date, DateTime, Time, Duration,
    /// Uuid), the row size is computed exactly from the first row and no further sampling
    /// is needed. For variable-width schemas (any String, Vector, Matrix, Tensor, Image,
    /// UInt8Array, or JsonValue column), every 64th row is sampled to maintain a running
    /// average. When the estimate crosses 75% of the budget, sampling escalates to every row.
    /// </para>
    /// <para>
    /// The per-row cost calculation sums payload sizes by kind (Scalar=4B, String=2×len,
    /// Vector/Matrix/Tensor=4×elementCount, Image/UInt8Array=len, etc.) plus ~40B object
    /// overhead per DataValue plus ~48B dictionary entry overhead. This is intentionally
    /// approximate — accuracy within 2× is sufficient for a spill trigger.
    /// </para>
    /// </remarks>
    private sealed class MemoryEstimator
    {
        private long _totalRowCount;
        private long _sampleCount;
        private long _totalSampledBytes;
        private bool _isFixedWidth;
        private bool _fixedWidthDetermined;
        private long _fixedRowSize;
        private int _sampleInterval = DefaultSampleInterval;

        /// <summary>Whether the next row should be sampled for size estimation.</summary>
        internal bool ShouldSample()
        {
            if (_fixedWidthDetermined && _isFixedWidth)
            {
                return false;
            }

            return _totalRowCount % _sampleInterval == 0;
        }

        /// <summary>Records a sampled row's estimated size.</summary>
        internal void RecordSample(Row row)
        {
            long rowBytes = EstimateRowBytes(row);

            if (!_fixedWidthDetermined)
            {
                _isFixedWidth = IsFixedWidthRow(row);
                _fixedWidthDetermined = true;

                if (_isFixedWidth)
                {
                    _fixedRowSize = rowBytes;
                    return;
                }
            }

            _sampleCount++;
            _totalSampledBytes += rowBytes;
        }

        /// <summary>Increments the total row count (called for every row, not just samples).</summary>
        internal void IncrementRowCount()
        {
            _totalRowCount++;
        }

        /// <summary>Switches to sampling every row (called when nearing budget threshold).</summary>
        internal void EscalateToEveryRow()
        {
            _sampleInterval = 1;
        }

        /// <summary>Estimates total memory consumed by all rows seen so far.</summary>
        internal long EstimateTotalBytes()
        {
            if (_totalRowCount == 0)
            {
                return 0;
            }

            if (_fixedWidthDetermined && _isFixedWidth)
            {
                return _totalRowCount * _fixedRowSize;
            }

            if (_sampleCount == 0)
            {
                return 0;
            }

            long averageRowBytes = _totalSampledBytes / _sampleCount;
            return _totalRowCount * averageRowBytes;
        }

        private static bool IsFixedWidthRow(Row row)
        {
            for (int index = 0; index < row.FieldCount; index++)
            {
                DataKind kind = row[index].Kind;
                if (kind is DataKind.String or DataKind.Vector or DataKind.Matrix
                    or DataKind.Tensor or DataKind.Image or DataKind.UInt8Array
                    or DataKind.JsonValue)
                {
                    return false;
                }
            }

            return true;
        }

        private static long EstimateRowBytes(Row row)
        {
            long bytes = 0;

            for (int index = 0; index < row.FieldCount; index++)
            {
                DataValue value = row[index];
                bytes += DataValueOverheadBytes;

                if (value.IsNull)
                {
                    continue;
                }

                bytes += value.Kind switch
                {
                    DataKind.Scalar => 4,
                    DataKind.UInt8 => 1,
                    DataKind.Boolean => 1,
                    DataKind.Date => 4,
                    DataKind.DateTime => 10,
                    DataKind.Time => 8,
                    DataKind.Duration => 8,
                    DataKind.Uuid => 16,
                    DataKind.String => 2L * value.AsString().Length,
                    DataKind.JsonValue => 2L * value.AsJsonValue().Length,
                    DataKind.Vector => 4L * value.AsVector().Length,
                    DataKind.Matrix => 4L * value.AsMatrix(out _, out _).Length,
                    DataKind.Tensor => 4L * value.AsTensor(out _).Length,
                    DataKind.UInt8Array => value.AsUInt8Array().Length,
                    DataKind.Image => value.AsImage().Length,
                    _ => 8,
                };
            }

            // Dictionary entry overhead for this row in the hash table.
            bytes += DictionaryEntryOverheadBytes;

            return bytes;
        }
    }
}
