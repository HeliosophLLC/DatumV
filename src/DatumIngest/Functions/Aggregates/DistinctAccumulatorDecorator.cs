using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Decorator that wraps an <see cref="IAggregateAccumulator"/> with a
/// <see cref="HashSet{T}"/> filter, ensuring only distinct argument values
/// are forwarded to the inner accumulator. This enables <c>COUNT(DISTINCT col)</c>,
/// <c>SUM(DISTINCT col)</c>, and similar aggregate DISTINCT semantics without
/// modifying the individual aggregate function implementations.
/// <para>
/// For single-argument aggregates (the common case), a <see cref="HashSet{DataValue}"/>
/// is used. For multi-argument aggregates, a <see cref="HashSet{CompositeKey}"/> provides
/// element-wise equality.
/// </para>
/// <para>
/// When a memory budget is provided, the decorator spills to hash-partitioned temporary
/// files once estimated memory exceeds the budget. Values are partitioned by their hash
/// code so each partition contains a non-overlapping subset of distinct values. During
/// the drain phase (triggered by <see cref="Result"/>), partitions are processed
/// sequentially with fresh accumulators and merged into the final result, keeping peak
/// memory proportional to the largest partition rather than the total distinct count.
/// </para>
/// </summary>
internal sealed class DistinctAccumulatorDecorator : IAggregateAccumulator, IDisposable
{
    /// <summary>Number of hash partitions used when spilling to disk.</summary>
    private const int SpillPartitionCount = 64;

    /// <summary>
    /// Conservative per-entry overhead estimate for the <see cref="HashSet{T}"/>.
    /// Combines the <see cref="DataValue"/> struct size with the hash set's internal
    /// entry bookkeeping (hash code, next pointer, value reference).
    /// </summary>
    private const long EstimatedBytesPerHashSetEntry =
        MemoryEstimator.DataValueOverheadBytes + MemoryEstimator.DictionaryEntryOverheadBytes;

    private readonly IAggregateAccumulator _inner;
    private readonly int _argumentCount;
    private readonly long? _memoryBudgetBytes;
    private readonly Func<IAggregateAccumulator>? _accumulatorFactory;
    private readonly InvocationFrame _capturedFrame;
    private HashSet<DataValue>? _singleArgumentSet;
    private HashSet<CompositeKey>? _multiArgumentSet;

    // ── Spill state ──
    private bool _spilling;
    private bool _drained;
    private string? _spillDirectory;
    private BinaryWriter?[]? _spillWriters;
    private string?[]? _spillPaths;

    /// <summary>
    /// The wrapped accumulator. Exposed so pooling infrastructure can return
    /// the inner accumulator to a type-keyed pool independently of the decorator.
    /// </summary>
    internal IAggregateAccumulator InnerAccumulator => _inner;

    /// <summary>
    /// Creates a new distinct decorator wrapping the given accumulator.
    /// </summary>
    /// <param name="inner">The accumulator to delegate to for distinct values.</param>
    /// <param name="argumentCount">
    /// The number of arguments the aggregate function expects.
    /// Determines whether single-key or composite-key deduplication is used.
    /// </param>
    /// <param name="frame">
    /// Captured per-call invocation context, reused for inner-accumulator
    /// <c>Accumulate</c>/<c>Result</c> calls during <c>Merge</c> and spill drain
    /// where no per-call frame flows through.
    /// </param>
    /// <param name="memoryBudgetBytes">
    /// Optional memory budget in bytes. When the in-memory hash set's estimated size
    /// exceeds this budget, values are spilled to hash-partitioned temporary files.
    /// <c>null</c> disables spill-to-disk (unbounded in-memory accumulation).
    /// </param>
    /// <param name="accumulatorFactory">
    /// Factory that creates a fresh inner accumulator of the same type. Required when
    /// <paramref name="memoryBudgetBytes"/> is set so that each spill partition can be
    /// drained with an independent accumulator instance.
    /// </param>
    /// <param name="estimatedDistinctCount">
    /// Optional estimated number of distinct values this accumulator will see.
    /// Pre-sizes the internal <see cref="HashSet{T}"/> to avoid repeated resize
    /// doublings that generate Gen2 garbage.
    /// </param>
    public DistinctAccumulatorDecorator(
        IAggregateAccumulator inner,
        int argumentCount,
        in InvocationFrame frame,
        long? memoryBudgetBytes = null,
        Func<IAggregateAccumulator>? accumulatorFactory = null,
        int estimatedDistinctCount = 0)
    {
        _inner = inner;
        _argumentCount = argumentCount;
        _memoryBudgetBytes = memoryBudgetBytes;
        _accumulatorFactory = accumulatorFactory;
        _capturedFrame = frame;

        if (argumentCount <= 1)
        {
            _singleArgumentSet = estimatedDistinctCount > 0
                ? new HashSet<DataValue>(estimatedDistinctCount)
                : new HashSet<DataValue>();
        }
        else
        {
            _multiArgumentSet = estimatedDistinctCount > 0
                ? new HashSet<CompositeKey>(estimatedDistinctCount)
                : new HashSet<CompositeKey>();
        }
    }

