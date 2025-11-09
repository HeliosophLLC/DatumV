using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes set operations (UNION, INTERSECT, EXCEPT) over two input operator branches.
/// Supports both ALL (multiset) and DISTINCT (set) semantics for each operation type.
/// <para>
/// <strong>UNION ALL</strong> concatenates both streams without deduplication.
/// <strong>UNION DISTINCT</strong> concatenates with a hash-set dedup; once the budget
/// is exceeded, post-spill rows route to a hash-partitioned <see cref="SpillReaderWriter"/>
/// and the in-memory set stops growing.
/// </para>
/// <para>
/// <strong>INTERSECT</strong> and <strong>EXCEPT</strong> materialise the right branch
/// into a hash structure, then probe with rows from the left branch. Distinct variants
/// use a hash set + emit-dedup set; ALL variants use a counted multiset and emit per
/// occurrence. When the budget is exceeded, both sides spill to per-partition files via
/// paired <see cref="SpillReaderWriter"/> instances (partition-aligned through the same
/// hash function) and drain partition-by-partition: each partition's local right state
/// is built from the in-memory subset whose hash routes there plus the spilled right
/// rows for that partition, then probed against the spilled left rows.
/// </para>
/// <para>
/// All output rows are stabilised into a long-lived <c>hashSetArena</c> rented from the
/// pool, so emitted batches resolve correctly after their source input batches return.
/// Per-row pool rentals (output <see cref="DataValue"/>[]s, partition buffers, the
/// <c>compositeKeyScratch</c>) are returned in each iterator's <c>finally</c>, keeping
/// <see cref="PoolBacking.ArenaRentCount"/> / <see cref="PoolBacking.RowBatchRentCount"/>
/// / <see cref="PoolBacking.DataValueArrayRentCount"/> balanced for clean leak detection.
/// </para>
/// </summary>
internal sealed class SetOperationOperator : IQueryOperator, IDisposable
{
    /// <summary>Number of hash partitions used when spilling to disk.</summary>
    private const int SpillPartitionCount = 64;

    private readonly IQueryOperator _left;
    private readonly IQueryOperator _right;
    private readonly SetOperationType _operationType;
    private readonly bool _all;

    /// <summary>
    /// Creates a new set operation operator combining two input branches.
    /// </summary>
    /// <param name="left">The left (first) input operator.</param>
    /// <param name="right">The right (second) input operator.</param>
    /// <param name="operationType">The type of set operation (Union, Intersect, or Except).</param>
    /// <param name="all">Whether to use ALL (multiset) semantics, preserving duplicates.</param>
    public SetOperationOperator(
        IQueryOperator left,
        IQueryOperator right,
        SetOperationType operationType,
        bool all)
    {
        _left = left;
        _right = right;
        _operationType = operationType;
        _all = all;
    }

    /// <summary>The left input operator.</summary>
    public IQueryOperator Left => _left;

    /// <summary>The right input operator.</summary>
    public IQueryOperator Right => _right;

    /// <summary>The type of set operation.</summary>
    public SetOperationType OperationType => _operationType;

    /// <summary>Whether ALL (multiset) semantics are used.</summary>
    public bool All => _all;

    /// <summary>
    /// Number of rows emitted from the drain phase of a spilled UNION DISTINCT.
    /// Test-only observability: when zero after a query that exceeded its budget, the
    /// spill machinery is dead code (every row was already emitted from the in-memory
    /// path). When non-zero, drain is doing real work.
    /// </summary>
    internal long DrainEmittedRowCount { get; private set; }

    /// <summary>
    /// Set to <see langword="true"/> the first time any branch crosses its memory budget
    /// and constructs a <see cref="SpillReaderWriter"/>. Test-only observability: lets
    /// spill tests assert that the spill code path actually executed (rather than the
    /// budget being silently larger than the test data).
    /// </summary>
    internal bool SpillingTriggered { get; private set; }

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        string operationName = _operationType switch
        {
            SetOperationType.Union => _all ? "Union All" : "Union",
            SetOperationType.Intersect => _all ? "Intersect All" : "Intersect",
            SetOperationType.Except => _all ? "Except All" : "Except",
            _ => _operationType.ToString(),
        };

