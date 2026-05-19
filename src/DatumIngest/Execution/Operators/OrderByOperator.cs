using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Execution.Operators.Ordering;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Sorts the output of a child operator by one or more expressions.
/// When <see cref="TopNRows"/> is set, uses a bounded max-heap to retain
/// only the top N rows in O(n log N) time and O(N) memory. Otherwise,
/// materialises all rows and sorts them.
/// <para>
/// When <see cref="ExecutionContext.MemoryBudgetBytes"/> is set and the sort is
/// unbounded, the operator spills sorted runs to disk via <see cref="SpillReaderWriter"/>
/// when estimated memory usage exceeds the budget and merges them with a k-way merge
/// at the end.
/// </para>
/// <para>
/// All input rows are stabilised into an operator-owned <see cref="Arena"/> when
/// materialised (top-N heap, in-memory buffer, or pre-spill chunk). This lets input
/// batches return to the pool immediately rather than being pinned for the operator's
/// lifetime, and ensures sort comparisons / emit reads see live arena bytes.
/// </para>
/// <para>
/// Sort keys are pre-evaluated against each row at insertion time so comparators stay
/// synchronous (List.Sort and PriorityQueue can't await). Keys live in the same arena
/// as the row's stabilised payload, so they share the row's lifetime.
/// </para>
/// </summary>
public sealed class OrderByOperator : QueryOperator, IDisposable
{
    private readonly QueryOperator _source;
    private readonly IReadOnlyList<OrderByItem> _orderByItems;
    private readonly int? _topNRows;

    /// <summary>
    /// Creates an ORDER BY operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="orderByItems">The sort criteria.</param>
    /// <param name="topNRows">
    /// When set, limits the sort to the top N rows using a bounded heap.
    /// Typically <c>LIMIT + OFFSET</c> from the query planner.
    /// </param>
    public OrderByOperator(
        QueryOperator source,
        IReadOnlyList<OrderByItem> orderByItems,
        int? topNRows = null)
    {
        _source = source;
        _orderByItems = orderByItems;
        _topNRows = topNRows;
    }

    /// <summary>The child operator producing rows.</summary>
    public QueryOperator Source => _source;

    /// <summary>The sort criteria.</summary>
    public IReadOnlyList<OrderByItem> OrderByItems => _orderByItems;

    /// <summary>
    /// The bounded heap size, or <c>null</c> for unbounded full sort.
    /// </summary>
    public int? TopNRows => _topNRows;

    /// <summary>
    /// Set to <see langword="true"/> the first time the in-memory buffer crosses the
    /// budget and a sorted run is spilled. Test-only observability for spill tests.
    /// </summary>
    internal bool SpillingTriggered { get; private set; }

