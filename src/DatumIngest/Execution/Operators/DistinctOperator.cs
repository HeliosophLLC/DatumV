using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Streaming duplicate-elimination operator that yields only the first occurrence
/// of each distinct row from the source. Uses a <see cref="HashSet{T}"/> of
/// <see cref="DataValue"/> (single-column) or <see cref="CompositeKey"/> (multi-column)
/// to track seen rows.
/// <para>
/// When <see cref="ExecutionContext.MemoryBudgetBytes"/> is configured and the in-memory
/// set crosses the budget, post-spill rows route to a hash-partitioned
/// <see cref="SpillReaderWriter"/> instead of the in-memory set. The drain phase replays
/// each partition, dedupes against (a) the partition-local seen-set and (b) the in-memory
/// set (any row whose key is in-memory was already emitted), and emits the survivors.
/// This keeps the in-memory set bounded at the size it had when spill triggered.
/// </para>
/// <para>
/// Per-partition emit dedup is correct because a key always hashes to the same
/// partition, and in-memory keys are checked at probe time — a key is never both
/// in-memory and spilled-and-not-yet-emitted.
/// </para>
/// </summary>
internal sealed class DistinctOperator : IQueryOperator, IDisposable
{
    /// <summary>Number of spill partitions used when the memory budget is exceeded.</summary>
    private const int SpillPartitionCount = 64;

    private readonly IQueryOperator _source;

    /// <summary>
    /// Creates a new distinct operator over the given source.
    /// </summary>
    /// <param name="source">The upstream operator whose output rows are deduplicated.</param>
    public DistinctOperator(IQueryOperator source)
    {
        _source = source;
    }

    /// <summary>The upstream operator.</summary>
    public IQueryOperator Source => _source;

    /// <summary>
    /// Set to <see langword="true"/> the first time the in-memory set crosses the
    /// budget and the spiller is constructed. Test-only observability: lets spill
    /// tests assert that the spill code path actually executed.
    /// </summary>
    internal bool SpillingTriggered { get; private set; }

    /// <summary>
    /// Number of rows emitted from the drain phase. Test-only observability: when
    /// zero after a query that exceeded its budget, the spill machinery is dead code
    /// (every row was already emitted in-memory). When non-zero, drain is doing
    /// real work — proves the post-spill route-to-spill gate is wired correctly.
    /// </summary>
    internal long DrainEmittedRowCount { get; private set; }

