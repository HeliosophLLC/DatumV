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
/// </summary>
public sealed class OrderByOperator : IQueryOperator, IDisposable
{
    private readonly IQueryOperator _source;
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
        IQueryOperator source,
        IReadOnlyList<OrderByItem> orderByItems,
        int? topNRows = null)
    {
        _source = source;
        _orderByItems = orderByItems;
        _topNRows = topNRows;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

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
    public OperatorPlanDescription DescribeForExplain()
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
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = new(
            context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);

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
        // Heap rows live in bufferArena: stabilised at insertion so input batches can
        // return to the pool immediately. The heap is a max-heap (reverse comparison)
        // — its peek is the worst-of-the-best, which is what gets evicted on overflow.
        Arena? bufferArena = null;
        ColumnLookup? schema = null;
        PriorityQueue<Row, Row> heap = new(
            Comparer<Row>.Create((left, right) => -CompareRows(left, right, evaluator)));
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
                            heap.Enqueue(stableRow, stableRow);
                        }
                        else
                        {
                            // Compare without allocating: peek the worst, decide if the
                            // new row beats it. If so, evict the worst (return its array
                            // to the pool) and insert the new row (rented + stabilised).
                            Row worst = heap.Peek();
                            if (CompareRows(sourceRow, worst, evaluator) < 0)
                            {
                                Row evicted = heap.Dequeue();
                                pool.ReturnRow(evicted);

                                DataValue[] copy = pool.RentAndCopyDataValues(
                                    sourceRow, inputBatch.Arena, bufferArena!);
                                Row stableRow = new(sourceRow.ColumnLookup, copy);
                                heap.Enqueue(stableRow, stableRow);
                            }
                        }
                    }
                }
                finally
                {
                    pool.ReturnRowBatch(inputBatch);
                }
            }

            if (heap.Count == 0) yield break;

            // Drain the heap into a list and sort ascending (the heap was max-ordered
            // for eviction; output wants the natural sort direction).
            List<Row> sorted = new(heap.Count);
            while (heap.Count > 0)
            {
                sorted.Add(heap.Dequeue());
            }
            sorted.Sort((left, right) => CompareRows(left, right, evaluator));

            for (int i = 0; i < sorted.Count; i++)
            {
                Row row = sorted[i];
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
                pool.ReturnRow(heap.Dequeue());
            }
            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            if (bufferArena is not null) pool.ReturnArena(bufferArena);
        }
    }

    // ───────── Unbounded path with optional spill ─────────

    private async IAsyncEnumerable<RowBatch> ExecuteUnboundedAsync(
        ExpressionEvaluator evaluator, ExecutionContext context, Pool pool)
    {
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        Arena? bufferArena = null;
        ColumnLookup? schema = null;
        List<Row> buffer = [];
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
                        bufferArena = pool.RentArena();
                    }

                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row sourceRow = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        DataValue[] copy = pool.RentAndCopyDataValues(
                            sourceRow, inputBatch.Arena, bufferArena!);
                        buffer.Add(new Row(sourceRow.ColumnLookup, copy));

                        if (estimator is not null)
                        {
                            if (estimator.ShouldSample())
                            {
                                estimator.RecordSample(sourceRow);
                            }

                            estimator.IncrementRowCount();
                            long estimatedMemory = estimator.EstimateTotalBytes();

                            if (estimatedMemory > memoryBudget!.Value)
                            {
                                // Sort the in-memory chunk and write it as a sorted run.
                                buffer.Sort((left, right) => CompareRows(left, right, evaluator));
                                SpillReaderWriter run = SpillSortedBuffer(
                                    pool, schema!, context, bufferArena!, buffer);
                                sortedRuns.Add(run);
                                SpillingTriggered = true;
                                SortedRunCount++;

                                // Reset for next chunk: fresh arena, fresh estimator,
                                // fresh buffer. Buffer rows' DataValue[]s were transferred
                                // to the run batch and consumed by the spiller's Write
                                // (which returned them to the pool); the buffer's list
                                // entries are now stale slot references.
                                buffer.Clear();
                                pool.ReturnArena(bufferArena!);
                                bufferArena = pool.RentArena();
                                estimator = new MemoryEstimator();
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
                    pool.ReturnRowBatch(inputBatch);
                }
            }

            if (sortedRuns.Count == 0)
            {
                // Everything fit in memory. Sort buffer and emit.
                buffer.Sort((left, right) => CompareRows(left, right, evaluator));

                for (int i = 0; i < buffer.Count; i++)
                {
                    Row row = buffer[i];
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
                buffer.Sort((left, right) => CompareRows(left, right, evaluator));
                SpillReaderWriter finalRun = SpillSortedBuffer(
                    pool, schema!, context, bufferArena!, buffer);
                sortedRuns.Add(finalRun);
                SortedRunCount++;
                buffer.Clear();
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
            foreach (Row row in buffer)
            {
                if (row.RawValues is not null) pool.ReturnRow(row);
            }
            buffer.Clear();

            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
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
        Pool pool, ColumnLookup schema, ExecutionContext context, Arena bufferArena, List<Row> sortedBuffer)
    {
        SpillReaderWriter run = new SpillReaderWriter(
            pool, schema, context.SpillDirectory, partitionCount: 1);

        RowBatch runBatch = pool.RentRowBatch(schema, sortedBuffer.Count, bufferArena);
        foreach (Row row in sortedBuffer)
        {
            runBatch.Add(row.RawValues);
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
        RowBatch? outputBatch = null;

        try
        {
            foreach (SpillReaderWriter run in runs)
            {
                RunReader reader = new(run, schema, context, pool);
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
                Comparer<RunReader>.Create((a, b) => CompareRows(a.Current, b.Current, evaluator)));
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
            if (outputBatch is not null) pool.ReturnRowBatch(outputBatch);
            foreach (RunReader r in readers)
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private int CompareRows(Row left, Row right, ExpressionEvaluator evaluator)
    {
        foreach (OrderByItem item in _orderByItems)
        {
            DataValue leftValue = evaluator.Evaluate(item.Expression, left);
            DataValue rightValue = evaluator.Evaluate(item.Expression, right);

            int comparison = CompareDataValues(leftValue, rightValue);

            if (item.Direction == SortDirection.Descending)
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
    /// Compares two <see cref="DataValue"/> instances for ordering. Nulls sort last.
    /// </summary>
    internal static int CompareDataValues(DataValue left, DataValue right)
    {
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return DataValueComparer.Compare(left, right);
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
    /// the next from the underlying enumerator.
    /// </summary>
    private sealed class RunReader : IAsyncDisposable
    {
        private readonly SpillReaderWriter _run;
        private readonly Pool _pool;
        private readonly IAsyncEnumerator<RowBatch> _enumerator;
        private RowBatch? _currentBatch;
        private int _currentIndex;
        private bool _disposed;

        public RunReader(SpillReaderWriter run, ColumnLookup schema, ExecutionContext context, Pool pool)
        {
            _run = run;
            _pool = pool;
            _enumerator = run.ReplayPartitionAsync(context, schema, partition: 0).GetAsyncEnumerator(context.CancellationToken);
            _currentIndex = -1;
        }

        public Row Current => _currentBatch![_currentIndex];

        public RowBatch CurrentBatch => _currentBatch!;

        public int CurrentIndex => _currentIndex;

        public async ValueTask<bool> ReadNextAsync()
        {
            if (_currentBatch is not null && _currentIndex + 1 < _currentBatch.Count)
            {
                _currentIndex++;
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
