using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

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

    /// <summary>
    /// A buffered row with its pre-evaluated sort keys. Keys are stabilised into the
    /// same arena as the row's payload, so they share the row's lifetime.
    /// </summary>
    private readonly struct KeyedRow(Row row, DataValue[] keys)
    {
        public Row Row { get; } = row;
        public DataValue[] Keys { get; } = keys;
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = new(context);

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

    /// <summary>
    /// Pre-evaluates each ORDER BY item against <paramref name="row"/>. Results live in
    /// <paramref name="targetArena"/> so they survive past the input batch's recycle.
    /// </summary>
    private async ValueTask<DataValue[]> EvaluateKeysAsync(
        ExpressionEvaluator evaluator,
        Row row,
        Arena sourceArena,
        Arena targetArena,
        SidecarRegistry? sidecarRegistry,
        TypeRegistry? types,
        CancellationToken cancellationToken)
    {
        DataValue[] keys = new DataValue[_orderByItems.Count];
        EvaluationFrame frame = new(row, sourceArena, targetArena, evaluator.Accountant, outerRow: null, sidecarRegistry, types);
        for (int i = 0; i < _orderByItems.Count; i++)
        {
            keys[i] = await evaluator.EvaluateAsync(_orderByItems[i].Expression, frame, cancellationToken).ConfigureAwait(false);
        }
        return keys;
    }

    // ───────── Top-N path ─────────

    private async IAsyncEnumerable<RowBatch> ExecuteTopNAsync(
        int topN, ExpressionEvaluator evaluator, ExecutionContext context, Pool pool)
    {
        // Heap rows live in bufferArena: stabilised at insertion so input batches can
        // return to the pool immediately. The heap is a max-heap (reverse comparison)
        // — its peek is the worst-of-the-best, which is what gets evicted on overflow.
        Arena? bufferArena = null;
        SidecarRegistry? sidecarRegistry = context.SidecarRegistry;
        ColumnLookup? schema = null;
        PriorityQueue<KeyedRow, KeyedRow> heap = new(
            Comparer<KeyedRow>.Create((left, right) =>
                -CompareKeys(
                    left.Keys, bufferArena!, sidecarRegistry,
                    right.Keys, bufferArena!, sidecarRegistry)));
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
                        bufferArena = pool.RentArena();
                    }

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row sourceRow = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        if (heap.Count < topN)
                        {
                            DataValue[] copy = pool.RentAndCopyDataValues(
                                sourceRow, inputBatch.Arena, bufferArena!);
                            Row stableRow = new(sourceRow.ColumnLookup, copy);
                            DataValue[] keys = await EvaluateKeysAsync(
                                evaluator, stableRow, bufferArena!, bufferArena!, sidecarRegistry, context.Types, context.CancellationToken).ConfigureAwait(false);
                            KeyedRow keyedRow = new(stableRow, keys);
                            heap.Enqueue(keyedRow, keyedRow);
                        }
                        else
                        {
                            // Compare without allocating: peek the worst, decide if the
                            // new row beats it. If so, evict the worst (return its array
                            // to the pool) and insert the new row (rented + stabilised).
                            // Asymmetric arenas: the source row's payloads live in the
                            // input batch's arena, while the heap row was already
                            // stabilised into bufferArena. Pre-evaluate the candidate's
                            // keys against the input arena before deciding.
                            DataValue[] candidateKeys = await EvaluateKeysAsync(
                                evaluator, sourceRow, inputBatch.Arena, bufferArena!, sidecarRegistry, context.Types, context.CancellationToken).ConfigureAwait(false);
                            KeyedRow worst = heap.Peek();
                            if (CompareKeys(
                                    candidateKeys, bufferArena!, sidecarRegistry,
                                    worst.Keys, bufferArena!, sidecarRegistry) < 0)
                            {
                                KeyedRow evicted = heap.Dequeue();
                                pool.ReturnRow(evicted.Row);

                                DataValue[] copy = pool.RentAndCopyDataValues(
                                    sourceRow, inputBatch.Arena, bufferArena!);
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
                CompareKeys(
                    left.Keys, bufferArena!, sidecarRegistry,
                    right.Keys, bufferArena!, sidecarRegistry));

            for (int i = 0; i < sorted.Count; i++)
            {
                Row row = sorted[i].Row;
                outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, bufferArena!);
                DataValue[] outValues = pool.RentDataValues(row.FieldCount);
                row.RawValues.CopyTo(outValues);
                outputBatch.Add(outValues);
                pool.ReturnRow(row);                // sorted's row is consumed; clear the slot
                sorted[i] = default;

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
            // Drain whatever's left in the heap (mid-cancel).
            while (heap.Count > 0)
            {
                pool.ReturnRow(heap.Dequeue().Row);
            }
            if (outputBatch is not null) context.ReturnRowBatch(outputBatch);
            if (bufferArena is not null) pool.ReturnArena(bufferArena);
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

        Arena? bufferArena = null;
        SidecarRegistry? sidecarRegistry = context.SidecarRegistry;
        ColumnLookup? schema = null;
        List<KeyedRow> buffer = [];
        List<SpillReaderWriter> sortedRuns = [];
        Arena? outputArena = null;
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
                        // Pre-size the bufferArena to the budget. Without
                        // this, the arena grows from 1 MB → ~budget through
                        // ~10 power-of-two doublings each spill cycle; each
                        // doubling creates a fresh pagefile-backed mmap and
                        // releases the old, leaving the OS to reclaim pages
                        // that haven't been needed yet. For a 12-spill query
                        // that's ~10 GB of transient commit charge. Sizing
                        // up-front means one mmap allocation per cycle.
                        // Cap at int.MaxValue (Arena uses int internally)
                        // and use a sensible 64 MB default when no budget is
                        // configured.
                        long budget = context.Accountant.MemoryBudgetBytes ?? 64L * 1024 * 1024;
                        int bufferArenaCapacity = (int)Math.Min(budget, int.MaxValue);
                        bufferArena = pool.RentArena(bufferArenaCapacity);
                        // 20 bytes per DataValue cell (overhead) × (field + key) cells
                        // plus a ~64-byte KeyedRow / List slot per row.
                        perRowBytes = 20L * schema.Count + 20L * _orderByItems.Count + 64L;
                    }

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row sourceRow = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        DataValue[] copy = pool.RentAndCopyDataValues(
                            sourceRow, inputBatch.Arena, bufferArena!);
                        Row stableRow = new(sourceRow.ColumnLookup, copy);
                        DataValue[] keys = await EvaluateKeysAsync(
                            evaluator, stableRow, bufferArena!, bufferArena!, sidecarRegistry, context.Types, context.CancellationToken).ConfigureAwait(false);
                        buffer.Add(new KeyedRow(stableRow, keys));
                        context.Accountant.NotifyMaterialized(perRowBytes);
                        residentBytesNotified += perRowBytes;

                        // Adding this row may have pushed the plan-wide
                        // residency over the budget. Spill the current
                        // sorted chunk to disk and reset — the spilled
                        // rows release their accounted bytes.
                        if (context.Accountant.WouldExceedBudget())
                        {
                            buffer.Sort((left, right) =>
                                CompareKeys(
                                    left.Keys, bufferArena!, sidecarRegistry,
                                    right.Keys, bufferArena!, sidecarRegistry));
                            SpillReaderWriter run = SpillSortedBuffer(
                                pool, schema!, context, bufferArena!, buffer);
                            sortedRuns.Add(run);
                            SpillingTriggered = true;
                            SortedRunCount++;

                            // Reset for next chunk: fresh arena, fresh
                            // buffer, accounted bytes released. Buffer rows'
                            // DataValue[]s were transferred to the run batch
                            // and consumed by the spiller's Write (which
                            // returned them to the pool); the buffer's list
                            // entries are now stale slot references.
                            buffer.Clear();
                            context.Accountant.NotifyReleased(residentBytesNotified);
                            residentBytesNotified = 0;
                            pool.ReturnArena(bufferArena!);
                            // Pre-size the fresh arena (see schema-init
                            // comment above for the budget rationale).
                            long postSpillBudget = context.Accountant.MemoryBudgetBytes ?? 64L * 1024 * 1024;
                            bufferArena = pool.RentArena((int)Math.Min(postSpillBudget, int.MaxValue));
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }

            if (sortedRuns.Count == 0)
            {
                // Everything fit in memory. Sort buffer and emit.
                buffer.Sort((left, right) =>
                    CompareKeys(
                        left.Keys, bufferArena!, sidecarRegistry,
                        right.Keys, bufferArena!, sidecarRegistry));

                for (int i = 0; i < buffer.Count; i++)
                {
                    Row row = buffer[i].Row;
                    outputBatch ??= pool.RentRowBatch(schema!, context.BatchSize, bufferArena!);
                    DataValue[] outValues = pool.RentDataValues(row.FieldCount);
                    row.RawValues.CopyTo(outValues);
                    outputBatch.Add(outValues);

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
                yield break;
            }

            // Flush any remaining in-memory rows as the final sorted run.
            if (buffer.Count > 0)
            {
                buffer.Sort((left, right) =>
                    CompareKeys(
                        left.Keys, bufferArena!, sidecarRegistry,
                        right.Keys, bufferArena!, sidecarRegistry));
                SpillReaderWriter finalRun = SpillSortedBuffer(
                    pool, schema!, context, bufferArena!, buffer);
                sortedRuns.Add(finalRun);
                SortedRunCount++;
                buffer.Clear();
                // Final chunk's rows are on disk; release the bytes we
                // notified for them.
                if (residentBytesNotified > 0)
                {
                    context.Accountant.NotifyReleased(residentBytesNotified);
                    residentBytesNotified = 0;
                }
            }
            // Buffer arena's last round — release it; merge phase uses a separate output arena.
            if (bufferArena is not null)
            {
                pool.ReturnArena(bufferArena);
                bufferArena = null;
            }

            // K-way merge across all sorted runs. Output rows are stabilised into
            // outputArena via RentAndCopyToOutput, so the consumer sees a single
            // arena regardless of which run a given row came from.
            outputArena = pool.RentArena();
            await foreach (RowBatch mergedBatch in MergeSortedRunsAsync(
                sortedRuns, schema!, evaluator, context, pool, outputArena).ConfigureAwait(false))
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

            // Release any residency we notified for in-memory buffer rows
            // that didn't go through the spill release path (the all-in-
            // memory emit case + cancellation / error early exits).
            if (residentBytesNotified > 0)
            {
                context.Accountant.NotifyReleased(residentBytesNotified);
            }

            if (outputBatch is not null) context.ReturnRowBatch(outputBatch);
            foreach (SpillReaderWriter run in sortedRuns)
            {
                run.Dispose();
            }
            if (bufferArena is not null) pool.ReturnArena(bufferArena);
            if (outputArena is not null) pool.ReturnArena(outputArena);
        }
    }

    /// <summary>
    /// Wraps a sorted in-memory buffer as a single-partition <see cref="SpillReaderWriter"/>
    /// run: bundles the buffer's rows into a <see cref="RowBatch"/> over the buffer's
    /// arena, hands it to the spiller (which stabilises payloads into its own
    /// consolidated arena and returns the input batch — releasing all the rented
    /// <see cref="DataValue"/>[]s back to the pool). The spiller is the caller's to
    /// dispose later.
    /// </summary>
    private static SpillReaderWriter SpillSortedBuffer(
        Pool pool, ColumnLookup schema, ExecutionContext context, Arena bufferArena, List<KeyedRow> sortedBuffer)
    {
        SpillReaderWriter run = new SpillReaderWriter(
            pool, schema, context.SpillDirectory, partitionCount: 1);

        RowBatch runBatch = pool.RentRowBatch(schema, sortedBuffer.Count, bufferArena);
        foreach (KeyedRow keyedRow in sortedBuffer)
        {
            runBatch.Add(keyedRow.Row.RawValues);
        }

        run.Write(runBatch, partition: 0);
        return run;
    }

    /// <summary>
    /// K-way merge of pre-sorted runs. Each run's <c>ReplayPartitionAsync</c>
    /// stream is wrapped in a <see cref="RunReader"/> exposing the current row + a
    /// <c>ReadNextAsync</c> that advances within a batch (no I/O on intra-batch step)
    /// or pulls the next batch from the run.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> MergeSortedRunsAsync(
        List<SpillReaderWriter> runs,
        ColumnLookup schema,
        ExpressionEvaluator evaluator,
        ExecutionContext context,
        Pool pool,
        Arena outputArena)
    {
        List<RunReader> readers = new(runs.Count);
        SidecarRegistry? sidecarRegistry = context.SidecarRegistry;
        RowBatch? outputBatch = null;

        try
        {
            foreach (SpillReaderWriter run in runs)
            {
                RunReader reader = new(run, schema, context, pool, this, evaluator);
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

            PriorityQueue<RunReader, RunReader> heap = new(
                Comparer<RunReader>.Create((a, b) =>
                    CompareKeys(
                        a.CurrentKeys, a.CurrentBatch.Arena, sidecarRegistry,
                        b.CurrentKeys, b.CurrentBatch.Arena, sidecarRegistry)));
            foreach (RunReader r in readers)
            {
                heap.Enqueue(r, r);
            }

            while (heap.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                RunReader winner = heap.Dequeue();

                outputBatch ??= pool.RentRowBatch(schema, context.BatchSize, outputArena);
                pool.RentAndCopyToOutput(winner.CurrentBatch, winner.CurrentIndex, outputBatch);

                if (outputBatch.IsFull)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }

                if (await winner.ReadNextAsync().ConfigureAwait(false))
                {
                    heap.Enqueue(winner, winner);
                }
                else
                {
                    await winner.DisposeAsync().ConfigureAwait(false);
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
            if (outputBatch is not null) context.ReturnRowBatch(outputBatch);
            foreach (RunReader r in readers)
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Compares two pre-evaluated sort-key arrays element-by-element. Direction is
    /// applied per-key from <see cref="OrderByItem.Direction"/>. First non-equal key
    /// determines the result; all-equal returns 0.
    /// </summary>
    private int CompareKeys(
        DataValue[] left, IValueStore leftStore, SidecarRegistry? leftRegistry,
        DataValue[] right, IValueStore rightStore, SidecarRegistry? rightRegistry)
    {
        for (int i = 0; i < _orderByItems.Count; i++)
        {
            int comparison = CompareDataValues(
                left[i], leftStore, leftRegistry,
                right[i], rightStore, rightRegistry);

            if (_orderByItems[i].Direction == SortDirection.Descending)
            {
                comparison = -comparison;
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    /// <summary>
    /// Compares two <see cref="DataValue"/> instances for ordering across all three storage
    /// tiers (inline / arena-backed / sidecar-backed). Nulls sort last.
    /// </summary>
    /// <param name="left">Left value.</param>
    /// <param name="leftStore">Arena backing <paramref name="left"/> when arena-backed.</param>
    /// <param name="leftRegistry">Sidecar registry resolving <paramref name="left"/> when sidecar-backed.</param>
    /// <param name="right">Right value.</param>
    /// <param name="rightStore">Arena backing <paramref name="right"/> when arena-backed.</param>
    /// <param name="rightRegistry">Sidecar registry resolving <paramref name="right"/> when sidecar-backed.</param>
    internal static int CompareDataValues(
        DataValue left, IValueStore leftStore, SidecarRegistry? leftRegistry,
        DataValue right, IValueStore rightStore, SidecarRegistry? rightRegistry)
    {
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return DataValueComparer.Compare(
            left, leftStore, leftRegistry,
            right, rightStore, rightRegistry);
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

    /// <summary>
    /// Streams rows from a single sorted run via <c>SpillReaderWriter.ReplayPartitionAsync</c>,
    /// exposing a <c>Current</c>+<c>ReadNextAsync</c> interface for the k-way merge
    /// heap. Each batch is held until exhausted (intra-batch advance is just an index
    /// bump, no I/O); when it's exhausted we return the batch to the pool and pull
    /// the next from the underlying enumerator. Sort keys are recomputed against the
    /// live current row each advance — runs spill DataValue[]s without keys, so the
    /// merge phase re-evaluates against each batch's arena.
    /// </summary>
    private sealed class RunReader : IAsyncDisposable
    {
        private readonly SpillReaderWriter _run;
        private readonly Pool _pool;
        private readonly IAsyncEnumerator<RowBatch> _enumerator;
        private readonly OrderByOperator _owner;
        private readonly ExpressionEvaluator _evaluator;
        private readonly ExecutionContext _context;
        private readonly SidecarRegistry? _sidecarRegistry;
        private RowBatch? _currentBatch;
        private int _currentIndex;
        private DataValue[] _currentKeys;
        private bool _disposed;

        public RunReader(
            SpillReaderWriter run,
            ColumnLookup schema,
            ExecutionContext context,
            Pool pool,
            OrderByOperator owner,
            ExpressionEvaluator evaluator)
        {
            _run = run;
            _pool = pool;
            _enumerator = run.ReplayPartitionAsync(context, schema, partition: 0).GetAsyncEnumerator(context.CancellationToken);
            _currentIndex = -1;
            _owner = owner;
            _evaluator = evaluator;
            _context = context;
            _sidecarRegistry = context.SidecarRegistry;
            _currentKeys = Array.Empty<DataValue>();
        }

        public Row Current => _currentBatch![_currentIndex];

        public RowBatch CurrentBatch => _currentBatch!;

        public int CurrentIndex => _currentIndex;

        public DataValue[] CurrentKeys => _currentKeys;

        public async ValueTask<bool> ReadNextAsync()
        {
            if (_currentBatch is not null && _currentIndex + 1 < _currentBatch.Count)
            {
                _currentIndex++;
                _currentKeys = await _owner.EvaluateKeysAsync(
                    _evaluator, Current, _currentBatch.Arena, _currentBatch.Arena,
                    _sidecarRegistry, _context.Types, _context.CancellationToken).ConfigureAwait(false);
                return true;
            }

            if (_currentBatch is not null)
            {
                _pool.ReturnRowBatch(_currentBatch);
                _currentBatch = null;
            }

            while (await _enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                RowBatch next = _enumerator.Current;
                if (next.Count > 0)
                {
                    _currentBatch = next;
                    _currentIndex = 0;
                    _currentKeys = await _owner.EvaluateKeysAsync(
                        _evaluator, Current, _currentBatch.Arena, _currentBatch.Arena,
                        _sidecarRegistry, _context.Types, _context.CancellationToken).ConfigureAwait(false);
                    return true;
                }
                _pool.ReturnRowBatch(next);
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            if (_currentBatch is not null)
            {
                _pool.ReturnRowBatch(_currentBatch);
                _currentBatch = null;
            }
            await _enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
