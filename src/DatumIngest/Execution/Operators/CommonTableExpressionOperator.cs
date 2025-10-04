using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Operator that provides the result set of a Common Table Expression (CTE).
/// Supports two execution modes controlled by the <see cref="IsMaterialized"/> flag:
/// <list type="bullet">
/// <item>
/// <term>Inlined</term>
/// <description>
/// Each call to <see cref="ExecuteAsync"/> re-executes the inner operator tree,
/// behaving like a subquery at each reference site.
/// </description>
/// </item>
/// <item>
/// <term>Materialized</term>
/// <description>
/// The first call to <see cref="ExecuteAsync"/> fully consumes the inner operator
/// and buffers the result set into pool-owned <see cref="RowBatch"/> objects.
/// Subsequent calls replay by copying cached values into fresh output batches.
/// When a memory budget is configured and the buffer exceeds it, rows are spilled
/// to a temporary file via <see cref="RowSerializer"/> and replayed from disk.
/// </description>
/// </item>
/// </list>
/// </summary>
internal sealed class CommonTableExpressionOperator : IQueryOperator, IDisposable
{
    private readonly IQueryOperator _innerOperator;
    private readonly string _name;
    private readonly bool _isMaterialized;
    private readonly IReadOnlyList<string>? _explicitColumnNames;

    private List<RowBatch>? _materializedBatches;
    private LocalBufferPool? _cachePool;
    private bool _materialized;
    private string? _spillFilePath;
    private string[]? _spillSchemaNames;
    private Dictionary<string, int>? _spillSchemaNameIndex;

    /// <summary>
    /// Creates a new CTE operator.
    /// </summary>
    /// <param name="innerOperator">The operator tree for the CTE body.</param>
    /// <param name="name">The CTE name used as the table alias.</param>
    /// <param name="isMaterialized">
    /// When <see langword="true"/>, the inner result is computed once and buffered.
    /// When <see langword="false"/>, each reference re-executes the inner operator.
    /// </param>
    /// <param name="explicitColumnNames">
    /// Optional column names from the CTE definition that rename the inner query's output
    /// columns positionally (e.g. <c>WITH cte(a, b) AS (...)</c>).
    /// </param>
    public CommonTableExpressionOperator(
        IQueryOperator innerOperator,
        string name,
        bool isMaterialized,
        IReadOnlyList<string>? explicitColumnNames = null)
    {
        _innerOperator = innerOperator;
        _name = name;
        _isMaterialized = isMaterialized;
        _explicitColumnNames = explicitColumnNames;
    }

    /// <summary>The CTE name.</summary>
    public string Name => _name;

    /// <summary>Whether this CTE materializes its result set.</summary>
    public bool IsMaterialized => _isMaterialized;