        return new OperatorPlanDescription(operationName)
        {
            Children = [(Left, "left"), (Right, "right")],
        };
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        return (_operationType, _all) switch
        {
            (SetOperationType.Union, true) => ExecuteUnionAllAsync(context),
            (SetOperationType.Union, false) => ExecuteUnionDistinctAsync(context),
            (SetOperationType.Intersect, true) => ExecuteIntersectAllAsync(context),
            (SetOperationType.Intersect, false) => ExecuteIntersectDistinctAsync(context),
            (SetOperationType.Except, true) => ExecuteExceptAllAsync(context),
            (SetOperationType.Except, false) => ExecuteExceptDistinctAsync(context),
            _ => throw new InvalidOperationException(
                $"Unknown set operation: {_operationType} (all={_all})."),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Currently a no-op. Spill resources (per-side <see cref="SpillReaderWriter"/>
    /// instances and their temp directories / file-backed arenas) are owned by each
    /// iterator and disposed in its <c>finally</c> block, so consumer-driven dispose
    /// (e.g. mid-iteration break) flows through that path. Kept on the type so
    /// <c>using</c> patterns and the <see cref="IDisposable"/> contract continue to
    /// work transparently.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <summary>
    /// UNION ALL: concatenates left then right without deduplication.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteUnionAllAsync(ExecutionContext context)
    {
        await foreach (RowBatch batch in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }

        await foreach (RowBatch batch in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
    }

    /// <summary>
    /// UNION DISTINCT: concatenates both streams with hash-based deduplication,
    /// spilling to hash-partitioned disk files when the memory budget is exceeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two-phase dedup.</strong> Pre-spill rows are deduplicated against an
    /// in-memory hash set and emitted immediately. Once the budget is first exceeded,
    /// every subsequent row is routed to its hash partition's spill buffer (no in-memory
    /// set update, no immediate emit) — this keeps the in-memory set bounded at the size
    /// it was when spill triggered. The drain phase then replays each partition,
    /// deduplicating against a partition-local set seeded from the subset of in-memory
    /// keys whose hash routes to that partition.
    /// </para>
    /// <para>
    /// Hash set keys are added raw (no stabilization). Single-column lookups copy the
    /// <see cref="DataValue"/> struct (inline values are self-contained; arena-backed
    /// strings keep their cached <see cref="DataValue.RawContentHash"/> so lookups stay
    /// content-stable across recycled arenas). Composite lookups go through
    /// <see cref="HashSet{T}"/>.AlternateLookup{ReadOnlySpan{DataValue}}, which only
    /// allocates a <see cref="CompositeKey"/> on insert.
    /// </para>
    /// </remarks>
    private async IAsyncEnumerable<RowBatch> ExecuteUnionDistinctAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        // Long-lived arena for output-row stabilization. Lazily rented on first non-empty
        // batch (we need the schema). Output batches share this arena so consumers can
        // resolve arena-backed values after the input batches have been returned.
        Arena? hashSetArena = null;

        // Hash-partitioned spiller — replaces the inline BinaryWriter[] arrays. Lazily
        // constructed when the budget is first exceeded.
        SpillReaderWriter? spiller = null;

        // Per-partition row buffers used while spilling (RowBatch.Count grows up to
        // BatchSize, then flushed via spiller.Write(buffer, partition)). Lazily allocated
        // when spill begins.
        RowBatch?[]? partitionBuffers = null;

        // Single-column dedup uses HashSet<DataValue> (struct-copy keys); multi-column uses
        // HashSet<CompositeKey> with the AlternateLookup<ReadOnlySpan<DataValue>> probe path
        // (allocates only on insert, via Create(span) -> span.ToArray()). compositeKeyScratch
        // is a reusable per-probe scratch buffer.
        HashSet<DataValue>? singleKeySet = null;
        HashSet<CompositeKey>? compositeKeySet = null;
        HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> compositeKeyLookup = default;
        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;
        ColumnLookup? schema = null;
        SpillingTriggered = false;     // reset for this execution; safe under re-iteration
        RowBatch? outputBatch = null;

        try
        {
            // Process both left and right through the same dedup logic.
            await foreach (RowBatch inputBatch in ConcatenateAsync(_left, _right, context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && inputBatch.Count > 0)
                    {
                        schema = inputBatch.ColumnLookup;
                        hashSetArena = pool.RentArena();
                    }

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row row = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                            if (columnCount == 1)
                            {
                                singleKeySet = new HashSet<DataValue>();
                            }
                            else
                            {
                                compositeKeySet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                                compositeKeyLookup = compositeKeySet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                                compositeKeyScratch = pool.RentDataValues(columnCount);
                            }
                        }

                        // Once spilling, route to spill-only: skip in-memory set Add and
                        // skip the in-memory emit path. The drain phase reads from spill
                        // partitions and dedupes against a partition-local set seeded from
                        // the (now-bounded) in-memory set. This is what keeps the in-memory
                        // hash set bounded and makes the spill machinery actually do work.
                        if (SpillingTriggered)
                        {
                            int spillHashCode;
                            if (columnCount == 1)
                            {
                                spillHashCode = row[0].GetHashCode();
                            }
                            else
                            {
                                for (int index = 0; index < columnCount; index++)
                                {
                                    compositeKeyScratch![index] = row[index];
                                }
                                spillHashCode = CompositeKeyComparer.Instance.GetHashCode(
                                    compositeKeyScratch.AsSpan(0, columnCount));
                            }

                            int spillPartition = AssignPartition(spillHashCode);
                            partitionBuffers![spillPartition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(inputBatch, i, partitionBuffers[spillPartition]!);

                            if (partitionBuffers[spillPartition]!.IsFull)
                            {
                                spiller!.Write(partitionBuffers[spillPartition]!, spillPartition);
                                partitionBuffers[spillPartition] = null;
                            }
                            continue;
                        }

                        bool isNew;

                        if (columnCount == 1)
                        {
                            DataValue key = row[0];
                            isNew = singleKeySet!.Add(key);
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }

                            ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                            isNew = compositeKeyLookup.Add(keySpan);
                        }

                        if (isNew)
                        {
                            if (estimator is not null)
                            {
                                if (estimator.ShouldSample())
                                {
                                    estimator.RecordSample(row);
                                }

                                estimator.IncrementRowCount();
                                long estimatedMemory = estimator.EstimateTotalBytes();

                                if (estimatedMemory > memoryBudget!.Value)
                                {
                                    SpillingTriggered = true;
                                    // Initial-arena hint: half the budget, capped at int.MaxValue.
                                    // Spilled rows' payloads accumulate in spiller.ConsolidatedArena.
                                    int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                    spiller = new SpillReaderWriter(
                                        pool, schema!, context.SpillDirectory,
                                        initialArenaCapacity: hint,
                                        partitionCount: SpillPartitionCount);
                                    partitionBuffers = new RowBatch?[SpillPartitionCount];
                                    // The current row stays in the in-memory set + emitted
                                    // path; subsequent rows hit the `if (SpillingTriggered)`
                                    // branch above and go to spill only.
                                }
                                else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                                {
                                    estimator.EscalateToEveryRow();
                                }
                            }

                            outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(inputBatch, i, outputBatch);

                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(inputBatch);
                }
            }

            // Flush any remaining partition buffers before drain.
            if (partitionBuffers is not null && spiller is not null)
            {
                for (int p = 0; p < partitionBuffers.Length; p++)
                {
                    if (partitionBuffers[p] is not null)
                    {
                        spiller.Write(partitionBuffers[p]!, partition: p);
                        partitionBuffers[p] = null;
                    }
                }
            }

            // Drain spilled partitions. Each partition's local set is seeded with the subset
            // of in-memory keys whose hash routes here, then we replay the partition's spill
            // file and emit any spilled row whose key isn't already in the seed (i.e. wasn't
            // already emitted from the in-memory path) and isn't a duplicate of an earlier
            // row in the same partition.
            if (SpillingTriggered && spiller is not null)
            {
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spiller.RowsWrittenInPartition(partition) == 0) continue;

                    // Seed a partition-local hash structure with the subset of in-memory keys
                    // whose hash routes to this partition (same hash used by AssignPartition
                    // at write time, so the assignment is consistent).
                    HashSet<DataValue>? partitionSingleSet = null;
                    HashSet<CompositeKey>? partitionCompositeSet = null;
                    HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> partitionCompositeLookup = default;

                    if (columnCount == 1)
                    {
                        partitionSingleSet = new HashSet<DataValue>();
                        foreach (DataValue existingKey in singleKeySet!)
                        {
                            if (AssignPartition(existingKey.GetHashCode()) == partition)
                            {
                                partitionSingleSet.Add(existingKey);
                            }
                        }
                    }
                    else
                    {
                        partitionCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                        partitionCompositeLookup = partitionCompositeSet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                        foreach (CompositeKey existingKey in compositeKeySet!)
                        {
                            if (AssignPartition(existingKey.GetHashCode()) == partition)
                            {
                                partitionCompositeSet.Add(existingKey);
                            }
                        }
                    }

                    await foreach (RowBatch spilledBatch in spiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledBatch.Count; i++)
                            {
                                Row spilledRow = spilledBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                bool isNew;
                                if (columnCount == 1)
                                {
                                    isNew = partitionSingleSet!.Add(spilledRow[0]);
                                }
                                else
                                {
                                    for (int index = 0; index < columnCount; index++)
                                    {
                                        compositeKeyScratch![index] = spilledRow[index];
                                    }
                                    ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                                    isNew = partitionCompositeLookup.Add(keySpan);
                                }

                                if (isNew)
                                {
                                    outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                                    pool.RentAndCopyToOutput(spilledBatch, i, outputBatch);
                                    DrainEmittedRowCount++;

                                    if (outputBatch.IsFull)
                                    {
                                        RowBatch toYield = outputBatch;
                                        outputBatch = null;
                                        yield return toYield;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledBatch);
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
        finally
        {
            // Defensive cleanup. By here partitionBuffers should already be flushed and
            // outputBatch already yielded, but on early dispose / cancellation we may have
            // unyielded state. Return everything we own to the pool; Dispose the spiller
            // (deletes its temp dir + arena file); release hashSetArena.
            if (partitionBuffers is not null)
            {
                for (int p = 0; p < partitionBuffers.Length; p++)
                {
                    if (partitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(partitionBuffers[p]!);
                        partitionBuffers[p] = null;
                    }
                }
            }

            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (compositeKeyScratch is not null) pool.ReturnDataValues(compositeKeyScratch);
            spiller?.Dispose();
            if (hashSetArena is not null) pool.ReturnArena(hashSetArena);
        }
    }

    /// <summary>
    /// INTERSECT DISTINCT: materialises the right branch into a hash set, then
    /// emits left rows that appear in the set (each emitted at most once).
    /// When the memory budget is exceeded, both branches are spilled to
    /// hash-partitioned files and processed partition-by-partition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two-phase probe.</strong> Right side is materialised first into
    /// <c>rightSingleSet</c> / <c>rightCompositeSet</c>. If the budget is exceeded mid-
    /// materialisation, subsequent right rows go to the right <see cref="SpillReaderWriter"/>
    /// (partitioned by row hash) — the in-memory set stops growing, matching the
    /// fixed-up UNION DISTINCT semantics. Left is then drained: if a left row's
    /// partition has any spilled right rows, the left row is buffered into the left
    /// spiller's matching partition for the drain phase; otherwise it probes the
    /// in-memory right set directly. Drain processes each spilled partition by building
    /// a partition-local right set (in-memory keys + spilled keys for this partition)
    /// and probing left spilled rows against it.
    /// </para>
    /// <para>
    /// Per-partition emit dedup is correct because a given key always hashes to the
    /// same partition: partitions are either fully in-memory (Phase-2 dedup catches
    /// repeats) or fully spilled (Phase-3 per-partition dedup catches repeats), never
    /// both.
    /// </para>
    /// </remarks>
    private async IAsyncEnumerable<RowBatch> ExecuteIntersectDistinctAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        Arena? hashSetArena = null;
        SpillReaderWriter? rightSpiller = null;
        SpillReaderWriter? leftSpiller = null;
        RowBatch?[]? rightPartitionBuffers = null;
        RowBatch?[]? leftPartitionBuffers = null;

        HashSet<DataValue>? rightSingleSet = null;
        HashSet<CompositeKey>? rightCompositeSet = null;
        HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> rightCompositeLookup = default;

        HashSet<DataValue>? emittedSingleSet = null;
        HashSet<CompositeKey>? emittedCompositeSet = null;
        HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> emittedCompositeLookup = default;

        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;
        ColumnLookup? schema = null;
        SpillingTriggered = false;     // reset for this execution; safe under re-iteration
        RowBatch? outputBatch = null;

        try
        {
            // ───── Phase 1: materialise right ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0)
                    {
                        schema = rightBatch.ColumnLookup;
                        hashSetArena = pool.RentArena();
                    }

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                            if (columnCount == 1)
                            {
                                rightSingleSet = new HashSet<DataValue>();
                                emittedSingleSet = new HashSet<DataValue>();
                            }
                            else
                            {
                                rightCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                                rightCompositeLookup = rightCompositeSet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                                emittedCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                                emittedCompositeLookup = emittedCompositeSet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                                compositeKeyScratch = pool.RentDataValues(columnCount);
                            }
                        }

                        if (SpillingTriggered)
                        {
                            int spillHash;
                            if (columnCount == 1)
                            {
                                spillHash = row[0].GetHashCode();
                            }
                            else
                            {
                                for (int index = 0; index < columnCount; index++)
                                {
                                    compositeKeyScratch![index] = row[index];
                                }
                                spillHash = CompositeKeyComparer.Instance.GetHashCode(
                                    compositeKeyScratch.AsSpan(0, columnCount));
                            }

                            int partition = AssignPartition(spillHash);
                            rightPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(rightBatch, i, rightPartitionBuffers[partition]!);

                            if (rightPartitionBuffers[partition]!.IsFull)
                            {
                                rightSpiller!.Write(rightPartitionBuffers[partition]!, partition);
                                rightPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        if (columnCount == 1)
                        {
                            rightSingleSet!.Add(row[0]);
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }
                            rightCompositeLookup.Add(compositeKeyScratch.AsSpan(0, columnCount));
                        }

                        if (estimator is not null)
                        {
                            if (estimator.ShouldSample())
                            {
                                estimator.RecordSample(row);
                            }

                            estimator.IncrementRowCount();
                            long estimatedMemory = estimator.EstimateTotalBytes();

                            if (estimatedMemory > memoryBudget!.Value)
                            {
                                SpillingTriggered = true;
                                int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                rightSpiller = new SpillReaderWriter(
                                    pool, schema!, context.SpillDirectory,
                                    initialArenaCapacity: hint,
                                    partitionCount: SpillPartitionCount);
                                rightPartitionBuffers = new RowBatch?[SpillPartitionCount];
                                // Subsequent rows hit the SpillingTriggered branch above;
                                // the current row stays in the in-memory set.
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
                    pool.ReturnRowBatch(rightBatch);
                }
            }

            if (columnCount == -1)
            {
                yield break;
            }

            // Flush remaining right partition buffers so RowsWrittenInPartition is accurate
            // for the partitionIsSpilled probe below.
            if (rightPartitionBuffers is not null && rightSpiller is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        rightSpiller.Write(rightPartitionBuffers[p]!, p);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (SpillingTriggered)
            {
                int leftHint = (int)Math.Min(memoryBudget!.Value / 2, int.MaxValue);
                leftSpiller = new SpillReaderWriter(
                    pool, schema!, context.SpillDirectory,
                    initialArenaCapacity: leftHint,
                    partitionCount: SpillPartitionCount);
                leftPartitionBuffers = new RowBatch?[SpillPartitionCount];
            }

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode;
                        if (columnCount == 1)
                        {
                            hashCode = row[0].GetHashCode();
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }
                            hashCode = CompositeKeyComparer.Instance.GetHashCode(
                                compositeKeyScratch.AsSpan(0, columnCount));
                        }

                        int partition = AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller!.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, leftPartitionBuffers[partition]!);

                            if (leftPartitionBuffers[partition]!.IsFull)
                            {
                                leftSpiller!.Write(leftPartitionBuffers[partition]!, partition);
                                leftPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        bool matched;
                        bool isNew;
                        if (columnCount == 1)
                        {
                            DataValue key = row[0];
                            matched = rightSingleSet!.Contains(key);
                            if (!matched) continue;
                            isNew = emittedSingleSet!.Add(key);
                        }
                        else
                        {
                            ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                            matched = rightCompositeLookup.Contains(keySpan);
                            if (!matched) continue;
                            isNew = emittedCompositeLookup.Add(keySpan);
                        }

                        if (isNew)
                        {
                            outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, outputBatch);

                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered && rightSpiller is not null && leftSpiller is not null)
            {
                // Flush remaining left partition buffers.
                for (int p = 0; p < leftPartitionBuffers!.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        leftSpiller.Write(leftPartitionBuffers[p]!, p);
                        leftPartitionBuffers[p] = null;
                    }
                }

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    HashSet<DataValue>? partRightSingle = null;
                    HashSet<CompositeKey>? partRightComposite = null;
                    HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> partRightCompositeLookup = default;
                    HashSet<DataValue>? partEmittedSingle = null;
                    HashSet<CompositeKey>? partEmittedComposite = null;
                    HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> partEmittedCompositeLookup = default;

                    if (columnCount == 1)
                    {
                        partRightSingle = new HashSet<DataValue>();
                        partEmittedSingle = new HashSet<DataValue>();
                        // Seed with in-memory right keys that hash to this partition
                        // (rows admitted before spill triggered).
                        foreach (DataValue key in rightSingleSet!)
                        {
                            if (AssignPartition(key.GetHashCode()) == partition)
                            {
                                partRightSingle.Add(key);
                            }
                        }
                    }
                    else
                    {
                        partRightComposite = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                        partRightCompositeLookup = partRightComposite.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                        partEmittedComposite = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                        partEmittedCompositeLookup = partEmittedComposite.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                        foreach (CompositeKey key in rightCompositeSet!)
                        {
                            if (AssignPartition(key.GetHashCode()) == partition)
                            {
                                partRightComposite.Add(key);
                            }
                        }
                    }

                    // Add spilled right rows for this partition.
                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                Row row = spilledRightBatch[i];
                                if (columnCount == 1)
                                {
                                    partRightSingle!.Add(row[0]);
                                }
                                else
                                {
                                    for (int index = 0; index < columnCount; index++)
                                    {
                                        compositeKeyScratch![index] = row[index];
                                    }
                                    partRightCompositeLookup.Add(compositeKeyScratch.AsSpan(0, columnCount));
                                }
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    // Probe left spilled rows.
                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                bool matched;
                                bool isNew;
                                if (columnCount == 1)
                                {
                                    DataValue key = row[0];
                                    matched = partRightSingle!.Contains(key);
                                    if (!matched) continue;
                                    isNew = partEmittedSingle!.Add(key);
                                }
                                else
                                {
                                    for (int index = 0; index < columnCount; index++)
                                    {
                                        compositeKeyScratch![index] = row[index];
                                    }
                                    ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                                    matched = partRightCompositeLookup.Contains(keySpan);
                                    if (!matched) continue;
                                    isNew = partEmittedCompositeLookup.Add(keySpan);
                                }

                                if (isNew)
                                {
                                    outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                                    pool.RentAndCopyToOutput(spilledLeftBatch, i, outputBatch);

                                    if (outputBatch.IsFull)
                                    {
                                        RowBatch toYield = outputBatch;
                                        outputBatch = null;
                                        yield return toYield;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledLeftBatch);
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
        finally
        {
            if (rightPartitionBuffers is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(rightPartitionBuffers[p]!);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (leftPartitionBuffers is not null)
            {
                for (int p = 0; p < leftPartitionBuffers.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(leftPartitionBuffers[p]!);
                        leftPartitionBuffers[p] = null;
                    }
                }
            }

            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (compositeKeyScratch is not null) pool.ReturnDataValues(compositeKeyScratch);
            rightSpiller?.Dispose();
            leftSpiller?.Dispose();
            if (hashSetArena is not null) pool.ReturnArena(hashSetArena);
        }
    }

    /// <summary>
    /// INTERSECT ALL: materialises the right branch into a counted multiset, then
    /// emits left rows up to their count in the right branch.
    /// When the memory budget is exceeded, both branches are spilled to
    /// hash-partitioned files and processed partition-by-partition.
    /// </summary>
    /// <remarks>
    /// Multiset variant of <see cref="ExecuteIntersectDistinctAsync"/>. Right side is
    /// materialised into a count-per-key dictionary (with spill on budget exceed); left
    /// rows decrement and emit per occurrence — the multiset shrinks as matches are
    /// consumed, so a key with right-count 2 emits up to 2 left occurrences. No emit
    /// dedup (multiset semantics: every match emits).
    /// </remarks>
    private async IAsyncEnumerable<RowBatch> ExecuteIntersectAllAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        Arena? hashSetArena = null;
        SpillReaderWriter? rightSpiller = null;
        SpillReaderWriter? leftSpiller = null;
        RowBatch?[]? rightPartitionBuffers = null;
        RowBatch?[]? leftPartitionBuffers = null;

        Dictionary<DataValue, int>? rightSingleCounts = null;
        Dictionary<CompositeKey, int>? rightCompositeCounts = null;

        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;
        ColumnLookup? schema = null;
        SpillingTriggered = false;     // reset for this execution; safe under re-iteration
        RowBatch? outputBatch = null;

        try
        {
            // ───── Phase 1: materialise right counted multiset ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0)
                    {
                        schema = rightBatch.ColumnLookup;
                        hashSetArena = pool.RentArena();
                    }

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                            if (columnCount == 1)
                            {
                                rightSingleCounts = new Dictionary<DataValue, int>();
                            }
                            else
                            {
                                rightCompositeCounts = new Dictionary<CompositeKey, int>(CompositeKeyComparer.Instance);
                                compositeKeyScratch = pool.RentDataValues(columnCount);
                            }
                        }

                        if (SpillingTriggered)
                        {
                            int spillHash;
                            if (columnCount == 1)
                            {
                                spillHash = row[0].GetHashCode();
                            }
                            else
                            {
                                for (int index = 0; index < columnCount; index++)
                                {
                                    compositeKeyScratch![index] = row[index];
                                }
                                spillHash = CompositeKeyComparer.Instance.GetHashCode(
                                    compositeKeyScratch.AsSpan(0, columnCount));
                            }

                            int partition = AssignPartition(spillHash);
                            rightPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(rightBatch, i, rightPartitionBuffers[partition]!);

                            if (rightPartitionBuffers[partition]!.IsFull)
                            {
                                rightSpiller!.Write(rightPartitionBuffers[partition]!, partition);
                                rightPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        IncrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch);

                        if (estimator is not null)
                        {
                            if (estimator.ShouldSample())
                            {
                                estimator.RecordSample(row);
                            }

                            estimator.IncrementRowCount();
                            long estimatedMemory = estimator.EstimateTotalBytes();

                            if (estimatedMemory > memoryBudget!.Value)
                            {
                                SpillingTriggered = true;
                                int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                rightSpiller = new SpillReaderWriter(
                                    pool, schema!, context.SpillDirectory,
                                    initialArenaCapacity: hint,
                                    partitionCount: SpillPartitionCount);
                                rightPartitionBuffers = new RowBatch?[SpillPartitionCount];
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
                    pool.ReturnRowBatch(rightBatch);
                }
            }

            if (columnCount == -1)
            {
                yield break;
            }

            if (rightPartitionBuffers is not null && rightSpiller is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        rightSpiller.Write(rightPartitionBuffers[p]!, p);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (SpillingTriggered)
            {
                int leftHint = (int)Math.Min(memoryBudget!.Value / 2, int.MaxValue);
                leftSpiller = new SpillReaderWriter(
                    pool, schema!, context.SpillDirectory,
                    initialArenaCapacity: leftHint,
                    partitionCount: SpillPartitionCount);
                leftPartitionBuffers = new RowBatch?[SpillPartitionCount];
            }

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode;
                        if (columnCount == 1)
                        {
                            hashCode = row[0].GetHashCode();
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }
                            hashCode = CompositeKeyComparer.Instance.GetHashCode(
                                compositeKeyScratch.AsSpan(0, columnCount));
                        }

                        int partition = AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller!.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, leftPartitionBuffers[partition]!);

                            if (leftPartitionBuffers[partition]!.IsFull)
                            {
                                leftSpiller!.Write(leftPartitionBuffers[partition]!, partition);
                                leftPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        if (DecrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch))
                        {
                            outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, outputBatch);

                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered && rightSpiller is not null && leftSpiller is not null)
            {
                for (int p = 0; p < leftPartitionBuffers!.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        leftSpiller.Write(leftPartitionBuffers[p]!, p);
                        leftPartitionBuffers[p] = null;
                    }
                }

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    Dictionary<DataValue, int>? partRightSingle = columnCount == 1
                        ? new Dictionary<DataValue, int>() : null;
                    Dictionary<CompositeKey, int>? partRightComposite = columnCount != 1
                        ? new Dictionary<CompositeKey, int>(CompositeKeyComparer.Instance) : null;

                    // Seed with in-memory right counts for this partition (rows admitted
                    // before spill triggered).
                    AddInMemoryCountsForPartition(partition, columnCount,
                        rightSingleCounts, rightCompositeCounts, partRightSingle, partRightComposite);

                    // Add spilled right rows for this partition.
                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                IncrementCount(spilledRightBatch[i], columnCount,
                                    partRightSingle, partRightComposite, compositeKeyScratch);
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    // Probe left spilled rows; emit per match.
                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                if (DecrementCount(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch))
                                {
                                    outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                                    pool.RentAndCopyToOutput(spilledLeftBatch, i, outputBatch);

                                    if (outputBatch.IsFull)
                                    {
                                        RowBatch toYield = outputBatch;
                                        outputBatch = null;
                                        yield return toYield;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledLeftBatch);
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
        finally
        {
            if (rightPartitionBuffers is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(rightPartitionBuffers[p]!);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (leftPartitionBuffers is not null)
            {
                for (int p = 0; p < leftPartitionBuffers.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(leftPartitionBuffers[p]!);
                        leftPartitionBuffers[p] = null;
                    }
                }
            }

            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (compositeKeyScratch is not null) pool.ReturnDataValues(compositeKeyScratch);
            rightSpiller?.Dispose();
            leftSpiller?.Dispose();
            if (hashSetArena is not null) pool.ReturnArena(hashSetArena);
        }
    }

    /// <summary>
    /// EXCEPT DISTINCT: materialises the right branch into a hash set, then
    /// emits left rows that are not in the set (each emitted at most once).
    /// When the memory budget is exceeded, both branches are spilled to
    /// hash-partitioned files and processed partition-by-partition.
    /// </summary>
    /// <remarks>
    /// Mirror of <see cref="ExecuteIntersectDistinctAsync"/> with inverted match
    /// (<c>!Contains</c>). Empty-right is valid: left passes through, deduped.
    /// Schema / <c>columnCount</c> are lazy-initialised so the empty-right path can
    /// still establish them from the first left row.
    /// </remarks>
    private async IAsyncEnumerable<RowBatch> ExecuteExceptDistinctAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        Arena? hashSetArena = null;
        SpillReaderWriter? rightSpiller = null;
        SpillReaderWriter? leftSpiller = null;
        RowBatch?[]? rightPartitionBuffers = null;
        RowBatch?[]? leftPartitionBuffers = null;

        HashSet<DataValue>? rightSingleSet = null;
        HashSet<CompositeKey>? rightCompositeSet = null;
        HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> rightCompositeLookup = default;

        HashSet<DataValue>? emittedSingleSet = null;
        HashSet<CompositeKey>? emittedCompositeSet = null;
        HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> emittedCompositeLookup = default;

        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;
        ColumnLookup? schema = null;
        SpillingTriggered = false;
        RowBatch? outputBatch = null;

        try
        {
            // ───── Phase 1: materialise right ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0)
                    {
                        schema = rightBatch.ColumnLookup;
                        hashSetArena = pool.RentArena();
                    }

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                            InitDistinctSets(columnCount, ref rightSingleSet, ref rightCompositeSet,
                                ref rightCompositeLookup, ref emittedSingleSet, ref emittedCompositeSet,
                                ref emittedCompositeLookup, ref compositeKeyScratch, pool);
                        }

                        if (SpillingTriggered)
                        {
                            int spillHash;
                            if (columnCount == 1)
                            {
                                spillHash = row[0].GetHashCode();
                            }
                            else
                            {
                                for (int index = 0; index < columnCount; index++)
                                {
                                    compositeKeyScratch![index] = row[index];
                                }
                                spillHash = CompositeKeyComparer.Instance.GetHashCode(
                                    compositeKeyScratch.AsSpan(0, columnCount));
                            }

                            int partition = AssignPartition(spillHash);
                            rightPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(rightBatch, i, rightPartitionBuffers[partition]!);

                            if (rightPartitionBuffers[partition]!.IsFull)
                            {
                                rightSpiller!.Write(rightPartitionBuffers[partition]!, partition);
                                rightPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        if (columnCount == 1)
                        {
                            rightSingleSet!.Add(row[0]);
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }
                            rightCompositeLookup.Add(compositeKeyScratch.AsSpan(0, columnCount));
                        }

                        if (estimator is not null)
                        {
                            if (estimator.ShouldSample())
                            {
                                estimator.RecordSample(row);
                            }

                            estimator.IncrementRowCount();
                            long estimatedMemory = estimator.EstimateTotalBytes();

                            if (estimatedMemory > memoryBudget!.Value)
                            {
                                SpillingTriggered = true;
                                int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                rightSpiller = new SpillReaderWriter(
                                    pool, schema!, context.SpillDirectory,
                                    initialArenaCapacity: hint,
                                    partitionCount: SpillPartitionCount);
                                rightPartitionBuffers = new RowBatch?[SpillPartitionCount];
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
                    pool.ReturnRowBatch(rightBatch);
                }
            }

            if (rightPartitionBuffers is not null && rightSpiller is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        rightSpiller.Write(rightPartitionBuffers[p]!, p);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (SpillingTriggered)
            {
                int leftHint = (int)Math.Min(memoryBudget!.Value / 2, int.MaxValue);
                leftSpiller = new SpillReaderWriter(
                    pool, schema!, context.SpillDirectory,
                    initialArenaCapacity: leftHint,
                    partitionCount: SpillPartitionCount);
                leftPartitionBuffers = new RowBatch?[SpillPartitionCount];
            }

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    // Empty-right path: schema/columnCount/sets weren't initialised in
                    // Phase 1. Set them up from the first non-empty left batch.
                    if (schema is null && leftBatch.Count > 0)
                    {
                        schema = leftBatch.ColumnLookup;
                        hashSetArena = pool.RentArena();
                    }

                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                            InitDistinctSets(columnCount, ref rightSingleSet, ref rightCompositeSet,
                                ref rightCompositeLookup, ref emittedSingleSet, ref emittedCompositeSet,
                                ref emittedCompositeLookup, ref compositeKeyScratch, pool);
                        }

                        int hashCode;
                        if (columnCount == 1)
                        {
                            hashCode = row[0].GetHashCode();
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }
                            hashCode = CompositeKeyComparer.Instance.GetHashCode(
                                compositeKeyScratch.AsSpan(0, columnCount));
                        }

                        int partition = AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller!.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, leftPartitionBuffers[partition]!);

                            if (leftPartitionBuffers[partition]!.IsFull)
                            {
                                leftSpiller!.Write(leftPartitionBuffers[partition]!, partition);
                                leftPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        // Inverted match: emit only when NOT in the right set. ContainsRow
                        // returns false against null sets, so empty-right correctly passes
                        // every left row through (subject to emit dedup below).
                        bool inRight;
                        bool isNew;
                        if (columnCount == 1)
                        {
                            DataValue key = row[0];
                            inRight = rightSingleSet is not null && rightSingleSet.Contains(key);
                            if (inRight) continue;
                            isNew = emittedSingleSet!.Add(key);
                        }
                        else
                        {
                            ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                            inRight = rightCompositeSet is not null && rightCompositeLookup.Contains(keySpan);
                            if (inRight) continue;
                            isNew = emittedCompositeLookup.Add(keySpan);
                        }

                        if (isNew)
                        {
                            outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, outputBatch);

                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered && rightSpiller is not null && leftSpiller is not null)
            {
                for (int p = 0; p < leftPartitionBuffers!.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        leftSpiller.Write(leftPartitionBuffers[p]!, p);
                        leftPartitionBuffers[p] = null;
                    }
                }

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    HashSet<DataValue>? partRightSingle = null;
                    HashSet<CompositeKey>? partRightComposite = null;
                    HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> partRightCompositeLookup = default;
                    HashSet<DataValue>? partEmittedSingle = null;
                    HashSet<CompositeKey>? partEmittedComposite = null;
                    HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> partEmittedCompositeLookup = default;

                    if (columnCount == 1)
                    {
                        partRightSingle = new HashSet<DataValue>();
                        partEmittedSingle = new HashSet<DataValue>();
                        foreach (DataValue key in rightSingleSet!)
                        {
                            if (AssignPartition(key.GetHashCode()) == partition)
                            {
                                partRightSingle.Add(key);
                            }
                        }
                    }
                    else
                    {
                        partRightComposite = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                        partRightCompositeLookup = partRightComposite.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                        partEmittedComposite = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                        partEmittedCompositeLookup = partEmittedComposite.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                        foreach (CompositeKey key in rightCompositeSet!)
                        {
                            if (AssignPartition(key.GetHashCode()) == partition)
                            {
                                partRightComposite.Add(key);
                            }
                        }
                    }

                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                Row row = spilledRightBatch[i];
                                if (columnCount == 1)
                                {
                                    partRightSingle!.Add(row[0]);
                                }
                                else
                                {
                                    for (int index = 0; index < columnCount; index++)
                                    {
                                        compositeKeyScratch![index] = row[index];
                                    }
                                    partRightCompositeLookup.Add(compositeKeyScratch.AsSpan(0, columnCount));
                                }
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                bool inRight;
                                bool isNew;
                                if (columnCount == 1)
                                {
                                    DataValue key = row[0];
                                    inRight = partRightSingle!.Contains(key);
                                    if (inRight) continue;
                                    isNew = partEmittedSingle!.Add(key);
                                }
                                else
                                {
                                    for (int index = 0; index < columnCount; index++)
                                    {
                                        compositeKeyScratch![index] = row[index];
                                    }
                                    ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                                    inRight = partRightCompositeLookup.Contains(keySpan);
                                    if (inRight) continue;
                                    isNew = partEmittedCompositeLookup.Add(keySpan);
                                }

                                if (isNew)
                                {
                                    outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                                    pool.RentAndCopyToOutput(spilledLeftBatch, i, outputBatch);

                                    if (outputBatch.IsFull)
                                    {
                                        RowBatch toYield = outputBatch;
                                        outputBatch = null;
                                        yield return toYield;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledLeftBatch);
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
        finally
        {
            if (rightPartitionBuffers is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(rightPartitionBuffers[p]!);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (leftPartitionBuffers is not null)
            {
                for (int p = 0; p < leftPartitionBuffers.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(leftPartitionBuffers[p]!);
                        leftPartitionBuffers[p] = null;
                    }
                }
            }

            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (compositeKeyScratch is not null) pool.ReturnDataValues(compositeKeyScratch);
            rightSpiller?.Dispose();
            leftSpiller?.Dispose();
            if (hashSetArena is not null) pool.ReturnArena(hashSetArena);
        }
    }

    /// <summary>
    /// Initialises the right-side and emit hash structures (and the composite-key
    /// scratch buffer when needed) once <c>columnCount</c> is known. Used by the
    /// EXCEPT branches where <c>columnCount</c> may be set in either Phase 1 (right
    /// has rows) or Phase 2 (right is empty).
    /// </summary>
    private static void InitDistinctSets(
        int columnCount,
        ref HashSet<DataValue>? rightSingleSet,
        ref HashSet<CompositeKey>? rightCompositeSet,
        ref HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> rightCompositeLookup,
        ref HashSet<DataValue>? emittedSingleSet,
        ref HashSet<CompositeKey>? emittedCompositeSet,
        ref HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> emittedCompositeLookup,
        ref DataValue[]? compositeKeyScratch,
        Pool pool)
    {
        if (columnCount == 1)
        {
            rightSingleSet = new HashSet<DataValue>();
            emittedSingleSet = new HashSet<DataValue>();
        }
        else
        {
            rightCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
            rightCompositeLookup = rightCompositeSet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
            emittedCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
            emittedCompositeLookup = emittedCompositeSet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
            compositeKeyScratch ??= pool.RentDataValues(columnCount);
        }
    }

    /// <summary>
    /// EXCEPT ALL: materialises the right branch into a counted multiset, then
    /// emits left rows whose count exceeds their right-side count.
    /// When the memory budget is exceeded, both branches are spilled to
    /// hash-partitioned files and processed partition-by-partition.
    /// </summary>
    /// <remarks>
    /// Multiset variant of <see cref="ExecuteExceptDistinctAsync"/>. Right side becomes
    /// a count-per-key dictionary (with spill on budget exceed); left rows decrement
    /// (consumed by right) or emit (no remaining count). Empty-right is valid: every
    /// left row passes through.
    /// </remarks>
    private async IAsyncEnumerable<RowBatch> ExecuteExceptAllAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        Arena? hashSetArena = null;
        SpillReaderWriter? rightSpiller = null;
        SpillReaderWriter? leftSpiller = null;
        RowBatch?[]? rightPartitionBuffers = null;
        RowBatch?[]? leftPartitionBuffers = null;

        Dictionary<DataValue, int>? rightSingleCounts = null;
        Dictionary<CompositeKey, int>? rightCompositeCounts = null;

        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;
        ColumnLookup? schema = null;
        SpillingTriggered = false;
        RowBatch? outputBatch = null;

        try
        {
            // ───── Phase 1: materialise right counted multiset ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0)
                    {
                        schema = rightBatch.ColumnLookup;
                        hashSetArena = pool.RentArena();
                    }

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                            InitCountedMultisets(columnCount, ref rightSingleCounts,
                                ref rightCompositeCounts, ref compositeKeyScratch, pool);
                        }

                        if (SpillingTriggered)
                        {
                            int spillHash;
                            if (columnCount == 1)
                            {
                                spillHash = row[0].GetHashCode();
                            }
                            else
                            {
                                for (int index = 0; index < columnCount; index++)
                                {
                                    compositeKeyScratch![index] = row[index];
                                }
                                spillHash = CompositeKeyComparer.Instance.GetHashCode(
                                    compositeKeyScratch.AsSpan(0, columnCount));
                            }

                            int partition = AssignPartition(spillHash);
                            rightPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(rightBatch, i, rightPartitionBuffers[partition]!);

                            if (rightPartitionBuffers[partition]!.IsFull)
                            {
                                rightSpiller!.Write(rightPartitionBuffers[partition]!, partition);
                                rightPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        IncrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch);

                        if (estimator is not null)
                        {
                            if (estimator.ShouldSample())
                            {
                                estimator.RecordSample(row);
                            }

                            estimator.IncrementRowCount();
                            long estimatedMemory = estimator.EstimateTotalBytes();

                            if (estimatedMemory > memoryBudget!.Value)
                            {
                                SpillingTriggered = true;
                                int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                rightSpiller = new SpillReaderWriter(
                                    pool, schema!, context.SpillDirectory,
                                    initialArenaCapacity: hint,
                                    partitionCount: SpillPartitionCount);
                                rightPartitionBuffers = new RowBatch?[SpillPartitionCount];
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
                    pool.ReturnRowBatch(rightBatch);
                }
            }

            if (rightPartitionBuffers is not null && rightSpiller is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        rightSpiller.Write(rightPartitionBuffers[p]!, p);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (SpillingTriggered)
            {
                int leftHint = (int)Math.Min(memoryBudget!.Value / 2, int.MaxValue);
                leftSpiller = new SpillReaderWriter(
                    pool, schema!, context.SpillDirectory,
                    initialArenaCapacity: leftHint,
                    partitionCount: SpillPartitionCount);
                leftPartitionBuffers = new RowBatch?[SpillPartitionCount];
            }

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && leftBatch.Count > 0)
                    {
                        schema = leftBatch.ColumnLookup;
                        hashSetArena = pool.RentArena();
                    }

                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                            InitCountedMultisets(columnCount, ref rightSingleCounts,
                                ref rightCompositeCounts, ref compositeKeyScratch, pool);
                        }

                        int hashCode;
                        if (columnCount == 1)
                        {
                            hashCode = row[0].GetHashCode();
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }
                            hashCode = CompositeKeyComparer.Instance.GetHashCode(
                                compositeKeyScratch.AsSpan(0, columnCount));
                        }

                        int partition = AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller!.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftPartitionBuffers![partition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, leftPartitionBuffers[partition]!);

                            if (leftPartitionBuffers[partition]!.IsFull)
                            {
                                leftSpiller!.Write(leftPartitionBuffers[partition]!, partition);
                                leftPartitionBuffers[partition] = null;
                            }
                            continue;
                        }

