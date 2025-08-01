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
/// and buffers the result set. Subsequent calls replay the buffer. When a memory
/// budget is configured and the buffer exceeds it, rows are spilled to a temporary
/// file via <see cref="RowSerializer"/> and replayed from disk.
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

    private List<Row>? _materializedRows;
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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        if (!_isMaterialized)
        {
            await foreach (Row row in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                yield return RenameColumnsIfNeeded(row);
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
            await foreach (Row row in ReplayFromDiskAsync(context.CancellationToken).ConfigureAwait(false))
            {
                yield return RenameColumnsIfNeeded(row);
            }
        }
        else if (_materializedRows is not null)
        {
            foreach (Row row in _materializedRows)
            {
                yield return RenameColumnsIfNeeded(row);
            }
        }
    }

    /// <summary>
    /// Consumes the inner operator fully, buffering rows in memory.
    /// Spills to disk when the memory budget is exceeded.
    /// </summary>
    private async Task MaterializeAsync(ExecutionContext context)
    {
        _materializedRows = new List<Row>();
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;
        BinaryWriter? spillWriter = null;
        bool schemaWritten = false;

        try
        {
            await foreach (Row row in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
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
                    _materializedRows.Add(row);

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
                            // Spill everything buffered so far plus future rows to disk.
                            SpillToDisk(ref spillWriter, ref schemaWritten);
                        }
                        else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                        {
                            estimator.EscalateToEveryRow();
                        }
                    }
                }
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

        // If we spilled, drop the in-memory buffer reference to free memory.
        if (_spillFilePath is not null)
        {
            _materializedRows = null;
        }

        _materialized = true;
    }

    /// <summary>
    /// Transitions to spill mode: writes all buffered rows to a temp file,
    /// then clears the in-memory buffer.
    /// </summary>
    private void SpillToDisk(ref BinaryWriter? spillWriter, ref bool schemaWritten)
    {
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            $"datum-cte-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDirectory);
        _spillFilePath = Path.Combine(spillDirectory, "cte.spill");

        FileStream fileStream = new(
            _spillFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        spillWriter = new BinaryWriter(fileStream);

        // Write all previously buffered rows to disk.
        foreach (Row bufferedRow in _materializedRows!)
        {
            if (!schemaWritten)
            {
                RowSerializer.WriteSchema(spillWriter, bufferedRow);
                CacheSpillSchema(bufferedRow);
                schemaWritten = true;
            }

            RowSerializer.WriteRow(spillWriter, bufferedRow);
        }

        _materializedRows!.Clear();
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
    private async IAsyncEnumerable<Row> ReplayFromDiskAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FileStream fileStream = new(
            _spillFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);

        await using (fileStream.ConfigureAwait(false))
        {
            using BinaryReader reader = new(fileStream);

            RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);

            while (fileStream.Position < fileStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return RowSerializer.ReadRow(reader, names, nameIndex);
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
