using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Operator for recursive Common Table Expressions (WITH RECURSIVE).
/// Executes the anchor member once, then iterates the recursive member using
/// the previous iteration's output as the working table until no new rows are
/// produced or <see cref="ExecutionContext.MaxRecursionDepth"/> is reached.
/// All accumulated rows (anchor + all iterations) are yielded.
/// </summary>
/// <remarks>
/// Recursive CTEs are always materialized — the full result set must be computed
/// before any row can be yielded to the outer query, because the recursive member
/// depends on the running accumulation.
/// </remarks>
internal sealed class RecursiveCommonTableExpressionOperator : IQueryOperator, IDisposable
{
    private readonly IQueryOperator _anchorOperator;
    private readonly string _name;
    private readonly IReadOnlyList<string>? _explicitColumnNames;

    /// <summary>
    /// Factory that produces the recursive member's operator tree. Called once per
    /// iteration with a <see cref="WorkingTableOperator"/> that replays the previous
    /// iteration's rows.
    /// </summary>
    private readonly Func<IQueryOperator, IQueryOperator> _recursiveMemberFactory;

    private List<RowBatch>? _allBatches;
    private LocalBufferPool? _cachePool;
    private bool _materialized;
    private string? _spillFilePath;

    /// <summary>
    /// Creates a recursive CTE operator.
    /// </summary>
    /// <param name="anchorOperator">The operator tree for the anchor (non-recursive) member.</param>
    /// <param name="recursiveMemberFactory">
    /// A factory that, given a working-table operator representing the previous iteration's
    /// output, returns the recursive member's operator tree. The working-table operator is
    /// substituted where the CTE name appears in the recursive member's FROM clause.
    /// </param>
    /// <param name="name">The CTE name.</param>
    /// <param name="explicitColumnNames">Optional explicit column names from the CTE definition.</param>
    public RecursiveCommonTableExpressionOperator(
        IQueryOperator anchorOperator,
        Func<IQueryOperator, IQueryOperator> recursiveMemberFactory,
        string name,
        IReadOnlyList<string>? explicitColumnNames = null)
    {
        _anchorOperator = anchorOperator;
        _recursiveMemberFactory = recursiveMemberFactory;
        _name = name;
        _explicitColumnNames = explicitColumnNames;
    }

    /// <summary>The CTE name.</summary>
    public string Name => _name;