                        // Inverted from INTERSECT ALL: emit when there is no remaining
                        // right count to consume the row. DecrementCount returns false
                        // against null sets, so empty-right correctly emits every left row.
                        if (!DecrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch))
                        {
                            outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                            pool.RentAndCopyToOutput(leftBatch, i, outputBatch);

                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered && rightSpiller is not null && leftSpiller is not null)
            {
                for (int p = 0; p < leftPartitionBuffers!.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        leftSpiller.Write(leftPartitionBuffers[p]!, p);
                        leftPartitionBuffers[p] = null;
                    }
                }

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    Dictionary<DataValue, int>? partRightSingle = columnCount == 1
                        ? new Dictionary<DataValue, int>() : null;
                    Dictionary<CompositeKey, int>? partRightComposite = columnCount != 1
                        ? new Dictionary<CompositeKey, int>(CompositeKeyComparer.Instance) : null;

                    AddInMemoryCountsForPartition(partition, columnCount,
                        rightSingleCounts, rightCompositeCounts, partRightSingle, partRightComposite);

                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                IncrementCount(spilledRightBatch[i], columnCount,
                                    partRightSingle, partRightComposite, compositeKeyScratch);
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                if (!DecrementCount(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch))
                                {
                                    outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, hashSetArena!);
                                    pool.RentAndCopyToOutput(spilledLeftBatch, i, outputBatch);

                                    if (outputBatch.IsFull)
                                    {
                                        RowBatch toYield = outputBatch;
                                        outputBatch = null;
                                        yield return toYield;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            pool.ReturnRowBatch(spilledLeftBatch);
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
        finally
        {
            if (rightPartitionBuffers is not null)
            {
                for (int p = 0; p < rightPartitionBuffers.Length; p++)
                {
                    if (rightPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(rightPartitionBuffers[p]!);
                        rightPartitionBuffers[p] = null;
                    }
                }
            }

            if (leftPartitionBuffers is not null)
            {
                for (int p = 0; p < leftPartitionBuffers.Length; p++)
                {
                    if (leftPartitionBuffers[p] is not null)
                    {
                        pool.ReturnRowBatch(leftPartitionBuffers[p]!);
                        leftPartitionBuffers[p] = null;
                    }
                }
            }

            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (compositeKeyScratch is not null) pool.ReturnDataValues(compositeKeyScratch);
            rightSpiller?.Dispose();
            leftSpiller?.Dispose();
            if (hashSetArena is not null) pool.ReturnArena(hashSetArena);
        }
    }

    /// <summary>
    /// Initialises the right-side counted multiset (and the composite-key scratch
    /// buffer when needed) once <c>columnCount</c> is known. Used by the EXCEPT ALL
    /// branch where <c>columnCount</c> may be set in either Phase 1 (right has rows)
    /// or Phase 2 (right is empty).
    /// </summary>
    private static void InitCountedMultisets(
        int columnCount,
        ref Dictionary<DataValue, int>? rightSingleCounts,
        ref Dictionary<CompositeKey, int>? rightCompositeCounts,
        ref DataValue[]? compositeKeyScratch,
        Pool pool)
    {
        if (columnCount == 1)
        {
            rightSingleCounts = new Dictionary<DataValue, int>();
        }
        else
        {
            rightCompositeCounts = new Dictionary<CompositeKey, int>(CompositeKeyComparer.Instance);
            compositeKeyScratch ??= pool.RentDataValues(columnCount);
        }
    }

    // ---------------------------------------------------------------
    //  Shared helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Fills the scratch buffer with the row's column values and returns a span over them.
    /// </summary>
    private static ReadOnlySpan<DataValue> FillScratch(Row row, DataValue[] scratch, int columnCount)
    {
        for (int index = 0; index < columnCount; index++)
        {
            scratch[index] = row[index];
        }

        return scratch.AsSpan(0, columnCount);
    }

    /// <summary>
    /// Copies in-memory right counted multiset entries that hash to the given
    /// partition into a partition-local dictionary for complete partition processing.
    /// </summary>
    private static void AddInMemoryCountsForPartition(
        int partition,
        int columnCount,
        Dictionary<DataValue, int>? inMemorySingle,
        Dictionary<CompositeKey, int>? inMemoryComposite,
        Dictionary<DataValue, int>? partitionSingle,
        Dictionary<CompositeKey, int>? partitionComposite)
    {
        if (columnCount == 1 && inMemorySingle is not null)
        {
            foreach (KeyValuePair<DataValue, int> entry in inMemorySingle)
            {
                if (AssignPartition(entry.Key.GetHashCode()) == partition)
                {
                    partitionSingle![entry.Key] = partitionSingle.GetValueOrDefault(entry.Key) + entry.Value;
                }
            }
        }
        else if (inMemoryComposite is not null)
        {
            foreach (KeyValuePair<CompositeKey, int> entry in inMemoryComposite)
            {
                if (AssignPartition(entry.Key.GetHashCode()) == partition)
                {
                    partitionComposite![entry.Key] = partitionComposite.GetValueOrDefault(entry.Key) + entry.Value;
                }
            }
        }
    }

    /// <summary>
    /// Concatenates two operator streams sequentially.
    /// </summary>
    private static async IAsyncEnumerable<RowBatch> ConcatenateAsync(
        IQueryOperator first,
        IQueryOperator second,
        ExecutionContext context)
    {
        await foreach (RowBatch batch in first.ExecuteAsync(context).ConfigureAwait(false))
        {
            yield return batch;
        }

        await foreach (RowBatch batch in second.ExecuteAsync(context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Increments the count for a row's key in the counted multiset.
    /// </summary>
    private static void IncrementCount(
        Row row,
        int columnCount,
        Dictionary<DataValue, int>? singleCounts,
        Dictionary<CompositeKey, int>? compositeCounts,
        DataValue[]? scratch)
    {
        if (columnCount == 1)
        {
            DataValue key = row[0];
            singleCounts![key] = singleCounts.GetValueOrDefault(key) + 1;
        }
        else
        {
            ReadOnlySpan<DataValue> keySpan = FillScratch(row, scratch!, columnCount);
            var lookup = compositeCounts!.GetAlternateLookup<ReadOnlySpan<DataValue>>();
            lookup.TryGetValue(keySpan, out int count);
            lookup[keySpan] = count + 1;
        }
    }

    /// <summary>
    /// Decrements the count for a row's key in the counted multiset.
    /// Returns true if the count was positive (row was present) and was decremented,
    /// false if the row was not present or already exhausted.
    /// </summary>
    private static bool DecrementCount(
        Row row,
        int columnCount,
        Dictionary<DataValue, int>? singleCounts,
        Dictionary<CompositeKey, int>? compositeCounts,
        DataValue[]? scratch)
    {
        if (singleCounts is null && compositeCounts is null)
        {
            return false;
        }

        if (columnCount == 1)
        {
            DataValue key = row[0];
            if (singleCounts!.TryGetValue(key, out int count) && count > 0)
            {
                singleCounts[key] = count - 1;
                return true;
            }

            return false;
        }

        ReadOnlySpan<DataValue> keySpan = FillScratch(row, scratch!, columnCount);
        var lookup = compositeCounts!.GetAlternateLookup<ReadOnlySpan<DataValue>>();
        if (lookup.TryGetValue(keySpan, out int compositeCount) && compositeCount > 0)
        {
            lookup[keySpan] = compositeCount - 1;
            return true;
        }

        return false;
    }

    /// <summary>Hash-partition routing: <paramref name="hashCode"/> mod
    /// <see cref="SpillPartitionCount"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AssignPartition(int hashCode)
    {
        return (int)((uint)hashCode % SpillPartitionCount);
    }
}
