using System.Runtime.CompilerServices;
using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes set operations (UNION, INTERSECT, EXCEPT) over two input operator branches.
/// Supports both ALL (multiset) and DISTINCT (set) semantics for each operation type.
/// <para>
/// <strong>UNION ALL</strong> concatenates both streams without deduplication.
/// <strong>UNION</strong> (distinct) concatenates and deduplicates using a hash set,
/// with spill-to-disk when the <see cref="ExecutionContext.MemoryBudgetBytes"/> is exceeded.
/// </para>
/// <para>
/// <strong>INTERSECT</strong> and <strong>EXCEPT</strong> materialise the right branch
/// into a hash structure, then probe with rows from the left branch.
/// ALL variants use counted multisets; distinct variants use simple hash sets.
/// When the memory budget is exceeded, rows are spilled to hash-partitioned disk files
/// and processed partition-by-partition in a drain phase.
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
    private string? _spillDirectory;

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
    public void Dispose()
    {
        CleanupSpillDirectory();
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
    /// spilling to disk when the memory budget is exceeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Known algorithm bug (Option A migration — preserved deliberately).</strong>
    /// New rows are added to the in-memory hash set unconditionally, even after spill has
    /// been triggered. This means the in-memory state grows unbounded and the spill files
    /// are duplicate insurance only — the drain phase's <c>partitionSet.Add(spilledKey)</c>
    /// returns <see langword="false"/> for every spilled row (already in the in-memory set
    /// from when isNew was first true), so drain emits nothing. The spill machinery is
    /// therefore dead code in this implementation. Multi-tenant memory safety is NOT
    /// delivered for UNION DISTINCT until this is fixed in a follow-up PR. The other four
    /// dual-side branches (Intersect/Except × Distinct/All) handle this correctly via an
    /// <c>if (spilling) route-to-spill-only / else update-in-memory</c> gate.
    /// </para>
    /// <para>
    /// <strong>What this PR migrates.</strong> Just the API surface: <see cref="Pool"/>
    /// instead of <see cref="LocalBufferPool"/>, <see cref="SpillReaderWriter"/> with
    /// <c>partitionCount: 64</c> instead of inline <see cref="BinaryWriter"/>[] arrays,
    /// stabilization of output rows into a long-lived <c>hashSetArena</c> so they survive
    /// the input batch return, null-before-yield iterator safety. Hash set keys are still
    /// added raw (no stabilization) — the existing code relies on
    /// <see cref="DataValue.GetHashCode"/>'s content-based hash for cached-hash strings,
    /// which makes <see cref="HashSet{T}"/>.Contains lookups robust to recycled arenas.
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
        bool spilling = false;
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
                                compositeKeyScratch = new DataValue[columnCount];
                            }
                        }

                        bool isNew;
                        int hashCode;

                        if (columnCount == 1)
                        {
                            DataValue key = row[0];
                            isNew = singleKeySet!.Add(key);
                            hashCode = key.GetHashCode();
                        }
                        else
                        {
                            for (int index = 0; index < columnCount; index++)
                            {
                                compositeKeyScratch![index] = row[index];
                            }

                            ReadOnlySpan<DataValue> keySpan = compositeKeyScratch.AsSpan(0, columnCount);
                            isNew = compositeKeyLookup.Add(keySpan);
                            hashCode = CompositeKeyComparer.Instance.GetHashCode(keySpan);
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
                                    if (!spilling)
                                    {
                                        spilling = true;
                                        // Initial-arena hint: half the budget, capped at int.MaxValue.
                                        // Spilled rows' payloads accumulate in spiller.ConsolidatedArena.
                                        int hint = (int)Math.Min(memoryBudget.Value / 2, int.MaxValue);
                                        spiller = new SpillReaderWriter(
                                            pool, schema!, context.SpillDirectory,
                                            initialArenaCapacity: hint,
                                            partitionCount: SpillPartitionCount);
                                        partitionBuffers = new RowBatch?[SpillPartitionCount];
                                    }

                                    // Buffer the row into its partition. Buffer's batch shares
                                    // hashSetArena (so stabilized values resolve until the
                                    // batch is handed to the spiller, which then re-stabilizes
                                    // into its own consolidated arena).
                                    int partition = AssignPartition(hashCode);
                                    partitionBuffers![partition] ??= pool.RentRowBatch(
                                        schema!, context.BatchSize, hashSetArena!);
                                    pool.RentAndCopyToOutput(inputBatch, i, partitionBuffers[partition]!);

                                    if (partitionBuffers[partition]!.IsFull)
                                    {
                                        spiller!.Write(partitionBuffers[partition]!, partition);
                                        partitionBuffers[partition] = null;
                                    }
                                }
                                else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                                {
                                    estimator.EscalateToEveryRow();
                                }
                            }

                            // Always emit (Option A: bug preserved — see method docstring).
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

            // Drain spilled partitions. NOTE (Option A): every spilled row's key was already
            // added to the in-memory hash set in the main loop, so partitionSet.Add returns
            // false for all of them — drain emits nothing. Preserved to keep behavior
            // identical to the pre-migration code; the bug fix in the follow-up PR makes
            // this loop actually do work.
            if (spilling && spiller is not null)
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
    private async IAsyncEnumerable<RowBatch> ExecuteIntersectDistinctAsync(ExecutionContext context)
    {
        HashSet<DataValue>? rightSingleSet = null;
        HashSet<CompositeKey>? rightCompositeSet = null;
        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;

        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        bool spilling = false;
        BinaryWriter?[]? rightSpillWriters = null;
        bool[]? rightSpillSchemaWritten = null;
        string[]? rightSpillPaths = null;
        string[]? spillSchemaNames = null;
        RowBatch? outputBatch = null;

        try
        {
            // Materialise the right branch.
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
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
                        }
                        else
                        {
                            rightCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                            compositeKeyScratch = new DataValue[columnCount];
                        }
                    }

                    if (spilling)
                    {
                        int hashCode = GetRowHashCode(row, columnCount);
                        WriteToSpillPartition(row, hashCode, rightSpillWriters!, rightSpillSchemaWritten!,
                            rightSpillPaths!, spillSchemaNames!);
                    }
                    else
                    {
                        AddRowToSet(row, columnCount, rightSingleSet, rightCompositeSet, compositeKeyScratch);

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
                                spilling = true;
                                EnsureSpillDirectory();
                                rightSpillWriters = new BinaryWriter[SpillPartitionCount];
                                rightSpillSchemaWritten = new bool[SpillPartitionCount];
                                rightSpillPaths = new string[SpillPartitionCount];
                                spillSchemaNames = CaptureSchemaNames(row);

                                if (ExecutionTracer.IsEnabled)
                                {
                                    ExecutionTracer.Write(
                                        $"INTERSECT DISTINCT right spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}");
                                }
                            }
                            else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                            {
                                estimator.EscalateToEveryRow();
                            }
                        }
                    }
                }
                rightBatch.Return();
            }

            if (columnCount == -1)
            {
                yield break;
            }

            if (!spilling)
            {
                // Fully in-memory path: probe left against right set.
                HashSet<DataValue>? emittedSingleSet = columnCount == 1 ? new() : null;
                HashSet<CompositeKey>? emittedCompositeSet = columnCount != 1
                    ? new(CompositeKeyComparer.Instance) : null;

                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (!ContainsRow(row, columnCount, rightSingleSet, rightCompositeSet, compositeKeyScratch))
                        {
                            continue;
                        }

                        bool isNew;
                        if (columnCount == 1)
                        {
                            isNew = emittedSingleSet!.Add(row[0]);
                        }
                        else
                        {
                            ReadOnlySpan<DataValue> keySpan = FillScratch(row, compositeKeyScratch!, columnCount);
                            isNew = emittedCompositeSet!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Add(keySpan);
                        }

                        if (isNew)
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                    leftBatch.Return();
                }
            }
            else
            {
                // Spilled path: partition left rows too, then process per partition.
                FlushSpillWriters(rightSpillWriters!);

                BinaryWriter?[] leftSpillWriters = new BinaryWriter[SpillPartitionCount];
                bool[] leftSpillSchemaWritten = new bool[SpillPartitionCount];
                string[] leftSpillPaths = new string[SpillPartitionCount];

                // Determine which partitions have spilled right data.
                HashSet<int> spilledPartitions = new();
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is not null)
                    {
                        spilledPartitions.Add(partition);
                    }
                }

                HashSet<DataValue>? emittedSingleSet = columnCount == 1 ? new() : null;
                HashSet<CompositeKey>? emittedCompositeSet = columnCount != 1
                    ? new(CompositeKeyComparer.Instance) : null;

                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode = GetRowHashCode(row, columnCount);
                        int partition = AssignPartition(hashCode);

                        if (spilledPartitions.Contains(partition))
                        {
                            // This partition was spilled — buffer left row for partition processing.
                            WriteToLeftSpillPartition(row, hashCode, leftSpillWriters,
                                leftSpillSchemaWritten, leftSpillPaths, spillSchemaNames!);
                        }
                        else
                        {
                            // Partition is fully in-memory — probe directly.
                            if (!ContainsRow(row, columnCount, rightSingleSet, rightCompositeSet, compositeKeyScratch))
                            {
                                continue;
                            }

                            bool isNew;
                            if (columnCount == 1)
                            {
                                isNew = emittedSingleSet!.Add(row[0]);
                            }
                            else
                            {
                                ReadOnlySpan<DataValue> keySpan = FillScratch(row, compositeKeyScratch!, columnCount);
                                isNew = emittedCompositeSet!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Add(keySpan);
                            }

                            if (isNew)
                            {
                                outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                                outputBatch.Add(row);

                                if (outputBatch.IsFull)
                                {
                                    yield return outputBatch;
                                    outputBatch = null;
                                }
                            }
                        }
                    }
                    leftBatch.Return();
                }

                FlushSpillWriters(leftSpillWriters);

                // Process each spilled partition.
                Dictionary<string, int>? schemaNameIndex = BuildSchemaNameIndex(spillSchemaNames!);

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is null)
                    {
                        continue;
                    }

                    // Load right partition into a local set.
                    HashSet<DataValue>? partRightSingle = columnCount == 1 ? new() : null;
                    HashSet<CompositeKey>? partRightComposite = columnCount != 1
                        ? new(CompositeKeyComparer.Instance) : null;

                    await foreach (Row row in ReadSpillPartitionAsync(
                        rightSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        AddRowToSet(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch);
                    }

                    // Also include in-memory rows for this partition.
                    AddInMemoryRowsForPartition(partition, columnCount,
                        rightSingleSet, rightCompositeSet, partRightSingle, partRightComposite);

                    // Probe left partition.
                    if (leftSpillPaths[partition] is null)
                    {
                        continue;
                    }

                    HashSet<DataValue>? partEmittedSingle = columnCount == 1 ? new() : null;
                    HashSet<CompositeKey>? partEmittedComposite = columnCount != 1
                        ? new(CompositeKeyComparer.Instance) : null;

                    await foreach (Row row in ReadSpillPartitionAsync(
                        leftSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        if (!ContainsRow(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch))
                        {
                            continue;
                        }

                        bool isNew;
                        if (columnCount == 1)
                        {
                            isNew = partEmittedSingle!.Add(row[0]);
                        }
                        else
                        {
                            ReadOnlySpan<DataValue> keySpan = FillScratch(row, compositeKeyScratch!, columnCount);
                            isNew = partEmittedComposite!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Add(keySpan);
                        }

                        if (isNew)
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                }

                CleanupSpillFiles(leftSpillWriters);
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
        finally
        {
            CleanupSpillFiles(rightSpillWriters);
        }
    }

    /// <summary>
    /// INTERSECT ALL: materialises the right branch into a counted multiset, then
    /// emits left rows up to their count in the right branch.
    /// When the memory budget is exceeded, both branches are spilled to
    /// hash-partitioned files and processed partition-by-partition.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteIntersectAllAsync(ExecutionContext context)
    {
        Dictionary<DataValue, int>? rightSingleCounts = null;
        Dictionary<CompositeKey, int>? rightCompositeCounts = null;
        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;

        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        bool spilling = false;
        BinaryWriter?[]? rightSpillWriters = null;
        bool[]? rightSpillSchemaWritten = null;
        string[]? rightSpillPaths = null;
        string[]? spillSchemaNames = null;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
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
                            compositeKeyScratch = new DataValue[columnCount];
                        }
                    }

                    if (spilling)
                    {
                        int hashCode = GetRowHashCode(row, columnCount);
                        WriteToSpillPartition(row, hashCode, rightSpillWriters!, rightSpillSchemaWritten!,
                            rightSpillPaths!, spillSchemaNames!);
                    }
                    else
                    {
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
                                spilling = true;
                                EnsureSpillDirectory();
                                rightSpillWriters = new BinaryWriter[SpillPartitionCount];
                                rightSpillSchemaWritten = new bool[SpillPartitionCount];
                                rightSpillPaths = new string[SpillPartitionCount];
                                spillSchemaNames = CaptureSchemaNames(row);

                                if (ExecutionTracer.IsEnabled)
                                {
                                    ExecutionTracer.Write(
                                        $"INTERSECT ALL right spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}");
                                }
                            }
                            else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                            {
                                estimator.EscalateToEveryRow();
                            }
                        }
                    }
                }
            }

            if (columnCount == -1)
            {
                yield break;
            }

            if (!spilling)
            {
                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (DecrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch))
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                }
            }
            else
            {
                FlushSpillWriters(rightSpillWriters!);

                BinaryWriter?[] leftSpillWriters = new BinaryWriter[SpillPartitionCount];
                bool[] leftSpillSchemaWritten = new bool[SpillPartitionCount];
                string[] leftSpillPaths = new string[SpillPartitionCount];

                HashSet<int> spilledPartitions = new();
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is not null)
                    {
                        spilledPartitions.Add(partition);
                    }
                }

                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode = GetRowHashCode(row, columnCount);
                        int partition = AssignPartition(hashCode);

                        if (spilledPartitions.Contains(partition))
                        {
                            WriteToLeftSpillPartition(row, hashCode, leftSpillWriters,
                                leftSpillSchemaWritten, leftSpillPaths, spillSchemaNames!);
                        }
                        else
                        {
                            if (DecrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch))
                            {
                                outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                                outputBatch.Add(row);

                                if (outputBatch.IsFull)
                                {
                                    yield return outputBatch;
                                    outputBatch = null;
                                }
                            }
                        }
                    }
                }

                FlushSpillWriters(leftSpillWriters);

                Dictionary<string, int>? schemaNameIndex = BuildSchemaNameIndex(spillSchemaNames!);

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is null)
                    {
                        continue;
                    }

                    Dictionary<DataValue, int>? partRightSingle = columnCount == 1 ? new() : null;
                    Dictionary<CompositeKey, int>? partRightComposite = columnCount != 1
                        ? new(CompositeKeyComparer.Instance) : null;

                    await foreach (Row row in ReadSpillPartitionAsync(
                        rightSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        IncrementCount(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch);
                    }

                    AddInMemoryCountsForPartition(partition, columnCount,
                        rightSingleCounts, rightCompositeCounts, partRightSingle, partRightComposite);

                    if (leftSpillPaths[partition] is null)
                    {
                        continue;
                    }

                    await foreach (Row row in ReadSpillPartitionAsync(
                        leftSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        if (DecrementCount(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch))
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                }

                CleanupSpillFiles(leftSpillWriters);
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
        finally
        {
            CleanupSpillFiles(rightSpillWriters);
        }
    }

    /// <summary>
    /// EXCEPT DISTINCT: materialises the right branch into a hash set, then
    /// emits left rows that are not in the set (each emitted at most once).
    /// When the memory budget is exceeded, both branches are spilled to
    /// hash-partitioned files and processed partition-by-partition.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteExceptDistinctAsync(ExecutionContext context)
    {
        HashSet<DataValue>? rightSingleSet = null;
        HashSet<CompositeKey>? rightCompositeSet = null;
        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;

        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        bool spilling = false;
        BinaryWriter?[]? rightSpillWriters = null;
        bool[]? rightSpillSchemaWritten = null;
        string[]? rightSpillPaths = null;
        string[]? spillSchemaNames = null;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
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
                        }
                        else
                        {
                            rightCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                            compositeKeyScratch = new DataValue[columnCount];
                        }
                    }

                    if (spilling)
                    {
                        int hashCode = GetRowHashCode(row, columnCount);
                        WriteToSpillPartition(row, hashCode, rightSpillWriters!, rightSpillSchemaWritten!,
                            rightSpillPaths!, spillSchemaNames!);
                    }
                    else
                    {
                        AddRowToSet(row, columnCount, rightSingleSet, rightCompositeSet, compositeKeyScratch);

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
                                spilling = true;
                                EnsureSpillDirectory();
                                rightSpillWriters = new BinaryWriter[SpillPartitionCount];
                                rightSpillSchemaWritten = new bool[SpillPartitionCount];
                                rightSpillPaths = new string[SpillPartitionCount];
                                spillSchemaNames = CaptureSchemaNames(row);

                                if (ExecutionTracer.IsEnabled)
                                {
                                    ExecutionTracer.Write(
                                        $"EXCEPT DISTINCT right spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}");
                                }
                            }
                            else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                            {
                                estimator.EscalateToEveryRow();
                            }
                        }
                    }
                }
                rightBatch.Return();
            }

            if (!spilling)
            {
                // Fully in-memory path.
                HashSet<DataValue>? emittedSingleSet = null;
                HashSet<CompositeKey>? emittedCompositeSet = null;

                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            columnCount = row.FieldCount;
                        }

                        if (emittedSingleSet is null && emittedCompositeSet is null)
                        {
                            if (columnCount == 1)
                            {
                                emittedSingleSet = new HashSet<DataValue>();
                            }
                            else
                            {
                                emittedCompositeSet = new HashSet<CompositeKey>(CompositeKeyComparer.Instance);
                                compositeKeyScratch ??= new DataValue[columnCount];
                            }
                        }

                        if (ContainsRow(row, columnCount, rightSingleSet, rightCompositeSet, compositeKeyScratch))
                        {
                            continue;
                        }

                        bool isNew;
                        if (columnCount == 1)
                        {
                            isNew = emittedSingleSet!.Add(row[0]);
                        }
                        else
                        {
                            ReadOnlySpan<DataValue> keySpan = FillScratch(row, compositeKeyScratch!, columnCount);
                            isNew = emittedCompositeSet!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Add(keySpan);
                        }

                        if (isNew)
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                    leftBatch.Return();
                }
            }
            else
            {
                FlushSpillWriters(rightSpillWriters!);

                BinaryWriter?[] leftSpillWriters = new BinaryWriter[SpillPartitionCount];
                bool[] leftSpillSchemaWritten = new bool[SpillPartitionCount];
                string[] leftSpillPaths = new string[SpillPartitionCount];

                HashSet<int> spilledPartitions = new();
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is not null)
                    {
                        spilledPartitions.Add(partition);
                    }
                }

                HashSet<DataValue>? emittedSingleSet = columnCount == 1 ? new() : null;
                HashSet<CompositeKey>? emittedCompositeSet = columnCount != 1
                    ? new(CompositeKeyComparer.Instance) : null;

                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode = GetRowHashCode(row, columnCount);
                        int partition = AssignPartition(hashCode);

                        if (spilledPartitions.Contains(partition))
                        {
                            WriteToLeftSpillPartition(row, hashCode, leftSpillWriters,
                                leftSpillSchemaWritten, leftSpillPaths, spillSchemaNames!);
                        }
                        else
                        {
                            if (ContainsRow(row, columnCount, rightSingleSet, rightCompositeSet, compositeKeyScratch))
                            {
                                continue;
                            }

                            bool isNew;
                            if (columnCount == 1)
                            {
                                isNew = emittedSingleSet!.Add(row[0]);
                            }
                            else
                            {
                                ReadOnlySpan<DataValue> keySpan = FillScratch(row, compositeKeyScratch!, columnCount);
                                isNew = emittedCompositeSet!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Add(keySpan);
                            }

                            if (isNew)
                            {
                                outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                                outputBatch.Add(row);

                                if (outputBatch.IsFull)
                                {
                                    yield return outputBatch;
                                    outputBatch = null;
                                }
                            }
                        }
                    }
                    leftBatch.Return();
                }

                FlushSpillWriters(leftSpillWriters);

                Dictionary<string, int>? schemaNameIndex = BuildSchemaNameIndex(spillSchemaNames!);

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is null)
                    {
                        continue;
                    }

                    // Load right partition.
                    HashSet<DataValue>? partRightSingle = columnCount == 1 ? new() : null;
                    HashSet<CompositeKey>? partRightComposite = columnCount != 1
                        ? new(CompositeKeyComparer.Instance) : null;

                    await foreach (Row row in ReadSpillPartitionAsync(
                        rightSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        AddRowToSet(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch);
                    }

                    AddInMemoryRowsForPartition(partition, columnCount,
                        rightSingleSet, rightCompositeSet, partRightSingle, partRightComposite);

                    if (leftSpillPaths[partition] is null)
                    {
                        continue;
                    }

                    HashSet<DataValue>? partEmittedSingle = columnCount == 1 ? new() : null;
                    HashSet<CompositeKey>? partEmittedComposite = columnCount != 1
                        ? new(CompositeKeyComparer.Instance) : null;

                    await foreach (Row row in ReadSpillPartitionAsync(
                        leftSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        if (ContainsRow(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch))
                        {
                            continue;
                        }

                        bool isNew;
                        if (columnCount == 1)
                        {
                            isNew = partEmittedSingle!.Add(row[0]);
                        }
                        else
                        {
                            ReadOnlySpan<DataValue> keySpan = FillScratch(row, compositeKeyScratch!, columnCount);
                            isNew = partEmittedComposite!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Add(keySpan);
                        }

                        if (isNew)
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                }

                CleanupSpillFiles(leftSpillWriters);
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
        finally
        {
            CleanupSpillFiles(rightSpillWriters);
        }
    }

    /// <summary>
    /// EXCEPT ALL: materialises the right branch into a counted multiset, then
    /// emits left rows whose count exceeds their right-side count.
    /// When the memory budget is exceeded, both branches are spilled to
    /// hash-partitioned files and processed partition-by-partition.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteExceptAllAsync(ExecutionContext context)
    {
        Dictionary<DataValue, int>? rightSingleCounts = null;
        Dictionary<CompositeKey, int>? rightCompositeCounts = null;
        DataValue[]? compositeKeyScratch = null;
        int columnCount = -1;

        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        bool spilling = false;
        BinaryWriter?[]? rightSpillWriters = null;
        bool[]? rightSpillSchemaWritten = null;
        string[]? rightSpillPaths = null;
        string[]? spillSchemaNames = null;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
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
                            compositeKeyScratch = new DataValue[columnCount];
                        }
                    }

                    if (spilling)
                    {
                        int hashCode = GetRowHashCode(row, columnCount);
                        WriteToSpillPartition(row, hashCode, rightSpillWriters!, rightSpillSchemaWritten!,
                            rightSpillPaths!, spillSchemaNames!);
                    }
                    else
                    {
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
                                spilling = true;
                                EnsureSpillDirectory();
                                rightSpillWriters = new BinaryWriter[SpillPartitionCount];
                                rightSpillSchemaWritten = new bool[SpillPartitionCount];
                                rightSpillPaths = new string[SpillPartitionCount];
                                spillSchemaNames = CaptureSchemaNames(row);

                                if (ExecutionTracer.IsEnabled)
                                {
                                    ExecutionTracer.Write(
                                        $"EXCEPT ALL right spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}");
                                }
                            }
                            else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                            {
                                estimator.EscalateToEveryRow();
                            }
                        }
                    }
                }
            }

            if (!spilling)
            {
                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (columnCount == -1)
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }

                            continue;
                        }

                        if (!DecrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch))
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                }
            }
            else
            {
                FlushSpillWriters(rightSpillWriters!);

                BinaryWriter?[] leftSpillWriters = new BinaryWriter[SpillPartitionCount];
                bool[] leftSpillSchemaWritten = new bool[SpillPartitionCount];
                string[] leftSpillPaths = new string[SpillPartitionCount];

                HashSet<int> spilledPartitions = new();
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is not null)
                    {
                        spilledPartitions.Add(partition);
                    }
                }

                await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode = GetRowHashCode(row, columnCount);
                        int partition = AssignPartition(hashCode);

                        if (spilledPartitions.Contains(partition))
                        {
                            WriteToLeftSpillPartition(row, hashCode, leftSpillWriters,
                                leftSpillSchemaWritten, leftSpillPaths, spillSchemaNames!);
                        }
                        else
                        {
                            if (!DecrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts, compositeKeyScratch))
                            {
                                outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                                outputBatch.Add(row);

                                if (outputBatch.IsFull)
                                {
                                    yield return outputBatch;
                                    outputBatch = null;
                                }
                            }
                        }
                    }
                }

                FlushSpillWriters(leftSpillWriters);

                Dictionary<string, int>? schemaNameIndex = BuildSchemaNameIndex(spillSchemaNames!);

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpillPaths![partition] is null)
                    {
                        continue;
                    }

                    Dictionary<DataValue, int>? partRightSingle = columnCount == 1 ? new() : null;
                    Dictionary<CompositeKey, int>? partRightComposite = columnCount != 1
                        ? new(CompositeKeyComparer.Instance) : null;

                    await foreach (Row row in ReadSpillPartitionAsync(
                        rightSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        IncrementCount(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch);
                    }

                    AddInMemoryCountsForPartition(partition, columnCount,
                        rightSingleCounts, rightCompositeCounts, partRightSingle, partRightComposite);

                    if (leftSpillPaths[partition] is null)
                    {
                        continue;
                    }

                    await foreach (Row row in ReadSpillPartitionAsync(
                        leftSpillPaths[partition], spillSchemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        if (!DecrementCount(row, columnCount, partRightSingle, partRightComposite, compositeKeyScratch))
                        {
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(row);

                            if (outputBatch.IsFull)
                            {
                                yield return outputBatch;
                                outputBatch = null;
                            }
                        }
                    }
                }

                CleanupSpillFiles(leftSpillWriters);
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
        finally
        {
            CleanupSpillFiles(rightSpillWriters);
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
    /// Computes the hash code for a row based on its columns.
    /// </summary>
    private static int GetRowHashCode(Row row, int columnCount)
    {
        if (columnCount == 1)
        {
            return row[0].GetHashCode();
        }

        return CompositeKeyComparer.Instance.GetHashCode(row.RawValues.AsSpan(0, columnCount));
    }

    /// <summary>
    /// Captures the column names from a row for spill file schemas.
    /// </summary>
    private static string[] CaptureSchemaNames(Row row)
    {
        string[] names = new string[row.FieldCount];
        for (int index = 0; index < row.FieldCount; index++)
        {
            names[index] = row.ColumnNames[index];
        }

        return names;
    }

    /// <summary>
    /// Builds a case-insensitive column name-to-index dictionary.
    /// </summary>
    private static Dictionary<string, int> BuildSchemaNameIndex(string[] schemaNames)
    {
        Dictionary<string, int> index = new(schemaNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < schemaNames.Length; i++)
        {
            index[schemaNames[i]] = i;
        }

        return index;
    }

    /// <summary>
    /// Ensures the spill directory exists, creating it if necessary.
    /// </summary>
    private void EnsureSpillDirectory()
    {
        if (_spillDirectory is null)
        {
            _spillDirectory = Path.Combine(
                Path.GetTempPath(), $"datum-setop-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_spillDirectory);
        }
    }

    /// <summary>
    /// Copies in-memory right set entries that hash to the given partition into
    /// a partition-local hash set for complete partition processing.
    /// </summary>
    private static void AddInMemoryRowsForPartition(
        int partition,
        int columnCount,
        HashSet<DataValue>? inMemorySingle,
        HashSet<CompositeKey>? inMemoryComposite,
        HashSet<DataValue>? partitionSingle,
        HashSet<CompositeKey>? partitionComposite)
    {
        if (columnCount == 1 && inMemorySingle is not null)
        {
            foreach (DataValue key in inMemorySingle)
            {
                if (AssignPartition(key.GetHashCode()) == partition)
                {
                    partitionSingle!.Add(key);
                }
            }
        }
        else if (inMemoryComposite is not null)
        {
            foreach (CompositeKey key in inMemoryComposite)
            {
                if (AssignPartition(key.GetHashCode()) == partition)
                {
                    partitionComposite!.Add(key);
                }
            }
        }
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
    /// Adds a row's key to the appropriate hash set.
    /// </summary>
    private static void AddRowToSet(
        Row row,
        int columnCount,
        HashSet<DataValue>? singleKeySet,
        HashSet<CompositeKey>? compositeKeySet,
        DataValue[]? scratch)
    {
        if (columnCount == 1)
        {
            singleKeySet!.Add(row[0]);
        }
        else
        {
            ReadOnlySpan<DataValue> keySpan = FillScratch(row, scratch!, columnCount);
            compositeKeySet!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Add(keySpan);
        }
    }

    /// <summary>
    /// Tests whether a row's key is contained in the hash set.
    /// </summary>
    private static bool ContainsRow(
        Row row,
        int columnCount,
        HashSet<DataValue>? singleKeySet,
        HashSet<CompositeKey>? compositeKeySet,
        DataValue[]? scratch)
    {
        if (singleKeySet is null && compositeKeySet is null)
        {
            return false;
        }

        if (columnCount == 1)
        {
            return singleKeySet!.Contains(row[0]);
        }

        ReadOnlySpan<DataValue> keySpan = FillScratch(row, scratch!, columnCount);
        return compositeKeySet!.GetAlternateLookup<ReadOnlySpan<DataValue>>().Contains(keySpan);
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

    // ---------------------------------------------------------------
    //  Spill infrastructure (shared with UNION DISTINCT)
    // ---------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AssignPartition(int hashCode)
    {
        return (int)((uint)hashCode % SpillPartitionCount);
    }

    private void WriteToSpillPartition(
        Row row,
        int hashCode,
        BinaryWriter?[] writers,
        bool[] schemaWritten,
        string[] paths,
        string[] schemaNames)
    {
        int partition = AssignPartition(hashCode);

        if (writers[partition] is null)
        {
            paths[partition] = Path.Combine(_spillDirectory!, $"setop_{partition}.spill");
            FileStream fileStream = new(paths[partition], FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            writers[partition] = new BinaryWriter(fileStream);
        }

        if (!schemaWritten[partition])
        {
            RowSerializer.WriteSchema(writers[partition]!, row);
            schemaWritten[partition] = true;
        }

        RowSerializer.WriteRow(writers[partition]!, row);
    }

    /// <summary>
    /// Writes a left-side row to a partition-specific spill file, using a separate
    /// file name prefix to avoid colliding with right-side spill files.
    /// </summary>
    private void WriteToLeftSpillPartition(
        Row row,
        int hashCode,
        BinaryWriter?[] writers,
        bool[] schemaWritten,
        string[] paths,
        string[] schemaNames)
    {
        int partition = AssignPartition(hashCode);

        if (writers[partition] is null)
        {
            paths[partition] = Path.Combine(_spillDirectory!, $"setop_left_{partition}.spill");
            FileStream fileStream = new(paths[partition], FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            writers[partition] = new BinaryWriter(fileStream);
        }

        if (!schemaWritten[partition])
        {
            RowSerializer.WriteSchema(writers[partition]!, row);
            schemaWritten[partition] = true;
        }

        RowSerializer.WriteRow(writers[partition]!, row);
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

    private static async IAsyncEnumerable<Row> ReadSpillPartitionAsync(
        string path,
        string[] schemaNames,
        Dictionary<string, int> schemaNameIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream fileStream = new(path, FileMode.Open, FileAccess.Read, FileShare.None, 65536);
        using BinaryReader reader = new(fileStream);

        RowSerializer.ReadSchema(reader, out _, out _);

        while (fileStream.Position < fileStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return RowSerializer.ReadRow(reader, schemaNames, schemaNameIndex);
        }

        await Task.CompletedTask;
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
}