    /// <inheritdoc />
    public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        if (_spilling)
        {
            WriteToSpillPartition(arguments);
            return;
        }

        bool isNew;

        if (_singleArgumentSet is not null)
        {
            // Single-argument path: deduplicate on the argument value directly.
            // COUNT(DISTINCT col) with no arguments (COUNT(*)) should never reach here
            // because COUNT(DISTINCT *) is rejected during validation.
            DataValue key = arguments.Length > 0 ? arguments[0] : DataValue.UnknownNull();
            isNew = _singleArgumentSet.Add(key);
        }
        else
        {
            // Multi-argument path (rare): deduplicate on the composite key.
            DataValue[] parts = new DataValue[arguments.Length];
            arguments.CopyTo(parts);
            isNew = _multiArgumentSet!.Add(new CompositeKey(parts));
        }

        if (isNew)
        {
            _inner.Accumulate(arguments, in frame);

            // Check whether the hash set has grown beyond the memory budget.
            if (_memoryBudgetBytes.HasValue)
            {
                long count = _singleArgumentSet?.Count ?? _multiArgumentSet!.Count;
                long estimatedBytes = count * EstimatedBytesPerHashSetEntry;

                if (estimatedBytes > _memoryBudgetBytes.Value)
                {
                    BeginSpill();
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Merges by iterating the other decorator's distinct set and accumulating
    /// only values that are new to this decorator's set into the inner accumulator.
    /// The other decorator's inner accumulator is not merged directly — its state
    /// is redundant because all distinct values are replayed through this inner.
    /// </remarks>
    public void Merge(IAggregateAccumulator other)
    {
        DistinctAccumulatorDecorator otherDecorator = (DistinctAccumulatorDecorator)other;

        // Drain any spilled partitions before merging so the decorator sets
        // and inner accumulators represent the complete state.
        otherDecorator.DrainSpilledPartitions();

        if (_singleArgumentSet is not null && otherDecorator._singleArgumentSet is not null)
        {
            foreach (DataValue value in otherDecorator._singleArgumentSet)
            {
                if (_singleArgumentSet.Add(value))
                {
                    _inner.Accumulate([value], in _capturedFrame);
                }
            }
        }
        else if (_multiArgumentSet is not null && otherDecorator._multiArgumentSet is not null)
        {
            foreach (CompositeKey key in otherDecorator._multiArgumentSet)
            {
                if (_multiArgumentSet.Add(key))
                {
                    _inner.Accumulate(key.Values, in _capturedFrame);
                }
            }
        }
    }

    /// <inheritdoc />
    public DataValue Result(in InvocationFrame frame)
    {
        DrainSpilledPartitions();
        return _inner.Result(in frame);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _inner.Reset();
        _singleArgumentSet?.Clear();
        _multiArgumentSet?.Clear();
        CleanupSpillState();
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose()
    {
        CleanupSpillState();
    }

    // ─────────────── Spill infrastructure ───────────────

    /// <summary>
    /// Transitions to spill mode: redistributes all values currently in the hash set
    /// to hash-partitioned temporary files, clears the set, and resets the inner
    /// accumulator. All subsequent <see cref="Accumulate"/> calls write directly to
    /// partition files without touching the in-memory set.
    /// </summary>
    private void BeginSpill()
    {
        _spilling = true;
        _spillDirectory = Path.Combine(
            Path.GetTempPath(), $"datum-distinct-agg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_spillDirectory);
        _spillWriters = new BinaryWriter[SpillPartitionCount];
        _spillPaths = new string[SpillPartitionCount];

        if (ExecutionTracer.IsEnabled)
        {
            long count = _singleArgumentSet?.Count ?? _multiArgumentSet!.Count;
            long estimatedBytes = count * EstimatedBytesPerHashSetEntry;
            ExecutionTracer.Write(
                $"DISTINCT accumulator spill start  " +
                $"budget={ExecutionTracer.FormatBytes(_memoryBudgetBytes!.Value)}  " +
                $"estimated={ExecutionTracer.FormatBytes(estimatedBytes)}  " +
                $"entries={count}");
        }

        // Redistribute existing hash set values to partition files.
        if (_singleArgumentSet is not null)
        {
            foreach (DataValue value in _singleArgumentSet)
            {
                int partition = AssignPartition(value.GetHashCode());
                EnsureSpillWriter(partition);
                RowSerializer.WriteDataValue(_spillWriters[partition]!, value);
            }

            _singleArgumentSet.Clear();
            _singleArgumentSet.TrimExcess();
        }
        else
        {
            foreach (CompositeKey key in _multiArgumentSet!)
            {
                int partition = AssignPartition(key.GetHashCode());
                EnsureSpillWriter(partition);
                WriteCompositeKey(_spillWriters[partition]!, key);
            }

            _multiArgumentSet.Clear();
            _multiArgumentSet.TrimExcess();
        }

        // All distinct values are now in partition files — reset the inner.
        _inner.Reset();
    }

    /// <summary>
    /// Writes argument values to the appropriate hash-partitioned spill file.
    /// </summary>
    private void WriteToSpillPartition(ReadOnlySpan<DataValue> arguments)
    {
        int hashCode;
        int partition;

        if (_argumentCount <= 1)
        {
            DataValue key = arguments.Length > 0 ? arguments[0] : DataValue.UnknownNull();
            hashCode = key.GetHashCode();
            partition = AssignPartition(hashCode);
            EnsureSpillWriter(partition);
            RowSerializer.WriteDataValue(_spillWriters![partition]!, key);
        }
        else
        {
            HashCode hashCodeBuilder = new();
            DataValue[] parts = new DataValue[arguments.Length];
            for (int index = 0; index < arguments.Length; index++)
            {
                parts[index] = arguments[index];
                hashCodeBuilder.Add(arguments[index]);
            }

            hashCode = hashCodeBuilder.ToHashCode();
            partition = AssignPartition(hashCode);
            EnsureSpillWriter(partition);
            WriteCompositeKey(_spillWriters![partition]!, new CompositeKey(parts));
        }
    }

    /// <summary>
    /// Processes all spill partition files sequentially, deduplicating within each
    /// partition and merging partial results into <see cref="_inner"/>. Each partition
    /// is processed with a temporary hash set and a fresh inner accumulator, so peak
    /// memory is proportional to the largest single partition rather than the total
    /// distinct count.
    /// </summary>
    private void DrainSpilledPartitions()
    {
        if (!_spilling || _drained)
        {
            return;
        }

        _drained = true;

        // Flush and close all partition writers.
        FlushSpillWriters();

        for (int partition = 0; partition < SpillPartitionCount; partition++)
        {
            if (_spillPaths?[partition] is null)
            {
                continue;
            }

            using FileStream stream = new(
                _spillPaths[partition]!, FileMode.Open, FileAccess.Read, FileShare.None, 65536);
            using BinaryReader reader = new(stream);

            IAggregateAccumulator partitionAccumulator = _accumulatorFactory!();

            if (_argumentCount <= 1)
            {
                HashSet<DataValue> partitionSet = new();

                while (stream.Position < stream.Length)
                {
                    DataValue value = RowSerializer.ReadDataValue(reader);

                    if (partitionSet.Add(value))
                    {
                        partitionAccumulator.Accumulate([value], in _capturedFrame);
                    }
                }
            }
            else
            {
                HashSet<CompositeKey> partitionSet = new();

                while (stream.Position < stream.Length)
                {
                    DataValue[] values = ReadCompositeKeyValues(reader);

                    if (partitionSet.Add(new CompositeKey(values)))
                    {
                        partitionAccumulator.Accumulate(values, in _capturedFrame);
                    }
                }
            }

            // Merge the partition's inner accumulator into the main inner.
            // Since partitions are hash-disjoint, no cross-partition dedup is needed.
            _inner.Merge(partitionAccumulator);
        }

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.Write("DISTINCT accumulator spill drain complete");
        }

        CleanupSpillFiles();
    }

    private void EnsureSpillWriter(int partition)
    {
        if (_spillWriters![partition] is null)
        {
            _spillPaths![partition] = Path.Combine(_spillDirectory!, $"distinct_{partition}.spill");
            FileStream stream = new(
                _spillPaths[partition]!, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            _spillWriters[partition] = new BinaryWriter(stream);
        }
    }

    private void FlushSpillWriters()
    {
        if (_spillWriters is null)
        {
            return;
        }

        for (int index = 0; index < _spillWriters.Length; index++)
        {
            if (_spillWriters[index] is not null)
            {
                _spillWriters[index]!.Flush();
                _spillWriters[index]!.Dispose();
                _spillWriters[index] = null;
            }
        }
    }

    private static void WriteCompositeKey(BinaryWriter writer, CompositeKey key)
    {
        writer.Write(key.Values.Length);

        foreach (DataValue value in key.Values)
        {
            RowSerializer.WriteDataValue(writer, value);
        }
    }

    private static DataValue[] ReadCompositeKeyValues(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        DataValue[] values = new DataValue[count];

        for (int index = 0; index < count; index++)
        {
            values[index] = RowSerializer.ReadDataValue(reader);
        }

        return values;
    }

    private static int AssignPartition(int hashCode)
    {
        return (int)((uint)hashCode % SpillPartitionCount);
    }

    private void CleanupSpillState()
    {
        _spilling = false;
        _drained = false;
        FlushSpillWriters();
        CleanupSpillFiles();
    }

    private void CleanupSpillFiles()
    {
        _spillWriters = null;
        _spillPaths = null;

        if (_spillDirectory is not null && Directory.Exists(_spillDirectory))
        {
            try
            {
                Directory.Delete(_spillDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — files are in the OS temp directory.
            }

            _spillDirectory = null;
        }
    }
}