    /// <summary>
    /// Number of sorted runs spilled to disk during the unbounded path. Zero when
    /// the entire sort fit in memory; one or more when external merge sort kicked
    /// in. Test-only observability.
    /// </summary>
    internal int SortedRunCount { get; private set; }

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
    {
        IReadOnlyList<OrderByItem> rewrittenItems = _orderByItems
            .Select(item => new OrderByItem(rewriter(item.Expression), item.Direction))
            .ToList();
        return new OrderByOperator(_source.RewriteExpressions(rewriter), rewrittenItems, _topNRows);
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        List<string> items = [];
        foreach (OrderByItem item in _orderByItems)
        {
            string direction = item.Direction == SortDirection.Descending ? "DESC" : "ASC";
            items.Add($"{QueryExplainer.FormatExpression(item.Expression)} {direction}");
        }

        Dictionary<string, string> properties = new()
        {
            ["order"] = string.Join(", ", items),
        };

        List<string> annotations = [];
        if (_topNRows is not null)
        {
            annotations.Add($"bounded top-N sort (N={_topNRows})");
            properties["top"] = _topNRows.Value.ToString();
        }

        return new OperatorPlanDescription("Sort")
        {
            Properties = properties,
            Children = [(Source, null)],
            Annotations = annotations,
            Warnings = _topNRows is null ? ["materializes all rows for sorting"] : [],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = context.CreateEvaluator();

        SpillingTriggered = false;
        SortedRunCount = 0;

        if (_topNRows is int topN and > 0)
        {
            await foreach (RowBatch batch in ExecuteTopNAsync(topN, evaluator, context, pool).ConfigureAwait(false))
            {
                yield return batch;
            }
            yield break;
        }

        await foreach (RowBatch batch in ExecuteUnboundedAsync(evaluator, context, pool).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    // ───────── Top-N path ─────────

    private async IAsyncEnumerable<RowBatch> ExecuteTopNAsync(
        int topN, ExpressionEvaluator evaluator, ExecutionContext context, Pool pool)
    {
        // Heap rows live in context.Store: stabilised at insertion so input batches can
        // return to the pool immediately. The heap is a max-heap (reverse comparison)
        // — its peek is the worst-of-the-best, which is what gets evicted on overflow.
        SidecarRegistry? sidecarRegistry = context.SidecarRegistry;
        SortKeyComparer comparer = new(_orderByItems);
        SortKeyEvaluator keyEvaluator = new(_orderByItems, evaluator);
        ColumnLookup? schema = null;
        PriorityQueue<KeyedRow, KeyedRow> heap = new(
            Comparer<KeyedRow>.Create((left, right) =>
                -comparer.Compare(
                    left.Keys, context.Store, sidecarRegistry,
                    right.Keys, context.Store, sidecarRegistry)));
        OutputBatchAccumulator output = new(context);

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
                        Row sourceRow = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        if (heap.Count < topN)
                        {
                            DataValue[] copy = pool.RentAndCopyDataValues(
                                sourceRow, inputBatch.Arena, context.Store);
                            Row stableRow = new(sourceRow.ColumnLookup, copy);
                            DataValue[] keys = await keyEvaluator
                                .EvaluateAsync(stableRow, context.Store, context.CancellationToken)
                                .ConfigureAwait(false);
                            KeyedRow keyedRow = new(stableRow, keys);
                            heap.Enqueue(keyedRow, keyedRow);
                        }
                        else
                        {
                            // Compare without allocating: peek the worst, decide if the new
                            // row beats it. Asymmetric arenas: the source row's payloads live
                            // in the input batch's arena, the heap row in context.Store —
                            // pre-evaluate the candidate's keys against the input arena and
                            // materialise them into context.Store so they survive eviction.
                            DataValue[] candidateKeys = await keyEvaluator
                                .EvaluateAsync(sourceRow, inputBatch.Arena, context.Store, context.CancellationToken)
                                .ConfigureAwait(false);
                            KeyedRow worst = heap.Peek();
                            if (comparer.Compare(
                                    candidateKeys, context.Store, sidecarRegistry,
                                    worst.Keys, context.Store, sidecarRegistry) < 0)
                            {
                                KeyedRow evicted = heap.Dequeue();
                                pool.ReturnRow(evicted.Row);

                                DataValue[] copy = pool.RentAndCopyDataValues(
                                    sourceRow, inputBatch.Arena, context.Store);
                                Row stableRow = new(sourceRow.ColumnLookup, copy);
                                KeyedRow keyedRow = new(stableRow, candidateKeys);
                                heap.Enqueue(keyedRow, keyedRow);
                            }
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            if (heap.Count == 0) yield break;

            // Drain the heap into a list and sort ascending (the heap was max-ordered
            // for eviction; output wants the natural sort direction).
            List<KeyedRow> sorted = new(heap.Count);
            while (heap.Count > 0)
            {
                sorted.Add(heap.Dequeue());
            }
            sorted.Sort((left, right) =>
                comparer.Compare(
                    left.Keys, context.Store, sidecarRegistry,
                    right.Keys, context.Store, sidecarRegistry));

            for (int i = 0; i < sorted.Count; i++)
            {
                Row row = sorted[i].Row;
                sorted[i] = default;
                // Heap rows already live in context.Store with pool-rented arrays.
                // Adopt transfers ownership to the output batch — no copy, no
                // separate pool.ReturnRow (batch recycle reclaims the array).
                RowBatch? full = output.Adopt(schema!, row);
                if (full is not null) yield return full;
            }

            RowBatch? trailing = output.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            // Drain whatever's left in the heap (mid-cancel).
            while (heap.Count > 0)
            {
                pool.ReturnRow(heap.Dequeue().Row);
            }
            RowBatch? leftover = output.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
            // No bufferArena release: heap rows lived in context.Store, owned by the query.
        }
    }

    // ───────── Unbounded path with optional spill ─────────

    private async IAsyncEnumerable<RowBatch> ExecuteUnboundedAsync(
        ExpressionEvaluator evaluator, ExecutionContext context, Pool pool)
    {
        // Structural per-row residency for budget accounting. Computed lazily
        // once we see the first batch (need fieldCount). The DataValue cells
        // and key array are both GC-resident; arena payloads they reference
        // live in `bufferArena` (file-backed mmap, OS-paged, out of budget).
        long perRowBytes = 0;
        long residentBytesNotified = 0;

        SortKeyComparer comparer = new(_orderByItems);
        SortKeyEvaluator keyEvaluator = new(_orderByItems, evaluator);
        SortedRunSpiller spiller = new(context);

        Arena? bufferArena = null;
        SidecarRegistry? sidecarRegistry = context.SidecarRegistry;
        ColumnLookup? schema = null;
        List<KeyedRow> buffer = [];
        OutputBatchAccumulator output = new(context);

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                if (inputBatch.Count == 0)
                {
                    context.ReturnRowBatch(inputBatch);
                    continue;
                }

                try
                {
                    if (bufferArena is null || schema is null)
                    {
                        schema = inputBatch.ColumnLookup;
                        // Pre-size the bufferArena to the budget. Without this, the arena
                        // grows from 1 MB → ~budget through ~10 power-of-two doublings each
                        // spill cycle; each doubling creates a fresh pagefile-backed mmap
                        // and releases the old, leaving the OS to reclaim pages that haven't
                        // been needed yet. For a 12-spill query that's ~10 GB of transient
                        // commit charge. Sizing up-front means one mmap allocation per cycle.
                        // 64 MB default when no budget is configured.
                        long bufferArenaCapacity = context.Accountant.MemoryBudgetBytes ?? 64L * 1024 * 1024;
                        bufferArena = pool.RentArena(bufferArenaCapacity);
                        // DataValue.SizeBytes (32) per cell × (field + key) cells
                        // plus a ~64-byte KeyedRow / List slot per row.
                        perRowBytes = DataValue.SizeBytes * (long)schema.Count + DataValue.SizeBytes * (long)_orderByItems.Count + 64L;
                    }

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row sourceRow = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        DataValue[] copy = pool.RentAndCopyDataValues(
                            sourceRow, inputBatch.Arena, bufferArena!);
                        Row stableRow = new(sourceRow.ColumnLookup, copy);
                        DataValue[] keys = await keyEvaluator
                            .EvaluateAsync(stableRow, bufferArena, context.CancellationToken)
                            .ConfigureAwait(false);
                        buffer.Add(new KeyedRow(stableRow, keys));
                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        // Adding this row may have pushed the plan-wide residency over the
                        // budget. Spill the current sorted chunk to disk and reset — the
                        // spilled rows release their accounted bytes.
                        if (context.Accountant.WouldExceedBudget())
                        {
                            buffer.Sort((left, right) =>
                                comparer.Compare(
                                    left.Keys, bufferArena!, sidecarRegistry,
                                    right.Keys, bufferArena!, sidecarRegistry));
                            spiller.Spill(schema!, bufferArena!, buffer);
                            SpillingTriggered = true;
                            SortedRunCount = spiller.Count;

                            // Reset for next chunk: fresh arena, fresh buffer, accounted
                            // bytes released. Buffer rows' DataValue[]s were transferred
                            // to the run batch and consumed by the spiller's Write (which
                            // returned them to the pool); the buffer's list entries are
                            // now stale slot references.
                            buffer.Clear();
                            context.Accountant.NotifyReleased(residentBytesNotified);
                            residentBytesNotified = 0;
                            pool.ReturnArena(bufferArena!);
                            long postSpillBudget = context.Accountant.MemoryBudgetBytes ?? 64L * 1024 * 1024;
                            bufferArena = pool.RentArena(postSpillBudget);
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            if (spiller.Count == 0)
            {
                // Everything fit in memory. Sort buffer and emit.
                buffer.Sort((left, right) =>
                    comparer.Compare(
                        left.Keys, bufferArena!, sidecarRegistry,
                        right.Keys, bufferArena!, sidecarRegistry));

                // Emit batches backed by context.Store so downstream operators that splice
                // values without re-stabilizing (e.g. JoinSchema.CombinePooledValues)
                // resolve offsets against the same arena the bytes live in. Re-stabilize
                // each value from bufferArena into context.Store on copy-out — bufferArena
                // is private to this operator and will be released below.
                for (int i = 0; i < buffer.Count; i++)
                {
                    Row row = buffer[i].Row;
                    DataValue[] outValues = pool.RentAndCopyDataValues(
                        row.RawValues, bufferArena!, context.Store);
                    Row stableRow = new(schema!, outValues);
                    RowBatch? full = output.Adopt(schema!, stableRow);
                    if (full is not null) yield return full;
                }

                RowBatch? trailing = output.Flush();
                if (trailing is not null) yield return trailing;
                yield break;
            }

            // Flush any remaining in-memory rows as the final sorted run.
            if (buffer.Count > 0)
            {
                buffer.Sort((left, right) =>
                    comparer.Compare(
                        left.Keys, bufferArena!, sidecarRegistry,
                        right.Keys, bufferArena!, sidecarRegistry));
                spiller.Spill(schema!, bufferArena!, buffer);
                SortedRunCount = spiller.Count;
                buffer.Clear();
                // Final chunk's rows are on disk; release the bytes we notified for them.
                if (residentBytesNotified > 0)
                {
                    context.Accountant.NotifyReleased(residentBytesNotified);
                    residentBytesNotified = 0;
                }
            }
            // Buffer arena's last round — release it; merge phase emits via context.Store.
            if (bufferArena is not null)
            {
                pool.ReturnArena(bufferArena);
                bufferArena = null;
            }

            // K-way merge across all sorted runs. Output rows are stabilised into
            // context.Store via RentAndCopyToOutput, so downstream operators that splice
            // values without re-stabilizing resolve offsets against the same arena the
            // bytes live in.
            await foreach (RowBatch mergedBatch in MergeSortedRunsAsync(
                spiller.Runs, schema!, evaluator, context, pool).ConfigureAwait(false))
            {
                yield return mergedBatch;
            }
        }
        finally
        {
            // Buffer rows still owning DataValue[]s (only present on early exit).
            foreach (KeyedRow keyedRow in buffer)
            {
                if (keyedRow.Row.RawValues is not null) pool.ReturnRow(keyedRow.Row);
            }
            buffer.Clear();

            // Release any residency we notified for in-memory buffer rows that didn't go
            // through the spill release path (the all-in-memory emit + cancellation paths).
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }

            RowBatch? leftover = output.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
            spiller.Dispose();
            if (bufferArena is not null) pool.ReturnArena(bufferArena);
        }
    }

    /// <summary>
    /// Builds a <see cref="SortedRunReader"/> per sorted run (advancing each to its first
    /// row) and delegates the k-way merge to <see cref="KWayMerger.MergeAsync"/>. Empty
    /// runs are disposed immediately rather than handed to the merger.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> MergeSortedRunsAsync(
        IReadOnlyList<SpillReaderWriter> runs,
        ColumnLookup schema,
        ExpressionEvaluator evaluator,
        ExecutionContext context,
        Pool pool)
    {
        SortKeyComparer comparer = new(_orderByItems);
        SortKeyEvaluator keyEvaluator = new(_orderByItems, evaluator);

        List<SortedRunReader> readers = new(runs.Count);
        foreach (SpillReaderWriter run in runs)
        {
            SortedRunReader reader = new(run, schema, context, keyEvaluator);
            if (await reader.ReadNextAsync().ConfigureAwait(false))
            {
                readers.Add(reader);
            }
            else
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (readers.Count == 0) yield break;

        await foreach (RowBatch batch in KWayMerger
            .MergeAsync(readers, schema, comparer, context)
            .ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op. Spill resources (per-run <see cref="SpillReaderWriter"/> instances and
    /// their temp directories / file-backed arenas) are owned by the iterator and
    /// disposed in its <c>finally</c> block, so consumer-driven dispose flows through
    /// that path. Kept on the type so <see cref="IDisposable"/> contract continues to
    /// work transparently.
    /// </remarks>
    public void Dispose()
    {
    }
}
