using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Streaming duplicate-elimination operator that yields only the first occurrence
/// of each distinct row from the source. Uses a <see cref="HashSet{T}"/> of
/// <see cref="DataValue"/> (single-column) or <see cref="CompositeKey"/> (multi-column)
/// to track seen rows.
/// <para>
/// When a <see cref="ExecutionContext.MemoryBudgetBytes"/> is configured and the in-memory
/// set exceeds the budget, the operator spills unseen rows to hash-partitioned disk files
/// via <see cref="RowSerializer"/>. After the source is exhausted, spilled partitions are
/// read back and deduplicated independently.
/// </para>
/// </summary>
internal sealed class DistinctOperator : IQueryOperator, IDisposable
{
    /// <summary>Number of spill partitions used when the memory budget is exceeded.</summary>
    private const int SpillPartitionCount = 64;

    private readonly IQueryOperator _source;
    private string? _spillDirectory;

    /// <summary>
    /// Creates a new distinct operator over the given source.
    /// </summary>
    /// <param name="source">The upstream operator whose output rows are deduplicated.</param>
    public DistinctOperator(IQueryOperator source)
    {
        _source = source;
    }

    /// <summary>The upstream operator.</summary>
    public IQueryOperator Source => _source;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        return new OperatorPlanDescription("Distinct")
        {
            Children = [(Source, null)],
            Warnings = ["materializes all unique rows in memory"],
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        // Spill state — lazily initialised only when the budget is exceeded.
        BinaryWriter?[]? spillWriters = null;
        bool[]? spillSchemaWritten = null;
        string[]? spillPaths = null;
        string[]? schemaNames = null;
        Dictionary<string, int>? schemaNameIndex = null;
        bool spilling = false;

        // Track distinct rows seen so far. The key type depends on column count;
        // we discover this from the first row.
        HashSet<DataValue>? singleKeySet = null;
        HashSet<CompositeKey>? compositeKeySet = null;
        int columnCount = -1;

        try
        {
            await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // Initialise key structure on first row.
                if (columnCount == -1)
                {
                    columnCount = row.FieldCount;
                    if (columnCount == 1)
                    {
                        singleKeySet = new HashSet<DataValue>();
                    }
                    else
                    {
                        compositeKeySet = new HashSet<CompositeKey>();
                    }
                }

                // Build the key for this row.
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
                    DataValue[] parts = new DataValue[columnCount];
                    for (int index = 0; index < columnCount; index++)
                    {
                        parts[index] = row[index];
                    }

                    CompositeKey compositeKey = new(parts);
                    isNew = compositeKeySet!.Add(compositeKey);
                    hashCode = compositeKey.GetHashCode();
                }

                if (isNew)
                {
                    // Memory estimation and spill check.
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
                            // Transition to spill mode: stop adding to the in-memory set
                            // and start writing new-but-unverified rows to disk partitions.
                            if (!spilling)
                            {
                                spilling = true;
                                _spillDirectory = Path.Combine(
                                    Path.GetTempPath(),
                                    $"datum-distinct-{Guid.NewGuid():N}");
                                Directory.CreateDirectory(_spillDirectory);
                                spillWriters = new BinaryWriter[SpillPartitionCount];
                                spillSchemaWritten = new bool[SpillPartitionCount];
                                spillPaths = new string[SpillPartitionCount];

                                // Cache schema from first row for serialization.
                                schemaNames = new string[row.FieldCount];
                                for (int index = 0; index < row.FieldCount; index++)
                                {
                                    schemaNames[index] = row.ColumnNames[index];
                                }
                            }

                            // Write the row that just exceeded the budget to a spill partition.
                            // It is already in the in-memory set so it won't be re-emitted,
                            // but future rows that hash to the same partition can be deduplicated
                            // against it during the drain phase.
                            WriteToSpillPartition(
                                row, hashCode, spillWriters!, spillSchemaWritten!, spillPaths!, schemaNames!);
                        }
                        else if (estimatedMemory > (long)(memoryBudget.Value * MemoryEstimator.EscalationThreshold))
                        {
                            estimator.EscalateToEveryRow();
                        }
                    }

                    yield return row;
                }
                else if (spilling)
                {
                    // Row was already in-memory set — no action needed.
                    // But if the row is NOT in the in-memory set *and* we are spilling,
                    // that case is handled above (isNew == true, spilling == true).
                }
            }

            // Drain phase: if we spilled, read each partition back and deduplicate.
            if (spilling)
            {
                FlushSpillWriters(spillWriters!);

                // Build schema for reading back.
                if (schemaNameIndex is null && schemaNames is not null)
                {
                    schemaNameIndex = new Dictionary<string, int>(
                        schemaNames.Length, StringComparer.OrdinalIgnoreCase);
                    for (int index = 0; index < schemaNames.Length; index++)
                    {
                        schemaNameIndex[schemaNames[index]] = index;
                    }
                }

                for (int partition = 0; partition < SpillPartitionCount; partition++)
                {
                    if (spillPaths![partition] is null)
                    {
                        continue;
                    }

                    // Build a per-partition set of rows that were already emitted (from
                    // the in-memory set) by re-hashing them. Only include rows whose hash
                    // maps to this partition.
                    HashSet<DataValue>? partitionSingleSet = columnCount == 1 ? new() : null;
                    HashSet<CompositeKey>? partitionCompositeSet = columnCount != 1 ? new() : null;

                    if (columnCount == 1)
                    {
                        foreach (DataValue existingKey in singleKeySet!)
                        {
                            if (AssignPartition(existingKey.GetHashCode()) == partition)
                            {
                                partitionSingleSet!.Add(existingKey);
                            }
                        }
                    }
                    else
                    {
                        foreach (CompositeKey existingKey in compositeKeySet!)
                        {
                            if (AssignPartition(existingKey.GetHashCode()) == partition)
                            {
                                partitionCompositeSet!.Add(existingKey);
                            }
                        }
                    }

                    // Read the spill file and emit any rows not already seen.
                    await foreach (Row spilledRow in ReadSpillPartitionAsync(
                        spillPaths[partition], schemaNames!, schemaNameIndex!, context.CancellationToken))
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();

                        bool spilledIsNew;
                        if (columnCount == 1)
                        {
                            spilledIsNew = partitionSingleSet!.Add(spilledRow[0]);
                        }
                        else
                        {
                            DataValue[] parts = new DataValue[columnCount];
                            for (int index = 0; index < columnCount; index++)
                            {
                                parts[index] = spilledRow[index];
                            }

                            spilledIsNew = partitionCompositeSet!.Add(new CompositeKey(parts));
                        }

                        if (spilledIsNew)
                        {
                            yield return spilledRow;
                        }
                    }
                }
            }
        }
        finally
        {
            CleanupSpillFiles(spillWriters);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CleanupSpillDirectory();
    }

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
            paths[partition] = Path.Combine(_spillDirectory!, $"distinct_{partition}.spill");
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

        // Skip the schema header (already known from the writing phase).
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
