using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Sorts the output of a child operator by one or more expressions.
/// When <see cref="TopNRows"/> is set, uses a bounded max-heap to retain
/// only the top N rows in O(n log N) time and O(N) memory. Otherwise,
/// materializes all rows and sorts them.
/// <para>
/// When <see cref="ExecutionContext.MemoryBudgetBytes"/> is set and the sort is
/// unbounded, the operator spills sorted runs to disk when estimated memory usage
/// exceeds the budget and merges them with a k-way merge at the end.
/// </para>
/// </summary>
public sealed class OrderByOperator : IQueryOperator, IDisposable
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<OrderByItem> _orderByItems;
    private readonly int? _topNRows;
    private string? _spillDirectory;

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

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        if (_topNRows is int topN and > 0)
        {
            List<Row> rows = await CollectTopNAsync(topN, evaluator, context).ConfigureAwait(false);
            rows.Sort((left, right) => CompareRows(left, right, evaluator));

            foreach (Row row in rows)
            {
                yield return row;
            }

            yield break;
        }

        // Unbounded sort — spill to disk when memory budget is exceeded.
        List<string> sortedRunPaths = new();

        try
        {
            List<Row> buffer = await CollectAllWithSpillAsync(
                context, evaluator, sortedRunPaths).ConfigureAwait(false);

            if (sortedRunPaths.Count == 0)
            {
                // Everything fit in memory — sort and emit.
                buffer.Sort((left, right) => CompareRows(left, right, evaluator));

                foreach (Row row in buffer)
                {
                    yield return row;
                }
            }
            else
            {
                // Flush any remaining in-memory rows as the final sorted run.
                if (buffer.Count > 0)
                {
                    buffer.Sort((left, right) => CompareRows(left, right, evaluator));
                    string runPath = Path.Combine(_spillDirectory!, $"run_{sortedRunPaths.Count}.spill");
                    WriteSortedRun(runPath, buffer);
                    sortedRunPaths.Add(runPath);
                    buffer.Clear();
                }

                // K-way merge all sorted runs.
                await foreach (Row row in MergeSortedRunsAsync(
                    sortedRunPaths, evaluator, context.CancellationToken).ConfigureAwait(false))
                {
                    yield return row;
                }
            }
        }
        finally
        {
            CleanupSpillDirectory();
        }
    }

    /// <summary>
    /// Materializes rows from the source, spilling sorted runs to disk when
    /// <see cref="ExecutionContext.MemoryBudgetBytes"/> is exceeded. Returns
    /// any rows still in memory after the source is exhausted.
    /// </summary>
    private async Task<List<Row>> CollectAllWithSpillAsync(
        ExecutionContext context,
        ExpressionEvaluator evaluator,
        List<string> sortedRunPaths)
    {
        List<Row> buffer = new();
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            buffer.Add(row);

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
                    // Sort the in-memory buffer and write it as a sorted run.
                    if (_spillDirectory is null)
                    {
                        _spillDirectory = Path.Combine(
                            Path.GetTempPath(), $"datum-orderby-{Guid.NewGuid():N}");
                        Directory.CreateDirectory(_spillDirectory);

                        if (ExecutionTracer.IsEnabled)
                        {
                            ExecutionTracer.Write(
                                $"ORDER BY spill start  budget={ExecutionTracer.FormatBytes(memoryBudget.Value)}  estimated={ExecutionTracer.FormatBytes(estimatedMemory)}  rows={buffer.Count}");
                        }
                    }

                    buffer.Sort((left, right) => CompareRows(left, right, evaluator));

                    string runPath = Path.Combine(_spillDirectory, $"run_{sortedRunPaths.Count}.spill");
                    WriteSortedRun(runPath, buffer);
                    sortedRunPaths.Add(runPath);

                    // Reset the buffer and estimator for the next run.
                    buffer.Clear();
                    estimator = new MemoryEstimator();
                }
                else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                {
                    estimator.EscalateToEveryRow();
                }
            }
        }

        if (ExecutionTracer.IsEnabled && sortedRunPaths.Count > 0)
        {
            ExecutionTracer.Write(
                $"ORDER BY spill done  runs={sortedRunPaths.Count}  remaining_buffer={buffer.Count}");
        }

        return buffer;
    }

    /// <summary>
    /// Retains only the top N rows using a bounded max-heap. The heap keeps
    /// the "worst" row (last in sort order) at the top so it can be evicted
    /// when a better row arrives. After streaming all source rows, the heap
    /// contains exactly the top N rows (or fewer if the source is smaller).
    /// </summary>
    private async Task<List<Row>> CollectTopNAsync(
        int topN, ExpressionEvaluator evaluator, ExecutionContext context)
    {
        // PriorityQueue is a min-heap. Using reversed comparison makes the
        // "worst" row (last in desired sort order) the one dequeued first,
        // turning it into a max-heap for eviction purposes.
        PriorityQueue<Row, Row> heap = new(
            Comparer<Row>.Create(
                (left, right) => -CompareRows(left, right, evaluator)));

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            if (heap.Count < topN)
            {
                heap.Enqueue(row, row);
            }
            else
            {
                // EnqueueDequeue adds the new row and immediately removes the
                // worst. If the new row is worse than all current rows, it is
                // the one removed — effectively a no-op.
                heap.EnqueueDequeue(row, row);
            }
        }

        List<Row> rows = new(heap.Count);

        while (heap.Count > 0)
        {
            rows.Add(heap.Dequeue());
        }

        return rows;
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
        // Nulls sort last.
        if (left.IsNull && right.IsNull) return 0;
        if (left.IsNull) return 1;
        if (right.IsNull) return -1;

        return left.Kind switch
        {
            DataKind.Scalar => left.AsScalar().CompareTo(right.AsScalar()),
            DataKind.UInt8 => left.AsUInt8().CompareTo(right.AsUInt8()),
            DataKind.String => string.Compare(
                left.AsString(), right.AsString(), StringComparison.Ordinal),
            DataKind.Date => left.AsDate().CompareTo(right.AsDate()),
            DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
            _ => 0,
        };
    }

    // ---------------------------------------------------------------
    //  Spill-to-disk external sort infrastructure
    // ---------------------------------------------------------------

    /// <summary>
    /// Writes a pre-sorted list of rows to a binary spill file.
    /// </summary>
    private static void WriteSortedRun(string path, List<Row> sortedRows)
    {
        using FileStream fileStream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using BinaryWriter writer = new(fileStream);

        bool schemaWritten = false;

        foreach (Row row in sortedRows)
        {
            if (!schemaWritten)
            {
                RowSerializer.WriteSchema(writer, row);
                schemaWritten = true;
            }

            RowSerializer.WriteRow(writer, row);
        }
    }

    /// <summary>
    /// Performs a k-way merge of pre-sorted run files, yielding rows in global sort order.
    /// Uses a priority queue where each entry tracks which run it came from, so the next
    /// row from that run can be loaded when the current one is dequeued.
    /// </summary>
    private async IAsyncEnumerable<Row> MergeSortedRunsAsync(
        List<string> runPaths,
        ExpressionEvaluator evaluator,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Open all run files and read their first row.
        List<RunReader> readers = new(runPaths.Count);

        try
        {
            foreach (string path in runPaths)
            {
                RunReader runReader = new(path);

                if (runReader.ReadNext())
                {
                    readers.Add(runReader);
                }
                else
                {
                    runReader.Dispose();
                }
            }

            if (readers.Count == 0)
            {
                yield break;
            }

            // Build a min-heap keyed by the current row of each run.
            PriorityQueue<RunReader, RunReader> heap = new(
                Comparer<RunReader>.Create(
                    (a, b) => CompareRows(a.Current!, b.Current!, evaluator)));

            foreach (RunReader reader in readers)
            {
                heap.Enqueue(reader, reader);
            }

            while (heap.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RunReader winner = heap.Dequeue();
                yield return winner.Current!;

                if (winner.ReadNext())
                {
                    heap.Enqueue(winner, winner);
                }
                else
                {
                    winner.Dispose();
                }
            }
        }
        finally
        {
            foreach (RunReader reader in readers)
            {
                reader.Dispose();
            }
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
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

    /// <summary>
    /// Reads rows sequentially from a single sorted run file.
    /// </summary>
    private sealed class RunReader : IDisposable
    {
        private readonly FileStream _fileStream;
        private readonly BinaryReader _binaryReader;
        private readonly string[] _schemaNames;
        private readonly Dictionary<string, int> _schemaNameIndex;
        private bool _disposed;

        /// <summary>Creates a reader for the given spill file.</summary>
        /// <param name="path">Path to the sorted run spill file.</param>
        public RunReader(string path)
        {
            _fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 65536);
            _binaryReader = new BinaryReader(_fileStream);
            RowSerializer.ReadSchema(_binaryReader, out _schemaNames, out _schemaNameIndex);
        }

        /// <summary>The most recently read row, or <see langword="null"/> before the first read.</summary>
        public Row? Current { get; private set; }

        /// <summary>
        /// Reads the next row from the run file. Returns <see langword="false"/> when the file is exhausted.
        /// </summary>
        public bool ReadNext()
        {
            if (_fileStream.Position >= _fileStream.Length)
            {
                Current = null;
                return false;
            }

            Current = RowSerializer.ReadRow(_binaryReader, _schemaNames, _schemaNameIndex);
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _binaryReader.Dispose();
            _fileStream.Dispose();
        }
    }
}
