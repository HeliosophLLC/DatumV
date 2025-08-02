using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes set operations (UNION, INTERSECT, EXCEPT) over two input operator branches.
/// Supports both ALL (multiset) and DISTINCT (set) semantics for each operation type.
/// <para>
/// <strong>UNION ALL</strong> concatenates both streams without deduplication.
/// <strong>UNION</strong> (distinct) concatenates and deduplicates using a hash set,
/// with spill-to-disk when the <see cref="ExecutionContext.MemoryBudgetBytes"/> is exceeded.
/// </para>
/// <para>
/// <strong>INTERSECT</strong> and <strong>EXCEPT</strong> materialise the right branch
/// into a hash structure, then probe with rows from the left branch.
/// ALL variants use counted multisets; distinct variants use simple hash sets.
/// When the memory budget is exceeded, rows are spilled to hash-partitioned disk files
/// and processed partition-by-partition in a drain phase.
/// </para>
/// </summary>
internal sealed class SetOperationOperator : IQueryOperator, IDisposable
{
    /// <summary>Number of hash partitions used when spilling to disk.</summary>
    private const int SpillPartitionCount = 64;

    private readonly IQueryOperator _left;
    private readonly IQueryOperator _right;
    private readonly SetOperationType _operationType;
    private readonly bool _all;
    private string? _spillDirectory;

    /// <summary>
    /// Creates a new set operation operator combining two input branches.
    /// </summary>
    /// <param name="left">The left (first) input operator.</param>
    /// <param name="right">The right (second) input operator.</param>
    /// <param name="operationType">The type of set operation (Union, Intersect, or Except).</param>
    /// <param name="all">Whether to use ALL (multiset) semantics, preserving duplicates.</param>
    public SetOperationOperator(
        IQueryOperator left,
        IQueryOperator right,
        SetOperationType operationType,
        bool all)
    {
        _left = left;
        _right = right;
        _operationType = operationType;
        _all = all;
    }

    /// <summary>The left input operator.</summary>
    public IQueryOperator Left => _left;

    /// <summary>The right input operator.</summary>
    public IQueryOperator Right => _right;

    /// <summary>The type of set operation.</summary>
    public SetOperationType OperationType => _operationType;

    /// <summary>Whether ALL (multiset) semantics are used.</summary>
    public bool All => _all;

    /// <inheritdoc />
    public IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
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
    public void Dispose()
    {
        CleanupSpillDirectory();
    }

