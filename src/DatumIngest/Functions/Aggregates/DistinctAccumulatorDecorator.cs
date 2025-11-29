using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

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
/// When a memory budget is provided, the decorator spills to a hash-partitioned
/// <see cref="SpillReaderWriter"/> once estimated memory exceeds the budget. Values are
/// partitioned by their hash code so each partition contains a non-overlapping subset
/// of distinct values. During the drain phase (triggered by <see cref="Result"/>),
/// partitions are processed sequentially with fresh accumulators and merged into the
/// final result, keeping peak memory proportional to the largest partition rather than
/// the total distinct count.
/// </para>
/// <para>
/// Hash-set keys are stabilised into the captured <see cref="InvocationFrame.Target"/>
/// store on insertion so their offsets stay valid after the per-call source arena
/// (typically <c>inputBatch.Arena</c>) recycles. Spill values are restabilised into the
/// spiller's consolidated arena via <c>SpillReaderWriter.Write</c>.
/// </para>
/// </summary>
internal sealed class DistinctAccumulatorDecorator : IAggregateAccumulator, IDisposable
{
    /// <summary>Number of hash partitions used when spilling to disk.</summary>
    private const int SpillPartitionCount = 64;

    /// <summary>Per-partition row buffer capacity before flushing to the spiller.</summary>
    private const int SpillBufferRows = 64;

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
    private readonly ExecutionContext _context;
    private HashSet<DataValue>? _singleArgumentSet;
    private HashSet<CompositeKey>? _multiArgumentSet;

