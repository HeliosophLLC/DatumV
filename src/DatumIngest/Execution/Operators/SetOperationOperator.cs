using DatumIngest.Execution.Operators.Sets;
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
/// All output rows are stabilised into <see cref="ExecutionContext.Store"/> (the per-query
/// arena), so emitted batches resolve correctly after their source input batches return AND
/// downstream operators that splice values without re-stabilizing (e.g.
/// <c>JoinSchema.CombinePooledValues</c>) read the offsets against the same arena the bytes
/// live in. Per-row pool rentals (output <see cref="DataValue"/>[]s, partition buffers, the
/// <c>compositeKeyScratch</c>) are returned in each iterator's <c>finally</c>, keeping
/// <see cref="PoolBacking.ArenaRentCount"/> / <see cref="PoolBacking.RowBatchRentCount"/>
/// / <see cref="PoolBacking.DataValueArrayRentCount"/> balanced for clean leak detection.
/// </para>
/// </summary>
internal sealed class SetOperationOperator : QueryOperator, IDisposable
{
    /// <summary>Number of hash partitions used when spilling to disk.</summary>
    private const int SpillPartitionCount = 64;

    private readonly QueryOperator _left;
    private readonly QueryOperator _right;
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
        QueryOperator left,
        QueryOperator right,
        SetOperationType operationType,
        bool all)
    {
        _left = left;
        _right = right;
        _operationType = operationType;
        _all = all;
    }

    /// <summary>The left input operator.</summary>
    public QueryOperator Left => _left;

    /// <summary>The right input operator.</summary>
    public QueryOperator Right => _right;

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
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
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
        // perRowBytes: DataValue cells + HashSet slot overhead. Arena payloads
        // referenced by the set live in context.Store (mmap, OS-paged) and are
        // not separately tracked. Discovered lazily on first row's FieldCount.
        long perRowBytes = 0;
        long residentBytesNotified = 0;

        DedupKeySet keys = new(context.Pool, poolBoundKeys: false);
        PartitionedRowSpiller spiller = new(context, SpillPartitionCount);
        RowCopyOutputWriter writer = new(context);

        ColumnLookup? schema = null;
        SpillingTriggered = false;     // reset for this execution; safe under re-iteration

        try
        {
            await foreach (RowBatch inputBatch in ConcatenateAsync(_left, _right, context).ConfigureAwait(false))
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

                        if (!keys.IsInitialized)
                        {
                            keys.Initialize(row.FieldCount);
                            perRowBytes = 20L * row.FieldCount + 48L;
                        }

                        // Once spilling, route to spill-only: skip in-memory set Add and
                        // skip the in-memory emit path. The drain phase reads from spill
                        // partitions and dedupes against a partition-local set seeded from
                        // the (now-bounded) in-memory set. This is what keeps the in-memory
                        // hash set bounded and makes the spill machinery actually do work.
                        if (spiller.IsActive)
                        {
                            int partition = spiller.AssignPartition(keys.GetKeyHash(row));
                            spiller.Route(inputBatch, i, partition);
                            continue;
                        }

                        if (!keys.Add(row)) continue;

                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        if (context.Accountant.WouldExceedBudget())
                        {
                            SpillingTriggered = true;
                            spiller.Activate(schema!);
                            // The current row stays in the in-memory set + emit path;
                            // subsequent rows route to spill via the IsActive check above.
                        }

                        RowBatch? full = writer.Add(inputBatch, i);
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            spiller.FlushAllBuffers();

            // Drain spilled partitions. Each partition's local set is seeded with the subset
            // of in-memory keys whose hash routes here, then we replay the partition's spill
            // file and emit any spilled row whose key isn't already in the seed (i.e. wasn't
            // already emitted from the in-memory path) and isn't a duplicate of an earlier
            // row in the same partition.
            if (spiller.IsActive)
            {
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spiller.RowsWrittenInPartition(partition) == 0) continue;

                    using DedupKeySet partitionKeys = new(context.Pool, keys.Comparer, keys.Scratch);
                    partitionKeys.Initialize(keys.ColumnCount);
                    keys.SeedPartitionInto(partition, SpillPartitionCount, partitionKeys);

                    await foreach (RowBatch spilledBatch in spiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledBatch.Count; i++)
                            {
                                context.CancellationToken.ThrowIfCancellationRequested();
                                if (!partitionKeys.Add(spilledBatch[i])) continue;

                                RowBatch? full = writer.Add(spilledBatch, i);
                                DrainEmittedRowCount++;
                                if (full is not null) yield return full;
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledBatch);
                        }
                    }
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }

            // Defensive cleanup. By here writer + spiller buffers should be empty,
            // but on early dispose / cancellation there may be unyielded state.
            keys.Dispose();
            spiller.Dispose();
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
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
        long perRowBytes = 0;
        long residentBytesNotified = 0;

        DedupKeySet rightSet = new(context.Pool, poolBoundKeys: true);
        // emittedSet shares rightSet's pool-bound comparer + composite-key scratch:
        // two co-living dedup sets only need one rented scratch and one comparer.
        DedupKeySet emittedSet = new(context.Pool, rightSet.Comparer, rightSet.Scratch);

        PartitionedRowSpiller rightSpiller = new(context, SpillPartitionCount);
        PartitionedRowSpiller leftSpiller = new(context, SpillPartitionCount);
        RowCopyOutputWriter writer = new(context);

        ColumnLookup? schema = null;
        SpillingTriggered = false;

        try
        {
            // ───── Phase 1: materialise right ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0) schema = rightBatch.ColumnLookup;

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (!rightSet.IsInitialized)
                        {
                            rightSet.Initialize(row.FieldCount);
                            // emittedSet uses rightSet's scratch — Initialize doesn't re-rent.
                            emittedSet.Initialize(row.FieldCount);
                            perRowBytes = 20L * row.FieldCount + 48L;
                        }

                        if (rightSpiller.IsActive)
                        {
                            int partition = rightSpiller.AssignPartition(rightSet.GetKeyHash(row));
                            rightSpiller.Route(rightBatch, i, partition);
                            continue;
                        }

                        rightSet.Add(row);

                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        if (context.Accountant.WouldExceedBudget())
                        {
                            SpillingTriggered = true;
                            rightSpiller.Activate(schema!);
                            // Subsequent rows route to spill via IsActive check above;
                            // the current row stays in the in-memory set.
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(rightBatch);
                }
            }

            if (!rightSet.IsInitialized) yield break;

            // Flush remaining right buffers so RowsWrittenInPartition is accurate
            // for the partitionIsSpilled probe below.
            rightSpiller.FlushAllBuffers();

            if (SpillingTriggered) leftSpiller.Activate(schema!);

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode = rightSet.GetKeyHash(row);
                        int partition = rightSpiller.AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftSpiller.Route(leftBatch, i, partition);
                            continue;
                        }

                        if (!rightSet.Contains(row)) continue;
                        if (!emittedSet.Add(row)) continue;

                        RowBatch? full = writer.Add(leftBatch, i);
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered)
            {
                leftSpiller.FlushAllBuffers();

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    // Two co-living partition-local sets sharing the outer comparer + scratch.
                    // SeedPartitionInto copies single-column keys whose hash routes here; for
                    // pool-bound composite keys it skips (would double-return on dispose), and
                    // we probe rightSet as a fallback at lookup time via Contains(row, fallback).
                    using DedupKeySet partRight = new(context.Pool, rightSet.Comparer, rightSet.Scratch);
                    using DedupKeySet partEmitted = new(context.Pool, rightSet.Comparer, rightSet.Scratch);
                    partRight.Initialize(rightSet.ColumnCount);
                    partEmitted.Initialize(rightSet.ColumnCount);
                    rightSet.SeedPartitionInto(partition, SpillPartitionCount, partRight);

                    // Add spilled right rows for this partition.
                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                partRight.Add(spilledRightBatch[i]);
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    // Probe spilled left rows against partRight (with rightSet fallback for
                    // composite keys that couldn't be seeded), dedupe via partEmitted.
                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                if (!partRight.Contains(row, fallback: rightSet)) continue;
                                if (!partEmitted.Add(row)) continue;

                                RowBatch? full = writer.Add(spilledLeftBatch, i);
                                if (full is not null) yield return full;
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledLeftBatch);
                        }
                    }
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }

            // emittedSet shares scratch with rightSet — disposed first so the scratch
            // is still available to rightSet's composite-key returner. Order matters
            // only if both owned scratch; here only rightSet does.
            emittedSet.Dispose();
            rightSet.Dispose();
            rightSpiller.Dispose();
            leftSpiller.Dispose();
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
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
        long perRowBytes = 0;
        long residentBytesNotified = 0;

        CountedKeyMultiset rightCounts = new(context.Pool);
        PartitionedRowSpiller rightSpiller = new(context, SpillPartitionCount);
        PartitionedRowSpiller leftSpiller = new(context, SpillPartitionCount);
        RowCopyOutputWriter writer = new(context);

        ColumnLookup? schema = null;
        SpillingTriggered = false;

        try
        {
            // ───── Phase 1: materialise right counted multiset ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0) schema = rightBatch.ColumnLookup;

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (!rightCounts.IsInitialized)
                        {
                            rightCounts.Initialize(row.FieldCount);
                            perRowBytes = 20L * row.FieldCount + 48L;
                        }

                        if (rightSpiller.IsActive)
                        {
                            int partition = rightSpiller.AssignPartition(rightCounts.GetKeyHash(row));
                            rightSpiller.Route(rightBatch, i, partition);
                            continue;
                        }

                        rightCounts.Increment(row);

                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        if (context.Accountant.WouldExceedBudget())
                        {
                            SpillingTriggered = true;
                            rightSpiller.Activate(schema!);
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(rightBatch);
                }
            }

            if (!rightCounts.IsInitialized) yield break;

            rightSpiller.FlushAllBuffers();
            if (SpillingTriggered) leftSpiller.Activate(schema!);

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        int hashCode = rightCounts.GetKeyHash(row);
                        int partition = rightSpiller.AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftSpiller.Route(leftBatch, i, partition);
                            continue;
                        }

                        if (!rightCounts.TryDecrement(row)) continue;

                        RowBatch? full = writer.Add(leftBatch, i);
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered)
            {
                leftSpiller.FlushAllBuffers();

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    // Counted multiset can't be safely seeded from the outer set — decrementing
                    // the copy would diverge from the global count. Partition-local starts
                    // empty (counts ingested from spilled rows); decrement probes partition-local
                    // first, falls through to rightCounts on miss via TryDecrement(row, fallback).
                    using CountedKeyMultiset partRight = new(context.Pool, rightCounts.Comparer, rightCounts.Scratch);
                    partRight.Initialize(rightCounts.ColumnCount);

                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                partRight.Increment(spilledRightBatch[i]);
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                if (!partRight.TryDecrement(row, fallback: rightCounts)) continue;

                                RowBatch? full = writer.Add(spilledLeftBatch, i);
                                if (full is not null) yield return full;
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledLeftBatch);
                        }
                    }
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }

            rightCounts.Dispose();
            rightSpiller.Dispose();
            leftSpiller.Dispose();
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
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
        long perRowBytes = 0;
        long residentBytesNotified = 0;

        DedupKeySet rightSet = new(context.Pool, poolBoundKeys: true);
        DedupKeySet emittedSet = new(context.Pool, rightSet.Comparer, rightSet.Scratch);

        PartitionedRowSpiller rightSpiller = new(context, SpillPartitionCount);
        PartitionedRowSpiller leftSpiller = new(context, SpillPartitionCount);
        RowCopyOutputWriter writer = new(context);

        ColumnLookup? schema = null;
        SpillingTriggered = false;

        try
        {
            // ───── Phase 1: materialise right ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0) schema = rightBatch.ColumnLookup;

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (!rightSet.IsInitialized)
                        {
                            rightSet.Initialize(row.FieldCount);
                            emittedSet.Initialize(row.FieldCount);
                            perRowBytes = 20L * row.FieldCount + 48L;
                        }

                        if (rightSpiller.IsActive)
                        {
                            int partition = rightSpiller.AssignPartition(rightSet.GetKeyHash(row));
                            rightSpiller.Route(rightBatch, i, partition);
                            continue;
                        }

                        rightSet.Add(row);

                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        if (context.Accountant.WouldExceedBudget())
                        {
                            SpillingTriggered = true;
                            rightSpiller.Activate(schema!);
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(rightBatch);
                }
            }

            rightSpiller.FlushAllBuffers();

            if (SpillingTriggered) leftSpiller.Activate(schema!);

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    // Empty-right path: schema/columnCount weren't initialised in Phase 1.
                    // Set them up from the first non-empty left batch.
                    if (schema is null && leftBatch.Count > 0) schema = leftBatch.ColumnLookup;

                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (!rightSet.IsInitialized)
                        {
                            rightSet.Initialize(row.FieldCount);
                            emittedSet.Initialize(row.FieldCount);
                            perRowBytes = 20L * row.FieldCount + 48L;
                        }

                        int hashCode = rightSet.GetKeyHash(row);
                        int partition = rightSpiller.AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftSpiller.Route(leftBatch, i, partition);
                            continue;
                        }

                        // Inverted match: emit only when NOT in the right set. Empty-right
                        // correctly passes every left row through (subject to emit dedup).
                        if (rightSet.Contains(row)) continue;
                        if (!emittedSet.Add(row)) continue;

                        RowBatch? full = writer.Add(leftBatch, i);
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered)
            {
                leftSpiller.FlushAllBuffers();

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    // Don't seed partition-local sets — probe both at lookup time. Cleaner
                    // ownership: partRight fully owns its rented keys, returned on dispose.
                    using DedupKeySet partRight = new(context.Pool, rightSet.Comparer, rightSet.Scratch);
                    using DedupKeySet partEmitted = new(context.Pool, rightSet.Comparer, rightSet.Scratch);
                    partRight.Initialize(rightSet.ColumnCount);
                    partEmitted.Initialize(rightSet.ColumnCount);

                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                partRight.Add(spilledRightBatch[i]);
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                if (partRight.Contains(row, fallback: rightSet)) continue;
                                if (!partEmitted.Add(row)) continue;

                                RowBatch? full = writer.Add(spilledLeftBatch, i);
                                if (full is not null) yield return full;
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledLeftBatch);
                        }
                    }
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }

            emittedSet.Dispose();
            rightSet.Dispose();
            rightSpiller.Dispose();
            leftSpiller.Dispose();
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
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
        long perRowBytes = 0;
        long residentBytesNotified = 0;

        CountedKeyMultiset rightCounts = new(context.Pool);
        PartitionedRowSpiller rightSpiller = new(context, SpillPartitionCount);
        PartitionedRowSpiller leftSpiller = new(context, SpillPartitionCount);
        RowCopyOutputWriter writer = new(context);

        ColumnLookup? schema = null;
        SpillingTriggered = false;

        try
        {
            // ───── Phase 1: materialise right counted multiset ─────
            await foreach (RowBatch rightBatch in _right.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    if (schema is null && rightBatch.Count > 0) schema = rightBatch.ColumnLookup;

                    for (int i = 0; i < rightBatch.Count; i++)
                    {
                        Row row = rightBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (!rightCounts.IsInitialized)
                        {
                            rightCounts.Initialize(row.FieldCount);
                            perRowBytes = 20L * row.FieldCount + 48L;
                        }

                        if (rightSpiller.IsActive)
                        {
                            int partition = rightSpiller.AssignPartition(rightCounts.GetKeyHash(row));
                            rightSpiller.Route(rightBatch, i, partition);
                            continue;
                        }

                        rightCounts.Increment(row);

                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        if (context.Accountant.WouldExceedBudget())
                        {
                            SpillingTriggered = true;
                            rightSpiller.Activate(schema!);
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(rightBatch);
                }
            }

            rightSpiller.FlushAllBuffers();
            if (SpillingTriggered) leftSpiller.Activate(schema!);

            // ───── Phase 2: drain left ─────
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    // Empty-right path: schema/columnCount weren't initialised in Phase 1.
                    // Set them up from the first non-empty left batch.
                    if (schema is null && leftBatch.Count > 0) schema = leftBatch.ColumnLookup;

                    for (int i = 0; i < leftBatch.Count; i++)
                    {
                        Row row = leftBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (!rightCounts.IsInitialized)
                        {
                            rightCounts.Initialize(row.FieldCount);
                            perRowBytes = 20L * row.FieldCount + 48L;
                        }

                        int hashCode = rightCounts.GetKeyHash(row);
                        int partition = rightSpiller.AssignPartition(hashCode);
                        bool partitionIsSpilled = SpillingTriggered
                            && rightSpiller.RowsWrittenInPartition(partition) > 0;

                        if (partitionIsSpilled)
                        {
                            leftSpiller.Route(leftBatch, i, partition);
                            continue;
                        }

                        // Inverted from INTERSECT ALL: emit when no remaining right count
                        // consumes the row. TryDecrement returns false against an uninitialised
                        // multiset, so empty-right correctly emits every left row.
                        if (rightCounts.TryDecrement(row)) continue;

                        RowBatch? full = writer.Add(leftBatch, i);
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(leftBatch);
                }
            }

            // ───── Phase 3: drain spilled partitions ─────
            if (SpillingTriggered)
            {
                leftSpiller.FlushAllBuffers();

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (rightSpiller.RowsWrittenInPartition(partition) == 0) continue;
                    if (leftSpiller.RowsWrittenInPartition(partition) == 0) continue;

                    using CountedKeyMultiset partRight = new(context.Pool, rightCounts.Comparer, rightCounts.Scratch);
                    partRight.Initialize(rightCounts.ColumnCount);

                    await foreach (RowBatch spilledRightBatch in rightSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledRightBatch.Count; i++)
                            {
                                partRight.Increment(spilledRightBatch[i]);
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledRightBatch);
                        }
                    }

                    await foreach (RowBatch spilledLeftBatch in leftSpiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledLeftBatch.Count; i++)
                            {
                                Row row = spilledLeftBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                if (partRight.TryDecrement(row, fallback: rightCounts)) continue;

                                RowBatch? full = writer.Add(spilledLeftBatch, i);
                                if (full is not null) yield return full;
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(spilledLeftBatch);
                        }
                    }
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }

            rightCounts.Dispose();
            rightSpiller.Dispose();
            leftSpiller.Dispose();
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }

    /// <summary>
    /// Concatenates two operator streams sequentially.
    /// </summary>
    private static async IAsyncEnumerable<RowBatch> ConcatenateAsync(
        QueryOperator first,
        QueryOperator second,
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
}
