using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.GroupBy;

/// <summary>
/// Owns the spill-to-disk machinery for keyed hash aggregation: the
/// <see cref="SpillReaderWriter"/>, the per-partition staging buffers, the
/// buffer <see cref="Arena"/>, the flat spill schema, and the drain-replay
/// loop. Hides the entire "after memory pressure" branch from the operator.
/// </summary>
/// <remarks>
/// Lifecycle:
/// <list type="number">
/// <item><description>Construct once per pipeline (only when a memory budget
/// is configured for a keyed aggregation).</description></item>
/// <item><description>Stay idle until the operator decides to spill, at which
/// point <see cref="BeginSpilling"/> sets up the spiller + partition
/// buffers + buffer arena.</description></item>
/// <item><description>The operator calls <see cref="StageRow"/> for every
/// row while <see cref="IsSpilling"/> is true.</description></item>
/// <item><description>Before drain, the operator calls
/// <see cref="FlushPartitionBuffers"/>.</description></item>
/// <item><description>The operator drains via
/// <see cref="DrainPartitionsAsync"/>, which yields one
/// fully-accumulated partition-local <see cref="IHashGroupTable"/>
/// per non-empty partition.</description></item>
/// <item><description>Disposal returns any remaining partition buffers,
/// disposes the spiller, and returns the buffer arena.</description></item>
/// </list>
/// </remarks>
internal sealed class SpillCoordinator : IDisposable
{
    /// <summary>Number of hash partitions used when spilling.</summary>
    public const int DefaultPartitionCount = 64;

    private readonly Pool _pool;
    private readonly ExecutionContext _context;
    private readonly IReadOnlyList<Expression> _groupByExpressions;
    private readonly IReadOnlyList<AggregateColumn> _aggregateColumns;
    private readonly int _partitionCount;

    private bool _isSpilling;
    private SpillReaderWriter? _spiller;
    private ColumnLookup? _spillSchema;
    private RowBatch?[]? _partitionBuffers;
    private Arena? _bufferArena;

    public SpillCoordinator(
        Pool pool,
        ExecutionContext context,
        IReadOnlyList<Expression> groupByExpressions,
        IReadOnlyList<AggregateColumn> aggregateColumns,
        int partitionCount = DefaultPartitionCount)
    {
        _pool = pool;
        _context = context;
        _groupByExpressions = groupByExpressions;
        _aggregateColumns = aggregateColumns;
        _partitionCount = partitionCount;
    }

    public bool IsSpilling => _isSpilling;

    /// <summary>
    /// Arena that the spiller stabilises replayed values into. Use this as
    /// the <see cref="InvocationFrame.Source"/> when accumulating drained rows.
    /// </summary>
    public Arena ConsolidatedArena => _spiller!.ConsolidatedArena;

    /// <summary>
    /// Transitions from in-memory aggregation into spilling mode: builds the
    /// flat spill schema, rents the buffer arena, allocates the partition
    /// buffer array, and opens the spiller. Idempotent — subsequent calls
    /// are no-ops.
    /// </summary>
    public void BeginSpilling(long memoryBudget, long estimatedMemory, long currentGroupCount)
    {
        if (_isSpilling) return;

        _isSpilling = true;
        _spillSchema = BuildSpillSchema();
        _bufferArena = _pool.RentArena();
        int hint = (int)Math.Min(memoryBudget / 2, int.MaxValue);
        _spiller = new SpillReaderWriter(
            _pool, _spillSchema, _context.SpillDirectory,
            initialArenaCapacity: hint,
            partitionCount: _partitionCount);
        _partitionBuffers = new RowBatch?[_partitionCount];

        if (DatumActivity.Operators.HasListeners())
        {
            DatumActivity.Operators.Trace(
                $"GROUP BY spill start  budget={DatumActivity.FormatBytes(memoryBudget)}  estimated={DatumActivity.FormatBytes(estimatedMemory)}  groups={currentGroupCount}");
        }
    }

    /// <summary>
    /// Stages a single row into the partition selected by
    /// <see cref="IHashGroupTable.HashScratch"/>. Flushes the partition's
    /// staging buffer to the spiller when it fills.
    /// </summary>
    public void StageRow(IHashGroupTable hashTable, AggregateArgumentBinder binder, Arena sourceArena)
    {
        int partition = (int)((uint)hashTable.HashScratch() % _partitionCount);

        _partitionBuffers![partition] ??= _pool.RentRowBatch(
            _spillSchema!, _context.BatchSize, _bufferArena!);

        DataValue[] flatValues = _pool.RentDataValues(_spillSchema!.Count);
        try
        {
            int offset = hashTable.StabilizeScratchInto(
                flatValues.AsSpan(), sourceArena, _bufferArena!);
            for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
            {
                DataValue[] argValues = binder.Arguments[aggregateIndex];
                for (int argIndex = 0; argIndex < argValues.Length; argIndex++)
                {
                    flatValues[offset++] = DataValueRetention.Stabilize(
                        argValues[argIndex], sourceArena, _bufferArena!);
                }
                if (binder.SortKeys?[aggregateIndex] is DataValue[] sortKeys)
                {
                    for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                    {
                        flatValues[offset++] = DataValueRetention.Stabilize(
                            sortKeys[sortIndex], sourceArena, _bufferArena!);
                    }
                }
            }
            _partitionBuffers[partition]!.Add(flatValues);
            flatValues = null!;
        }
        finally
        {
            if (flatValues is not null) _pool.Backing.Return(flatValues);
        }

        if (_partitionBuffers[partition]!.IsFull)
        {
            _spiller!.Write(_partitionBuffers[partition]!, partition);
            _partitionBuffers[partition] = null;
        }
    }