    /// <summary>
    /// UNION ALL: concatenates left then right without deduplication.
    /// </summary>
    private async IAsyncEnumerable<Row> ExecuteUnionAllAsync(ExecutionContext context)
    {
        await foreach (Row row in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }

        await foreach (Row row in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    /// <summary>
    /// UNION DISTINCT: concatenates both streams with hash-based deduplication,
    /// spilling to disk when the memory budget is exceeded.
    /// </summary>
    private async IAsyncEnumerable<Row> ExecuteUnionDistinctAsync(ExecutionContext context)
    {
        long? memoryBudget = context.MemoryBudgetBytes;
        MemoryEstimator? estimator = memoryBudget.HasValue ? new MemoryEstimator() : null;

        BinaryWriter?[]? spillWriters = null;
        bool[]? spillSchemaWritten = null;
        string[]? spillPaths = null;
        string[]? schemaNames = null;
        Dictionary<string, int>? schemaNameIndex = null;
        bool spilling = false;

        HashSet<DataValue>? singleKeySet = null;
        HashSet<CompositeKey>? compositeKeySet = null;
        int columnCount = -1;

        try
        {
            // Process both left and right through the same dedup logic.
            await foreach (Row row in ConcatenateAsync(_left, _right, context).ConfigureAwait(false))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

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
                            if (!spilling)
                            {
                                spilling = true;
                                _spillDirectory = Path.Combine(
                                    Path.GetTempPath(),
                                    $"datum-setop-{Guid.NewGuid():N}");
                                Directory.CreateDirectory(_spillDirectory);
                                spillWriters = new BinaryWriter[SpillPartitionCount];
                                spillSchemaWritten = new bool[SpillPartitionCount];
                                spillPaths = new string[SpillPartitionCount];

                                schemaNames = new string[row.FieldCount];
                                for (int index = 0; index < row.FieldCount; index++)
                                {
                                    schemaNames[index] = row.ColumnNames[index];
                                }
                            }

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
            }

            // Drain spilled partitions.
            if (spilling)
            {
                FlushSpillWriters(spillWriters!);

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

    /// <summary>
    /// INTERSECT DISTINCT: materialises the right branch into a hash set, then
    /// emits left rows that appear in the set (each emitted at most once).
    /// </summary>
    private async IAsyncEnumerable<Row> ExecuteIntersectDistinctAsync(ExecutionContext context)
    {
        HashSet<DataValue>? rightSingleSet = null;
        HashSet<CompositeKey>? rightCompositeSet = null;
        int columnCount = -1;

        // Materialise the right branch.
        await foreach (Row row in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (columnCount == -1)
            {
                columnCount = row.FieldCount;
                if (columnCount == 1)
                {
                    rightSingleSet = new HashSet<DataValue>();
                }
                else
                {
                    rightCompositeSet = new HashSet<CompositeKey>();
                }
            }

            AddRowToSet(row, columnCount, rightSingleSet, rightCompositeSet);
        }

        // If right was empty, no rows can match.
        if (columnCount == -1)
        {
            yield break;
        }

        // Track which left rows have already been emitted to ensure distinct output.
        HashSet<DataValue>? emittedSingleSet = columnCount == 1 ? new() : null;
        HashSet<CompositeKey>? emittedCompositeSet = columnCount != 1 ? new() : null;

        await foreach (Row row in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            bool inRight = ContainsRow(row, columnCount, rightSingleSet, rightCompositeSet);
            if (!inRight)
            {
                continue;
            }

            // Only emit each distinct row once.
            bool isNew;
            if (columnCount == 1)
            {
                isNew = emittedSingleSet!.Add(row[0]);
            }
            else
            {
                DataValue[] parts = new DataValue[columnCount];
                for (int index = 0; index < columnCount; index++)
                {
                    parts[index] = row[index];
                }

                isNew = emittedCompositeSet!.Add(new CompositeKey(parts));
            }

            if (isNew)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// INTERSECT ALL: materialises the right branch into a counted multiset, then
    /// emits left rows up to their count in the right branch.
    /// </summary>
    private async IAsyncEnumerable<Row> ExecuteIntersectAllAsync(ExecutionContext context)
    {
        Dictionary<DataValue, int>? rightSingleCounts = null;
        Dictionary<CompositeKey, int>? rightCompositeCounts = null;
        int columnCount = -1;

        // Count occurrences of each row in the right branch.
        await foreach (Row row in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (columnCount == -1)
            {
                columnCount = row.FieldCount;
                if (columnCount == 1)
                {
                    rightSingleCounts = new Dictionary<DataValue, int>();
                }
                else
                {
                    rightCompositeCounts = new Dictionary<CompositeKey, int>();
                }
            }

            IncrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts);
        }

        if (columnCount == -1)
        {
            yield break;
        }

        // Emit left rows, decrementing their count in the right multiset.
        await foreach (Row row in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (DecrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// EXCEPT DISTINCT: materialises the right branch into a hash set, then
    /// emits left rows that are not in the set (each emitted at most once).
    /// </summary>
    private async IAsyncEnumerable<Row> ExecuteExceptDistinctAsync(ExecutionContext context)
    {
        HashSet<DataValue>? rightSingleSet = null;
        HashSet<CompositeKey>? rightCompositeSet = null;
        int columnCount = -1;

        // Materialise the right branch.
        await foreach (Row row in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (columnCount == -1)
            {
                columnCount = row.FieldCount;
                if (columnCount == 1)
                {
                    rightSingleSet = new HashSet<DataValue>();
                }
                else
                {
                    rightCompositeSet = new HashSet<CompositeKey>();
                }
            }

            AddRowToSet(row, columnCount, rightSingleSet, rightCompositeSet);
        }

        // Track emitted rows for distinct output.
        HashSet<DataValue>? emittedSingleSet = null;
        HashSet<CompositeKey>? emittedCompositeSet = null;

        await foreach (Row row in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Handle the case where right was empty — initialise column count from left.
            if (columnCount == -1)
            {
                columnCount = row.FieldCount;
            }

            if (emittedSingleSet is null && emittedCompositeSet is null)
            {
                if (columnCount == 1)
                {
                    emittedSingleSet = new HashSet<DataValue>();
                }
                else
                {
                    emittedCompositeSet = new HashSet<CompositeKey>();
                }
            }

            bool inRight = ContainsRow(row, columnCount, rightSingleSet, rightCompositeSet);
            if (inRight)
            {
                continue;
            }

            // Only emit each distinct row once.
            bool isNew;
            if (columnCount == 1)
            {
                isNew = emittedSingleSet!.Add(row[0]);
            }
            else
            {
                DataValue[] parts = new DataValue[columnCount];
                for (int index = 0; index < columnCount; index++)
                {
                    parts[index] = row[index];
                }

                isNew = emittedCompositeSet!.Add(new CompositeKey(parts));
            }

            if (isNew)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// EXCEPT ALL: materialises the right branch into a counted multiset, then
    /// emits left rows whose count exceeds their right-side count.
    /// </summary>
    private async IAsyncEnumerable<Row> ExecuteExceptAllAsync(ExecutionContext context)
    {
        Dictionary<DataValue, int>? rightSingleCounts = null;
        Dictionary<CompositeKey, int>? rightCompositeCounts = null;
        int columnCount = -1;

        // Count occurrences of each row in the right branch.
        await foreach (Row row in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (columnCount == -1)
            {
                columnCount = row.FieldCount;
                if (columnCount == 1)
                {
                    rightSingleCounts = new Dictionary<DataValue, int>();
                }
                else
                {
                    rightCompositeCounts = new Dictionary<CompositeKey, int>();
                }
            }

            IncrementCount(row, columnCount, rightSingleCounts, rightCompositeCounts);
        }

        // Emit left rows, subtracting their count from the right multiset.
        await foreach (Row row in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Handle the case where right was empty — all left rows are emitted.
            if (columnCount == -1)
            {
                yield return row;
                continue;
            }

            bool shouldSubtract = DecrementCount(
                row, columnCount, rightSingleCounts, rightCompositeCounts);

            // DecrementCount returns true when the row's right-side count was > 0,
            // meaning this occurrence is "cancelled out" by the right branch.
            if (!shouldSubtract)
            {
                yield return row;
            }
        }
    }

    // ---------------------------------------------------------------
    //  Shared helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Concatenates two operator streams sequentially.
    /// </summary>
    private static async IAsyncEnumerable<Row> ConcatenateAsync(
        IQueryOperator first,
        IQueryOperator second,
        ExecutionContext context)
    {
        await foreach (Row row in first.ExecuteAsync(context).ConfigureAwait(false))
        {
            yield return row;
        }

        await foreach (Row row in second.ExecuteAsync(context).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <summary>
    /// Adds a row's key to the appropriate hash set.
    /// </summary>
    private static void AddRowToSet(
        Row row,
        int columnCount,
        HashSet<DataValue>? singleKeySet,
        HashSet<CompositeKey>? compositeKeySet)
    {
        if (columnCount == 1)
        {
            singleKeySet!.Add(row[0]);
        }
        else
        {
            DataValue[] parts = new DataValue[columnCount];
            for (int index = 0; index < columnCount; index++)
            {
                parts[index] = row[index];
            }

            compositeKeySet!.Add(new CompositeKey(parts));
        }
    }

    /// <summary>
    /// Tests whether a row's key is contained in the hash set.
    /// </summary>
    private static bool ContainsRow(
        Row row,
        int columnCount,
        HashSet<DataValue>? singleKeySet,
        HashSet<CompositeKey>? compositeKeySet)
    {
        if (singleKeySet is null && compositeKeySet is null)
        {
            return false;
        }

        if (columnCount == 1)
        {
            return singleKeySet!.Contains(row[0]);
        }

        DataValue[] parts = new DataValue[columnCount];
        for (int index = 0; index < columnCount; index++)
        {
            parts[index] = row[index];
        }

        return compositeKeySet!.Contains(new CompositeKey(parts));
    }

    /// <summary>
    /// Increments the count for a row's key in the counted multiset.
    /// </summary>
    private static void IncrementCount(
        Row row,
        int columnCount,
        Dictionary<DataValue, int>? singleCounts,
        Dictionary<CompositeKey, int>? compositeCounts)
    {
        if (columnCount == 1)
        {
            DataValue key = row[0];
            singleCounts![key] = singleCounts.GetValueOrDefault(key) + 1;
        }
        else
        {
            DataValue[] parts = new DataValue[columnCount];
            for (int index = 0; index < columnCount; index++)
            {
                parts[index] = row[index];
            }

            CompositeKey compositeKey = new(parts);
            compositeCounts![compositeKey] = compositeCounts.GetValueOrDefault(compositeKey) + 1;
        }
    }

    /// <summary>
    /// Decrements the count for a row's key in the counted multiset.
    /// Returns true if the count was positive (row was present) and was decremented,
    /// false if the row was not present or already exhausted.
    /// </summary>
    private static bool DecrementCount(
        Row row,
        int columnCount,
        Dictionary<DataValue, int>? singleCounts,
        Dictionary<CompositeKey, int>? compositeCounts)
    {
        if (singleCounts is null && compositeCounts is null)
        {
            return false;
        }

        if (columnCount == 1)
        {
            DataValue key = row[0];
            if (singleCounts!.TryGetValue(key, out int count) && count > 0)
            {
                singleCounts[key] = count - 1;
                return true;
            }

            return false;
        }

        DataValue[] parts = new DataValue[columnCount];
        for (int index = 0; index < columnCount; index++)
        {
            parts[index] = row[index];
        }

        CompositeKey compositeKey = new(parts);
        if (compositeCounts!.TryGetValue(compositeKey, out int compositeCount) && compositeCount > 0)
        {
            compositeCounts[compositeKey] = compositeCount - 1;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------
    //  Spill infrastructure (shared with UNION DISTINCT)
    // ---------------------------------------------------------------

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
            paths[partition] = Path.Combine(_spillDirectory!, $"setop_{partition}.spill");
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