    // ── Spill state ──
    private bool _spilling;
    private bool _drained;
    private SpillReaderWriter? _spiller;
    private RowBatch?[]? _partitionBuffers;
    private Arena? _bufferArena;
    private ColumnLookup? _spillSchema;

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
    /// Captured per-call invocation context. <see cref="InvocationFrame.Target"/>
    /// is the stabilisation home for distinct-set keys (so they outlive their
    /// originating input batch). Reused for inner-accumulator <c>Accumulate</c>/<c>Merge</c>
    /// calls during drain where no per-call frame flows through.
    /// </param>
    /// <param name="context">
    /// Execution context — provides <see cref="ExecutionContext.Pool"/>,
    /// <see cref="ExecutionContext.SpillDirectory"/>, batch size, and cancellation
    /// token used by the <see cref="SpillReaderWriter"/> when budget-triggered spill
    /// kicks in. Required even when no budget is set so the decorator can rent the
    /// per-partition staging arena and resolve replay batches consistently.
    /// </param>
    /// <param name="memoryBudgetBytes">
    /// Optional memory budget in bytes. When the in-memory hash set's estimated size
    /// exceeds this budget, values are spilled to a hash-partitioned <c>SpillReaderWriter</c>.
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
        ExecutionContext context,
        long? memoryBudgetBytes = null,
        Func<IAggregateAccumulator>? accumulatorFactory = null,
        int estimatedDistinctCount = 0)
    {
        _inner = inner;
        _argumentCount = argumentCount;
        _memoryBudgetBytes = memoryBudgetBytes;
        _accumulatorFactory = accumulatorFactory;
        _capturedFrame = frame;
        _context = context;

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
            WriteToSpillPartition(arguments, in frame);
            return;
        }

        bool isNew;

        if (_singleArgumentSet is not null)
        {
            // Single-argument path: deduplicate on the argument value directly.
            // COUNT(DISTINCT col) with no arguments (COUNT(*)) should never reach here
            // because COUNT(DISTINCT *) is rejected during validation.
            DataValue raw = arguments.Length > 0 ? arguments[0] : DataValue.UnknownNull();
            // Stabilise into the captured Target so the hash-set entry stays valid
            // after the per-call source arena (e.g. inputBatch.Arena) recycles.
            // Inline values pass through unchanged; non-inline values get a copy.
            DataValue key = DataValueRetention.Stabilize(raw, frame.Source, _capturedFrame.Target);
            isNew = _singleArgumentSet.Add(key);
        }
        else
        {
            // Multi-argument path (rare): deduplicate on the composite key.
            DataValue[] parts = new DataValue[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                parts[i] = DataValueRetention.Stabilize(arguments[i], frame.Source, _capturedFrame.Target);
            }
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
    public void Merge(IAggregateAccumulator other, in InvocationFrame frame)
    {
        DistinctAccumulatorDecorator otherDecorator = (DistinctAccumulatorDecorator)other;

        // Drain any spilled partitions before merging so the decorator sets
        // and inner accumulators represent the complete state.
        if (otherDecorator._spilling && !otherDecorator._drained)
        {
            otherDecorator.DrainSpilledPartitionsAsync().GetAwaiter().GetResult();
        }

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
        if (_spilling && !_drained)
        {
            DrainSpilledPartitionsAsync().GetAwaiter().GetResult();
        }
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
    /// Transitions to spill mode: stands up the <see cref="SpillReaderWriter"/> +
    /// per-partition staging buffers, redistributes existing hash-set entries into
    /// those buffers, clears the set, and resets the inner accumulator. All subsequent
    /// <see cref="Accumulate"/> calls bypass the in-memory set and route directly to
    /// partition buffers.
    /// </summary>
    private void BeginSpill()
    {
        _spilling = true;
        _spillSchema = BuildSpillSchema();
        _bufferArena = _context.Pool.RentArena();

        int hint = _memoryBudgetBytes.HasValue
            ? (int)System.Math.Min(_memoryBudgetBytes.Value / 2, int.MaxValue)
            : 1024 * 1024;
        _spiller = new SpillReaderWriter(
            _context.Pool, _spillSchema, _context.SpillDirectory,
            initialArenaCapacity: hint,
            partitionCount: SpillPartitionCount);
        _partitionBuffers = new RowBatch?[SpillPartitionCount];

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

        // Redistribute existing hash-set entries into per-partition buffers. The
        // entries' arena-backed payloads already live in _capturedFrame.Target
        // (Stabilise-on-Accumulate above), so source = _capturedFrame.Target.
        if (_singleArgumentSet is not null)
        {
            foreach (DataValue value in _singleArgumentSet)
            {
                int partition = AssignPartition(value.GetHashCode());
                AddToPartitionBuffer(partition, [value], _capturedFrame.Target);
            }

            _singleArgumentSet.Clear();
            _singleArgumentSet.TrimExcess();
        }
        else
        {
            foreach (CompositeKey key in _multiArgumentSet!)
            {
                int partition = AssignPartition(key.GetHashCode());
                AddToPartitionBuffer(partition, key.Values, _capturedFrame.Target);
            }

            _multiArgumentSet.Clear();
            _multiArgumentSet.TrimExcess();
        }

        // All distinct values are now in partition buffers / spill — reset the inner.
        _inner.Reset();
    }

    /// <summary>
    /// Routes a post-spill row to its hash-partitioned buffer. Each value is stabilised
    /// into <see cref="_bufferArena"/> first; when the partition buffer fills, the
    /// spiller restabilises into its consolidated arena and the buffer is recycled.
    /// </summary>
    private void WriteToSpillPartition(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        int hashCode;
        DataValue[] values;

        if (_argumentCount <= 1)
        {
            DataValue key = arguments.Length > 0 ? arguments[0] : DataValue.UnknownNull();
            hashCode = key.GetHashCode();
            values = [key];
        }
        else
        {
            HashCode hashCodeBuilder = new();
            values = new DataValue[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                values[i] = arguments[i];
                hashCodeBuilder.Add(arguments[i]);
            }

            hashCode = hashCodeBuilder.ToHashCode();
        }

        int partition = AssignPartition(hashCode);
        AddToPartitionBuffer(partition, values, frame.Source);
    }

    /// <summary>
    /// Stabilises <paramref name="values"/> from <paramref name="sourceStore"/> into
    /// <see cref="_bufferArena"/> and appends them as a row to the partition's staging
    /// buffer. Flushes to the spiller when the buffer reaches <see cref="SpillBufferRows"/>.
    /// </summary>
    private void AddToPartitionBuffer(int partition, ReadOnlySpan<DataValue> values, IValueStore sourceStore)
    {
        _partitionBuffers![partition] ??= _context.Pool.RentRowBatch(
            _spillSchema!, SpillBufferRows, _bufferArena!);

        DataValue[] flatValues = _context.Pool.RentDataValues(values.Length);
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                flatValues[i] = DataValueRetention.Stabilize(values[i], sourceStore, _bufferArena!);
            }
            _partitionBuffers[partition]!.Add(flatValues);
            flatValues = null!;
        }
        finally
        {
            if (flatValues is not null) _context.Pool.Backing.Return(flatValues);
        }

        if (_partitionBuffers[partition]!.IsFull)
        {
            _spiller!.Write(_partitionBuffers[partition]!, partition);
            _partitionBuffers[partition] = null;
        }
    }

    /// <summary>
    /// Replays each non-empty spill partition through a fresh inner accumulator, then
    /// merges those partial inners into <see cref="_inner"/>. Each partition's local
    /// hash set deduplicates its disjoint key range, so peak memory is proportional to
    /// the largest partition rather than the total distinct count.
    /// </summary>
    private async Task DrainSpilledPartitionsAsync()
    {
        if (!_spilling || _drained)
        {
            return;
        }

        _drained = true;

        // Flush any partial buffers to the spiller before replay.
        if (_partitionBuffers is not null && _spiller is not null)
        {
            for (int p = 0; p < SpillPartitionCount; p++)
            {
                if (_partitionBuffers[p] is not null)
                {
                    _spiller.Write(_partitionBuffers[p]!, p);
                    _partitionBuffers[p] = null;
                }
            }
        }

        // Replay frame: spill bytes resolve against the consolidated arena; the inner
        // accumulator's own stabilisation logic decides where to retain (typically the
        // captured Target). SidecarRegistry threads through unchanged.
        InvocationFrame drainFrame = new(
            _spiller!.ConsolidatedArena, _capturedFrame.Target, _capturedFrame.SidecarRegistry);

        for (int partition = 0; partition < SpillPartitionCount; partition++)
        {
            if (_spiller!.RowsWrittenInPartition(partition) == 0) continue;

            IAggregateAccumulator partitionAccumulator = _accumulatorFactory!();
            HashSet<DataValue>? partitionSingleSet = _argumentCount <= 1 ? new() : null;
            HashSet<CompositeKey>? partitionCompositeSet = _argumentCount > 1 ? new() : null;

            await foreach (RowBatch batch in _spiller.ReplayPartitionAsync(
                _context, _spillSchema!, partition).ConfigureAwait(false))
            {
                try
                {
                    for (int row = 0; row < batch.Count; row++)
                    {
                        Row spillRow = batch[row];

                        if (_argumentCount <= 1)
                        {
                            DataValue value = spillRow[0];
                            if (partitionSingleSet!.Add(value))
                            {
                                partitionAccumulator.Accumulate([value], in drainFrame);
                            }
                        }
                        else
                        {
                            DataValue[] parts = new DataValue[_argumentCount];
                            for (int i = 0; i < _argumentCount; i++)
                            {
                                parts[i] = spillRow[i];
                            }
                            if (partitionCompositeSet!.Add(new CompositeKey(parts)))
                            {
                                partitionAccumulator.Accumulate(parts, in drainFrame);
                            }
                        }
                    }
                }
                finally
                {
                    _context.ReturnRowBatch(batch);
                }
            }

            // Merge the partition's inner accumulator into the main inner.
            // Since partitions are hash-disjoint, no cross-partition dedup is needed.
            _inner.Merge(partitionAccumulator, in drainFrame);
        }

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.Write("DISTINCT accumulator spill drain complete");
        }

        CleanupSpillState();
    }

    private ColumnLookup BuildSpillSchema()
    {
        if (_argumentCount <= 1)
        {
            return new ColumnLookup(["__value"]);
        }

        string[] names = new string[_argumentCount];
        for (int i = 0; i < _argumentCount; i++)
        {
            names[i] = $"__arg_{i}";
        }
        return new ColumnLookup(names);
    }

    private static int AssignPartition(int hashCode)
    {
        return (int)((uint)hashCode % SpillPartitionCount);
    }

    private void CleanupSpillState()
    {
        _spilling = false;
        _drained = false;

        if (_partitionBuffers is not null)
        {
            for (int p = 0; p < SpillPartitionCount; p++)
            {
                if (_partitionBuffers[p] is not null)
                {
                    _context.ReturnRowBatch(_partitionBuffers[p]!);
                    _partitionBuffers[p] = null;
                }
            }
            _partitionBuffers = null;
        }

        _spiller?.Dispose();
        _spiller = null;
        _spillSchema = null;

        if (_bufferArena is not null)
        {
            _context.Pool.ReturnArena(_bufferArena);
            _bufferArena = null;
        }
    }
}