    /// <summary>
    /// Flushes every non-empty partition staging buffer to the spiller. Must
    /// be called once after the input loop completes and before
    /// <see cref="DrainPartitionsAsync"/> so partial buffers are not lost.
    /// </summary>
    public void FlushPartitionBuffers()
    {
        if (_spiller is null || _partitionBuffers is null) return;

        for (int p = 0; p < _partitionBuffers.Length; p++)
        {
            if (_partitionBuffers[p] is not null)
            {
                _spiller.Write(_partitionBuffers[p]!, p);
                _partitionBuffers[p] = null;
            }
        }
    }

    /// <summary>
    /// Drains every non-empty spill partition. For each one, replays the
    /// spilled rows, skips any keys already present in <paramref name="mainTable"/>
    /// (those were accumulated in-memory before spill kicked in), and
    /// accumulates the rest into a fresh partition-local
    /// <see cref="IHashGroupTable"/>. Each fully-built partition table is
    /// yielded for the operator to emit and dispose.
    /// </summary>
    public async IAsyncEnumerable<IHashGroupTable> DrainPartitionsAsync(
        IHashGroupTable mainTable,
        AggregateArgumentBinder binder,
        InvocationFrame drainFrame,
        GroupStateFactory groupStateFactory)
    {
        for (int partition = 0; partition < _partitionCount; partition++)
        {
            if (_spiller!.RowsWrittenInPartition(partition) == 0) continue;

            IHashGroupTable partTable = mainTable.CreatePartitionLocal();

            await foreach (RowBatch spillBatch in _spiller.ReplayPartitionAsync(
                _context, _spillSchema!, partition).ConfigureAwait(false))
            {
                try
                {
                    for (int sb = 0; sb < spillBatch.Count; sb++)
                    {
                        Row spillRow = spillBatch[sb];
                        int offset = 0;

                        DataValue[] partKey = mainTable.ReadKeyFromRow(spillRow, ref offset);
                        if (mainTable.Contains(partKey)) continue;

                        GroupState? partGroup = partTable.TryGetByKey(partKey);
                        if (partGroup is null)
                        {
                            partGroup = groupStateFactory.Create(in drainFrame);
                            partTable.Insert(partKey, partGroup);
                        }

                        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
                        {
                            DataValue[] argValues = binder.Arguments[aggregateIndex];
                            for (int argIndex = 0; argIndex < argValues.Length; argIndex++)
                            {
                                argValues[argIndex] = spillRow[offset++];
                            }
                            if (binder.SortKeys?[aggregateIndex] is DataValue[] sortKeys)
                            {
                                for (int sortIndex = 0; sortIndex < sortKeys.Length; sortIndex++)
                                {
                                    sortKeys[sortIndex] = spillRow[offset++];
                                }
                            }
                        }

                        binder.AccumulateInto(partGroup, _context, in drainFrame);
                    }
                }
                finally
                {
                    _context.ReturnRowBatch(spillBatch);
                }
            }

            yield return partTable;
        }
    }

    /// <summary>
    /// Builds the flat schema used by spilled rows: <c>__key_0..__key_{N-1}</c>
    /// for the GROUP BY keys, then per aggregate <c>__arg_{i}_{j}</c> for each
    /// argument expression and <c>__sort_{i}_{k}</c> for each ORDER BY
    /// expression. <c>COUNT(*)</c> contributes no argument columns.
    /// </summary>
    private ColumnLookup BuildSpillSchema()
    {
        int keyCount = _groupByExpressions.Count;
        int extraCount = 0;
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn column = _aggregateColumns[aggregateIndex];
            if (!column.IsCountStar)
            {
                extraCount += column.ArgumentExpressions.Count;
            }
            if (column.OrderBy is not null)
            {
                extraCount += column.OrderBy.Count;
            }
        }

        string[] names = new string[keyCount + extraCount];
        int next = 0;
        for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
        {
            names[next++] = $"__key_{keyIndex}";
        }
        for (int aggregateIndex = 0; aggregateIndex < _aggregateColumns.Count; aggregateIndex++)
        {
            AggregateColumn column = _aggregateColumns[aggregateIndex];
            if (!column.IsCountStar)
            {
                for (int argIndex = 0; argIndex < column.ArgumentExpressions.Count; argIndex++)
                {
                    names[next++] = $"__arg_{aggregateIndex}_{argIndex}";
                }
            }
            if (column.OrderBy is not null)
            {
                for (int sortIndex = 0; sortIndex < column.OrderBy.Count; sortIndex++)
                {
                    names[next++] = $"__sort_{aggregateIndex}_{sortIndex}";
                }
            }
        }

        return new ColumnLookup(names);
    }

    public void Dispose()
    {
        if (_partitionBuffers is not null)
        {
            for (int p = 0; p < _partitionBuffers.Length; p++)
            {
                if (_partitionBuffers[p] is not null)
                {
                    _context.ReturnRowBatch(_partitionBuffers[p]!);
                    _partitionBuffers[p] = null;
                }
            }
        }
        _spiller?.Dispose();
        _spiller = null;
        if (_bufferArena is not null)
        {
            _pool.ReturnArena(_bufferArena);
            _bufferArena = null;
        }
    }
}