    /// <summary>
    /// Number of rows routed to the spill partitions during the input loop. Test-only
    /// observability: a non-zero value proves real disk traffic happened under a
    /// tight budget, and a zero value under a generous budget proves the operator
    /// avoided spill correctly. Combined with <see cref="DrainEmittedRowCount"/>,
    /// the difference (<c>SpilledRowCount − DrainEmittedRowCount</c>) is the number
    /// of spilled rows that turned out to be duplicates of in-memory keys at drain
    /// time and were correctly dropped.
    /// </summary>
    internal long SpilledRowCount { get; private set; }

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Distinct")
        {
            Children = [(Source, null)],
            Warnings = ["materializes all unique rows in memory"],
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // DISTINCT must scan enough input rows to find N unique values — it cannot
        // predict how many input rows are needed. Strip RowLimit to prevent child
        // operators (e.g. JoinOperator) from picking strategies (index nested-loop)
        // that only pay off when the consumer needs few rows.
        if (context.RowLimit is not null)
        {
            context = new ExecutionContext(context) { RowLimit = null };
        }

        Pool pool = context.Pool;
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        // Under one-arena-per-query, dedup keys + spill partition buffers + output
        // batches all share `context.Store`. The QueryPlan owns this arena's
        // baseline reference, so it outlives the operator's hash-set lifetime.
        Arena hashSetArena = context.Store;
        SpillReaderWriter? spiller = null;
        RowBatch?[]? partitionBuffers = null;

        // Pool-bound composite-key comparer for the multi-column path; null on the
        // single-column path. Returned in finally / per-partition-end.
        CompositeKeyComparer? compositeComparer = null;

        HashSet<DataValue>? singleKeySet = null;
        HashSet<CompositeKey>? compositeKeySet = null;
        HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> compositeKeyLookup = default;
        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;
        ColumnLookup? schema = null;
        SpillingTriggered = false;     // reset for this execution; safe under re-iteration
        DrainEmittedRowCount = 0;
        SpilledRowCount = 0;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && inputBatch.Count > 0)
                    {
                        schema = inputBatch.ColumnLookup;
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
                                compositeComparer = CompositeKeyComparer.ForPool(pool);
                                compositeKeySet = new HashSet<CompositeKey>(compositeComparer);
                                compositeKeyLookup = compositeKeySet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                                compositeKeyScratch = pool.RentDataValues(columnCount);
                            }
                        }

                        // Once spilling, route to spill-only: skip the in-memory set Add
                        // and skip the in-memory emit path. The drain phase reads from
                        // spill partitions and dedupes against (partition-local set ∪
                        // in-memory set). Keeps the in-memory set bounded at the size
                        // it had when spill triggered.
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

                            int spillPartition = AssignPartition(spillHash);
                            partitionBuffers![spillPartition] ??= pool.RentRowBatch(
                                schema!, context.BatchSize, hashSetArena);
                            pool.RentAndCopyToOutput(inputBatch, i, partitionBuffers[spillPartition]!);
                            SpilledRowCount++;

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
                            isNew = singleKeySet!.Add(row[0]);
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }
                            isNew = compositeKeyLookup.Add(compositeKeyScratch.AsSpan(0, columnCount));
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
                                    int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                    spiller = new SpillReaderWriter(
                                        pool, schema!, context.SpillDirectory,
                                        initialArenaCapacity: hint,
                                        partitionCount: SpillPartitionCount);
                                    partitionBuffers = new RowBatch?[SpillPartitionCount];
                                    // The current row stays in the in-memory set + emitted
                                    // path; subsequent rows hit the SpillingTriggered branch
                                    // above and go to spill only.
                                }
                                else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                                {
                                    estimator.EscalateToEveryRow();
                                }
                            }

                            outputBatch ??= context.RentRowBatch(schema!);
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
                    context.ReturnRowBatch(inputBatch);
                }
            }

            // Flush remaining partition buffers before drain.
            if (partitionBuffers is not null && spiller is not null)
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

            // Drain spilled partitions: per partition, build a partition-local seen-set
            // (keys rented through compositeComparer for multi-col, or plain for single).
            // For each spilled row, skip if it's already in the in-memory set (already
            // emitted before spill) or already in the partition-local set (duplicate
            // within spilled rows). Otherwise emit + add to partition-local set.
            if (SpillingTriggered && spiller is not null)
            {
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spiller.RowsWrittenInPartition(partition) == 0) continue;

                    HashSet<DataValue>? partitionSingleSet = null;
                    HashSet<CompositeKey>? partitionCompositeSet = null;
                    HashSet<CompositeKey>.AlternateLookup<ReadOnlySpan<DataValue>> partitionCompositeLookup = default;

                    if (columnCount == 1)
                    {
                        partitionSingleSet = new HashSet<DataValue>();
                    }
                    else
                    {
                        partitionCompositeSet = new HashSet<CompositeKey>(compositeComparer!);
                        partitionCompositeLookup = partitionCompositeSet.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                    }

                    await foreach (RowBatch spilledBatch in spiller
                        .ReplayPartitionAsync(context, schema!, partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledBatch.Count; i++)
                            {
                                Row row = spilledBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                bool isNew;
                                if (columnCount == 1)
                                {
                                    DataValue key = row[0];
                                    if (singleKeySet!.Contains(key)) continue;
                                    isNew = partitionSingleSet!.Add(key);
                                }
                                else
                                {
                                    for (int index = 0; index < columnCount; index++)
                                    {
                                        compositeKeyScratch![index] = row[index];
                                    }
                                    ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                                    if (compositeKeyLookup.Contains(keySpan)) continue;
                                    isNew = partitionCompositeLookup.Add(keySpan);
                                }

                                if (isNew)
                                {
                                    outputBatch ??= context.RentRowBatch(schema!);
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
                            context.ReturnRowBatch(spilledBatch);
                        }
                    }

                    // Return partition-local rented keys before they go out of scope.
                    if (compositeComparer is not null && partitionCompositeSet is not null)
                    {
                        compositeComparer.ReturnPooledKeys(partitionCompositeSet);
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
            if (partitionBuffers is not null)
            {
                for (int p = 0; p < partitionBuffers.Length; p++)
                {
                    if (partitionBuffers[p] is not null)
                    {
                        context.ReturnRowBatch(partitionBuffers[p]!);
                        partitionBuffers[p] = null;
                    }
                }
            }

            if (outputBatch is not null) context.ReturnRowBatch(outputBatch);
            if (compositeKeyScratch is not null) pool.ReturnDataValues(compositeKeyScratch);
            if (compositeComparer is not null && compositeKeySet is not null)
            {
                compositeComparer.ReturnPooledKeys(compositeKeySet);
            }
            spiller?.Dispose();
            // No private-arena return: hash set + buffers live in context.Store, owned by QueryPlan.
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op. Spill resources (the <see cref="SpillReaderWriter"/> instance and
    /// its temp directory / file-backed arena) are owned by the iterator and
    /// disposed in its <c>finally</c> block, so consumer-driven dispose
    /// (mid-iteration break) flows through that path. Kept on the type so
    /// <c>using</c> patterns and the <see cref="IDisposable"/> contract continue
    /// to work transparently.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <summary>Hash-partition routing: <paramref name="hashCode"/> mod
    /// <see cref="SpillPartitionCount"/>.</summary>
    private static int AssignPartition(int hashCode)
    {
        return (int)((uint)hashCode % SpillPartitionCount);
    }
}
