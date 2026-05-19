using Heliosoph.DatumV.Execution.Operators.Sets;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators;

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
internal sealed class DistinctOperator : QueryOperator, IDisposable
{
    /// <summary>Number of spill partitions used when the memory budget is exceeded.</summary>
    private const int SpillPartitionCount = 64;

    private readonly QueryOperator _source;

    /// <summary>
    /// Creates a new distinct operator over the given source.
    /// </summary>
    /// <param name="source">The upstream operator whose output rows are deduplicated.</param>
    public DistinctOperator(QueryOperator source)
    {
        _source = source;
    }

    /// <summary>The upstream operator.</summary>
    public QueryOperator Source => _source;

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
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return new OperatorPlanDescription("Distinct")
        {
            Children = [(Source, null)],
            Warnings = ["materializes all unique rows in memory"],
        };
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        // DISTINCT must scan enough input rows to find N unique values — it cannot
        // predict how many input rows are needed. Strip RowLimit to prevent child
        // operators (e.g. JoinOperator) from picking strategies (index nested-loop)
        // that only pay off when the consumer needs few rows.
        if (context.RowLimit is not null)
        {
            context = context.WithRowLimit(null);
        }

        // perRowBytes: DataValue cells + HashSet slot overhead. Arena payloads
        // referenced by keys live in context.Store (mmap, OS-paged) and are not
        // separately budgeted. Computed lazily on first row's FieldCount.
        long perRowBytes = 0;
        long residentBytesNotified = 0;

        DedupKeySet keys = new(context.Pool, poolBoundKeys: true);
        PartitionedRowSpiller spiller = new(context, SpillPartitionCount);
        RowCopyOutputWriter writer = new(context);

        ColumnLookup? schema = null;
        SpillingTriggered = false;     // reset for this execution; safe under re-iteration
        DrainEmittedRowCount = 0;
        SpilledRowCount = 0;

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

                        if (!keys.IsInitialized)
                        {
                            keys.Initialize(row.FieldCount);
                            perRowBytes = DataValue.SizeBytes * (long)row.FieldCount + 48L;
                        }

                        // Once spilling, route to spill-only: skip the in-memory set Add
                        // and skip the in-memory emit path. The drain phase reads from
                        // spill partitions and dedupes against (partition-local set ∪
                        // in-memory set). Keeps the in-memory set bounded at the size
                        // it had when spill triggered.
                        if (spiller.IsActive)
                        {
                            int partition = spiller.AssignPartition(keys.GetKeyHash(row));
                            spiller.Route(inputBatch, i, partition);
                            SpilledRowCount++;
                            continue;
                        }

                        if (!keys.Add(row)) continue;

                        // Account for the in-memory hash-set slot for this new distinct
                        // key. Subsequent budget check can then see plan-wide residency
                        // (including any upstream materializing operator's bytes).
                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        if (context.Accountant.WouldExceedBudget())
                        {
                            SpillingTriggered = true;
                            spiller.Activate(schema!);
                            // The current row stays in the in-memory set + emit path;
                            // subsequent rows route to spill via the IsActive check above.
                        }

                        RowBatch? full = writer.Add(schema!, inputBatch, i);
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            spiller.FlushAllBuffers();

            // Drain spilled partitions: per partition, build a partition-local seen-set
            // (Contains-fallback against the in-memory set for keys emitted pre-spill).
            // For each spilled row, skip if it's in the outer set (already emitted) or
            // already in the partition-local set (duplicate within spill). Otherwise
            // emit + add to partition-local set.
            if (spiller.IsActive)
            {
                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spiller.RowsWrittenInPartition(partition) == 0) continue;

                    using DedupKeySet partKeys = new(context.Pool, keys.Comparer, keys.Scratch);
                    partKeys.Initialize(keys.ColumnCount);

                    await foreach (RowBatch spilledBatch in spiller
                        .ReplayPartitionAsync(partition).ConfigureAwait(false))
                    {
                        try
                        {
                            for (int i = 0; i < spilledBatch.Count; i++)
                            {
                                Row row = spilledBatch[i];
                                context.CancellationToken.ThrowIfCancellationRequested();

                                // Skip if outer (in-mem) already emitted it OR already
                                // seen in this partition's spill.
                                if (keys.Contains(row)) continue;
                                if (!partKeys.Add(row)) continue;

                                RowBatch? full = writer.Add(schema!, spilledBatch, i);
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

            keys.Dispose();
            spiller.Dispose();
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op. Spill resources are owned by the iterator and disposed in its
    /// <c>finally</c> block, so consumer-driven dispose (mid-iteration break)
    /// flows through that path. Kept on the type so <c>using</c> patterns and
    /// the <see cref="IDisposable"/> contract continue to work transparently.
    /// </remarks>
    public void Dispose()
    {
    }
}