    /// <summary>The anchor operator tree.</summary>
    public IQueryOperator AnchorOperator => _anchorOperator;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Recursive CTE")
        {
            Properties = new Dictionary<string, string>
            {
                ["name"] = _name,
            },
            Children = [(AnchorOperator, "anchor")],
            Annotations = ["recursive member is generated at runtime"],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        LocalBufferPool pool = context.LocalBufferPool;

        if (!_materialized)
        {
            await MaterializeAsync(context).ConfigureAwait(false);
        }

        // Replay from disk if spilled, otherwise from memory.
        if (_spillFilePath is not null)
        {
            await foreach (RowBatch batch in ReplayFromDiskAsync(context).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        else if (_allBatches is not null)
        {
            // Replay by copying cached values into fresh output batches.
            RowBatch? outputBatch = null;
            foreach (RowBatch cachedBatch in _allBatches)
            {
                for (int i = 0; i < cachedBatch.Count; i++)
                {
                    Row cachedRow = cachedBatch[i];
                    DataValue[] outputValues = pool.RentCopy(cachedRow.RawValues);
                    Row outputRow = new(cachedRow.RawNames, outputValues, cachedRow.RawNameIndex);

                    outputBatch ??= pool.RentBatch(context.BatchSize);
                    outputBatch.Add(RenameColumnsIfNeeded(outputRow));
                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
    }

    /// <summary>
    /// Executes the anchor member, then iterates the recursive member until fixpoint
    /// or the max recursion depth is reached. All rows are cached with pool-rented
    /// <see cref="DataValue"/> arrays independent of the input batch lifecycle.
    /// </summary>
    private async Task MaterializeAsync(ExecutionContext context)
    {
        LocalBufferPool pool = context.LocalBufferPool;
        _cachePool = pool;
        _allBatches = new List<RowBatch>();
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        BinaryWriter? spillWriter = null;
        bool schemaWritten = false;
        int maxDepth = context.MaxRecursionDepth;

        // Schema arrays shared across all cached rows (built from first row).
        string[]? cacheNames = null;
        Dictionary<string, int>? cacheNameIndex = null;
        RowBatch? cacheBatch = null;

        try
        {
            // Execute anchor member.
            List<Row> workingTable = new();
            await foreach (RowBatch inputBatch in _anchorOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    Row row = inputBatch[i];
                    context.CancellationToken.ThrowIfCancellationRequested();
                    context.QueryMeter?.ThrowIfExceeded();

                    BuildCacheSchema(row, ref cacheNames, ref cacheNameIndex);

                    // Clone into pool-rented array for the cache/working table.
                    DataValue[] clonedValues = pool.RentCopy(row.RawValues);
                    Row clonedRow = new(cacheNames!, clonedValues, cacheNameIndex!);

                    workingTable.Add(clonedRow);
                    AddRow(clonedRow, ref cacheBatch, pool, estimator, memoryBudget,
                        ref spillWriter, ref schemaWritten);
                }

                inputBatch.Return();
            }

            // Iterate recursive member.
            for (int depth = 0; depth < maxDepth; depth++)
            {
                if (workingTable.Count == 0)
                {
                    break;
                }

                WorkingTableOperator workingTableOperator = new(workingTable, pool);
                IQueryOperator recursiveMember = _recursiveMemberFactory(workingTableOperator);

                List<Row> nextWorkingTable = new();
                await foreach (RowBatch inputBatch in recursiveMember.ExecuteAsync(context).ConfigureAwait(false))
                {
                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        Row row = inputBatch[i];
                        context.CancellationToken.ThrowIfCancellationRequested();
                        context.QueryMeter?.ThrowIfExceeded();

                        BuildCacheSchema(row, ref cacheNames, ref cacheNameIndex);

                        DataValue[] clonedValues = pool.RentCopy(row.RawValues);
                        Row clonedRow = new(cacheNames!, clonedValues, cacheNameIndex!);

                        nextWorkingTable.Add(clonedRow);
                        AddRow(clonedRow, ref cacheBatch, pool, estimator, memoryBudget,
                            ref spillWriter, ref schemaWritten);
                    }

                    inputBatch.Return();
                }

                // Previous working table rows share DataValue[] with _allBatches cache —
                // no separate cleanup needed.
                workingTable = nextWorkingTable;
            }

            if (workingTable.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Recursive CTE '{_name}' exceeded maximum recursion depth of {maxDepth}.");
            }
        }
        finally
        {
            if (spillWriter is not null)
            {
                spillWriter.Flush();
                spillWriter.Dispose();
            }
        }

        // Add the last partial batch.
        if (cacheBatch is not null && _spillFilePath is null)
        {
            _allBatches.Add(cacheBatch);
        }
        else if (cacheBatch is not null)
        {
            pool.ReturnBatch(cacheBatch);
        }

        if (_spillFilePath is not null)
        {
            foreach (RowBatch batch in _allBatches)
            {
                pool.ReturnBatch(batch);
            }

            _allBatches = null;
        }

        _materialized = true;
    }

    /// <summary>
    /// Builds shared schema arrays from the first row encountered.
    /// </summary>
    private static void BuildCacheSchema(
        Row row,
        ref string[]? cacheNames,
        ref Dictionary<string, int>? cacheNameIndex)
    {
        if (cacheNames is not null)
        {
            return;
        }

        cacheNames = new string[row.FieldCount];
        for (int col = 0; col < row.FieldCount; col++)
            cacheNames[col] = row.ColumnNames[col];
        cacheNameIndex = new Dictionary<string, int>(
            cacheNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int col = 0; col < cacheNames.Length; col++)
            cacheNameIndex[cacheNames[col]] = col;
    }

    /// <summary>
    /// Adds a row to the cache (in-memory batch or spilled to disk).
    /// </summary>
    private void AddRow(
        Row row,
        ref RowBatch? cacheBatch,
        LocalBufferPool pool,
        MemoryEstimator? estimator,
        long? memoryBudget,
        ref BinaryWriter? spillWriter,
        ref bool schemaWritten)
    {
        if (_spillFilePath is not null)
        {
            if (!schemaWritten)
            {
                RowSerializer.WriteSchema(spillWriter!, row);
                schemaWritten = true;
            }

            RowSerializer.WriteRow(spillWriter!, row);
            // Row was cloned into pool-rented array but is now serialized to disk.
            // Return its array to the pool.
            pool.ReturnValues(row);
        }
        else
        {
            cacheBatch ??= pool.RentBatch(1024);
            cacheBatch.Add(row);
            if (cacheBatch.IsFull)
            {
                _allBatches!.Add(cacheBatch);
                cacheBatch = null;
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
                    // Flush partial cache batch before spilling.
                    if (cacheBatch is not null)
                    {
                        _allBatches!.Add(cacheBatch);
                        cacheBatch = null;
                    }

                    SpillToDisk(ref spillWriter, ref schemaWritten, pool);
                }
                else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                {
                    estimator.EscalateToEveryRow();
                }
            }
        }
    }

    /// <summary>
    /// Transitions to spill mode, writing all cached rows to a temp file.
    /// </summary>
    private void SpillToDisk(ref BinaryWriter? spillWriter, ref bool schemaWritten, LocalBufferPool pool)
    {
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            $"datum-rcte-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDirectory);
        _spillFilePath = Path.Combine(spillDirectory, "rcte.spill");

        FileStream fileStream = new(
            _spillFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        spillWriter = new BinaryWriter(fileStream);

        foreach (RowBatch batch in _allBatches!)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row bufferedRow = batch[i];
                if (!schemaWritten)
                {
                    RowSerializer.WriteSchema(spillWriter, bufferedRow);
                    schemaWritten = true;
                }

                RowSerializer.WriteRow(spillWriter, bufferedRow);
            }

            pool.ReturnBatch(batch);
        }

        _allBatches!.Clear();
    }

    /// <summary>
    /// Replays rows from a spill file.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ReplayFromDiskAsync(ExecutionContext context)
    {
        LocalBufferPool pool = context.LocalBufferPool;
        FileStream fileStream = new(
            _spillFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);

        await using (fileStream.ConfigureAwait(false))
        {
            using BinaryReader reader = new(fileStream);

            RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);

            RowBatch? outputBatch = null;
            while (fileStream.Position < fileStream.Length)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                Row row = RowSerializer.ReadRow(reader, names, nameIndex);
                outputBatch ??= pool.RentBatch(context.BatchSize);
                outputBatch.Add(RenameColumnsIfNeeded(row));
                if (outputBatch.IsFull)
                {
                    yield return outputBatch;
                    outputBatch = null;
                }
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
    }

    /// <summary>
    /// Renames the output row's columns if the CTE definition provides explicit column names.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Row RenameColumnsIfNeeded(Row row)
    {
        if (_explicitColumnNames is null)
        {
            return row;
        }

        string[] renamedNames = new string[row.FieldCount];
        DataValue[] values = new DataValue[row.FieldCount];
        for (int index = 0; index < row.FieldCount; index++)
        {
            renamedNames[index] = index < _explicitColumnNames.Count
                ? _explicitColumnNames[index]
                : row.ColumnNames[index];
            values[index] = row[index];
        }

        return new Row(renamedNames, values);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Return cached batches to the pool if still held.
        if (_allBatches is not null && _cachePool is not null)
        {
            foreach (RowBatch batch in _allBatches)
            {
                _cachePool.ReturnBatch(batch);
            }

            _allBatches = null;
        }

        if (_spillFilePath is not null)
        {
            string? directory = Path.GetDirectoryName(_spillFilePath);
            if (directory is not null && Directory.Exists(directory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup — OS handles temp files on shutdown.
                }
            }

            _spillFilePath = null;
        }
    }

    /// <summary>
    /// Simple operator that replays an in-memory list of rows by copying
    /// cached values into fresh pool-rented output batches. The cached rows'
    /// <see cref="DataValue"/> arrays are owned by the recursive CTE's cache
    /// and must not be returned by downstream consumers.
    /// </summary>
    internal sealed class WorkingTableOperator : IQueryOperator
    {
        private readonly List<Row> _rows;
        private readonly LocalBufferPool _pool;

        /// <summary>
        /// Creates a working table operator from a snapshot of rows.
        /// </summary>
        /// <param name="rows">The rows to replay.</param>
        /// <param name="pool">The pool for renting output batch arrays.</param>
        public WorkingTableOperator(List<Row> rows, LocalBufferPool pool)
        {
            _rows = rows;
            _pool = pool;
        }

        /// <inheritdoc/>
        public OperatorPlanDescription DescribeForExplain()
        {
            return new OperatorPlanDescription("Working Table")
            {
                EstimatedRows = _rows.Count,
            };
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators
        /// <inheritdoc/>
        public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
        {
            RowBatch? outputBatch = null;
            foreach (Row row in _rows)
            {
                // Copy cached values into fresh pool-rented arrays so the output
                // batch can be safely ReturnBatch'd by downstream operators.
                DataValue[] outputValues = _pool.RentCopy(row.RawValues);
                Row outputRow = new(row.RawNames, outputValues, row.RawNameIndex);

                outputBatch ??= _pool.RentBatch(context.BatchSize);
                outputBatch.Add(outputRow);
                if (outputBatch.IsFull)
                {
                    yield return outputBatch;
                    outputBatch = null;
                }
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }
        }
#pragma warning restore CS1998
    }
}