    /// <summary>The inner operator tree.</summary>
    public IQueryOperator InnerOperator => _innerOperator;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["name"] = _name,
            ["mode"] = _isMaterialized ? "materialized" : "inline",
        };

        return new OperatorPlanDescription("CTE")
        {
            Properties = properties,
            Children = [(InnerOperator, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        LocalBufferPool pool = context.LocalBufferPool;

        if (!_isMaterialized)
        {
            RowBatch? outputBatch = null;
            await foreach (RowBatch inputBatch in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    Row row = inputBatch[i];
                    outputBatch ??= pool.RentBatch(context.BatchSize);
                    outputBatch.Add(RenameColumnsIfNeeded(row));
                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }

                inputBatch.Return();
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }

            yield break;
        }

        // Materialized path: compute once, replay on subsequent calls.
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
        else if (_materializedBatches is not null)
        {
            // Replay cached rows by copying values into fresh output batches.
            // The cache owns its DataValue[] arrays; the output batch owns copies.
            RowBatch? outputBatch = null;
            foreach (RowBatch cachedBatch in _materializedBatches)
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
    /// Consumes the inner operator fully, buffering rows into pool-owned
    /// <see cref="RowBatch"/> objects. Each input row's <see cref="DataValue"/>
    /// values are copied into fresh pool-rented arrays so the cache is
    /// independent of the input batch lifecycle. Spills to disk when the
    /// memory budget is exceeded.
    /// </summary>
    private async Task MaterializeAsync(ExecutionContext context)
    {
        LocalBufferPool pool = context.LocalBufferPool;
        _cachePool = pool;
        _materializedBatches = new List<RowBatch>();
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        BinaryWriter? spillWriter = null;
        bool schemaWritten = false;

        // Schema arrays shared across all cached rows (built from first row).
        string[]? cacheNames = null;
        Dictionary<string, int>? cacheNameIndex = null;
        RowBatch? cacheBatch = null;

        try
        {
            await foreach (RowBatch inputBatch in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    Row row = inputBatch[i];
                    context.CancellationToken.ThrowIfCancellationRequested();
                    context.QueryMeter?.ThrowIfExceeded();

                    if (_spillFilePath is not null)
                    {
                        // Already spilling — write directly to disk.
                        if (!schemaWritten)
                        {
                            RowSerializer.WriteSchema(spillWriter!, row);
                            CacheSpillSchema(row);
                            schemaWritten = true;
                        }

                        RowSerializer.WriteRow(spillWriter!, row);
                    }
                    else
                    {
                        // Build shared schema from the first row.
                        if (cacheNames is null)
                        {
                            cacheNames = new string[row.FieldCount];
                            for (int col = 0; col < row.FieldCount; col++)
                                cacheNames[col] = row.ColumnNames[col];
                            cacheNameIndex = new Dictionary<string, int>(
                                cacheNames.Length, StringComparer.OrdinalIgnoreCase);
                            for (int col = 0; col < cacheNames.Length; col++)
                                cacheNameIndex[cacheNames[col]] = col;
                        }

                        DataValue[] cacheValues = pool.RentCopy(row.RawValues);
                        Row cachedRow = new(cacheNames, cacheValues, cacheNameIndex!);

                        cacheBatch ??= pool.RentBatch(context.BatchSize);
                        cacheBatch.Add(cachedRow);
                        if (cacheBatch.IsFull)
                        {
                            _materializedBatches.Add(cacheBatch);
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
                                // Flush the current partial cache batch before spilling.
                                if (cacheBatch is not null)
                                {
                                    _materializedBatches.Add(cacheBatch);
                                    cacheBatch = null;
                                }

                                // Spill everything buffered so far plus future rows to disk.
                                SpillToDisk(ref spillWriter, ref schemaWritten, pool);
                            }
                            else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                            {
                                estimator.EscalateToEveryRow();
                            }
                        }
                    }
                }

                inputBatch.Return();
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

        // Add the last partial batch if not spilled.
        if (cacheBatch is not null && _spillFilePath is null)
        {
            _materializedBatches.Add(cacheBatch);
        }
        else if (cacheBatch is not null)
        {
            // Spill happened after partial batch was created but before it was added.
            pool.ReturnBatch(cacheBatch);
        }

        // If we spilled, drop the in-memory cache and return batches to the pool.
        if (_spillFilePath is not null)
        {
            foreach (RowBatch batch in _materializedBatches)
            {
                pool.ReturnBatch(batch);
            }

            _materializedBatches = null;
        }

        _materialized = true;
    }

    /// <summary>
    /// Transitions to spill mode: writes all cached rows to a temp file,
    /// then clears the in-memory cache.
    /// </summary>
    private void SpillToDisk(ref BinaryWriter? spillWriter, ref bool schemaWritten, LocalBufferPool pool)
    {
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            $"datum-cte-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDirectory);
        _spillFilePath = Path.Combine(spillDirectory, "cte.spill");

        FileStream fileStream = new(
            _spillFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        spillWriter = new BinaryWriter(fileStream);

        // Write all previously cached rows to disk, then return cache batches to the pool.
        foreach (RowBatch batch in _materializedBatches!)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row bufferedRow = batch[i];
                if (!schemaWritten)
                {
                    RowSerializer.WriteSchema(spillWriter, bufferedRow);
                    CacheSpillSchema(bufferedRow);
                    schemaWritten = true;
                }

                RowSerializer.WriteRow(spillWriter, bufferedRow);
            }

            pool.ReturnBatch(batch);
        }

        _materializedBatches!.Clear();
    }

    /// <summary>
    /// Caches schema arrays from the first row for the disk replay path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheSpillSchema(Row row)
    {
        if (_spillSchemaNames is not null)
        {
            return;
        }

        _spillSchemaNames = new string[row.FieldCount];
        for (int index = 0; index < row.FieldCount; index++)
        {
            _spillSchemaNames[index] = row.ColumnNames[index];
        }

        _spillSchemaNameIndex = new Dictionary<string, int>(
            _spillSchemaNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < _spillSchemaNames.Length; index++)
        {
            _spillSchemaNameIndex[_spillSchemaNames[index]] = index;
        }
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
        if (_materializedBatches is not null && _cachePool is not null)
        {
            foreach (RowBatch batch in _materializedBatches)
            {
                _cachePool.ReturnBatch(batch);
            }

            _materializedBatches = null;
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
}
