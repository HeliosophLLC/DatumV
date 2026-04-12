using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Sets;

/// <summary>
/// Wraps a <see cref="SpillReaderWriter"/> plus the per-partition row-batch buffer
/// array used while spilling. Encapsulates the lazy-activate / route / flush / replay /
/// dispose pattern that every spilling set-operation iterator repeated inline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Constructed up-front in each iterator (cheap — no resources held).
/// <see cref="Activate(ColumnLookup)"/> is called once when the budget is first exceeded,
/// allocating the <see cref="SpillReaderWriter"/> with the schema discovered from the
/// first non-empty input batch. After activation, <see cref="Route"/> sends each
/// subsequent row to its hash-assigned partition buffer; full buffers flush to the
/// spill file and are returned to the pool by <c>SpillReaderWriter.Write</c>.
/// <see cref="FlushAllBuffers"/> flushes any non-empty trailing buffers before drain.
/// </para>
/// <para>
/// <b>Dispose contract.</b> <see cref="Dispose"/> returns any still-rented partition
/// buffers and disposes the underlying <see cref="SpillReaderWriter"/> (which deletes
/// its temp directory + arena file). Safe on exception paths — call from a
/// <c>finally</c> block.
/// </para>
/// </remarks>
internal sealed class PartitionedRowSpiller : IDisposable
{
    private readonly ExecutionContext _context;
    private readonly Pool _pool;
    private readonly int _partitionCount;

    private ColumnLookup? _schema;
    private SpillReaderWriter? _spiller;
    private RowBatch?[]? _buffers;

    public PartitionedRowSpiller(ExecutionContext context, int partitionCount)
    {
        _context = context;
        _pool = context.Pool;
        _partitionCount = partitionCount;
    }

    /// <summary>
    /// Whether <see cref="Activate(ColumnLookup)"/> has been called. Used by iterators
    /// to decide between the in-memory path and the route-to-spill path.
    /// </summary>
    public bool IsActive => _spiller is not null;

    /// <summary>The number of hash partitions configured for this spiller.</summary>
    public int PartitionCount => _partitionCount;

    /// <summary>
    /// Lazily creates the underlying <see cref="SpillReaderWriter"/> and partition-buffer
    /// array using a half-budget initial-arena hint. Idempotent — subsequent calls
    /// with the same schema are no-ops; a different schema throws.
    /// </summary>
    public void Activate(ColumnLookup schema)
    {
        if (_spiller is not null)
        {
            if (!ReferenceEquals(_schema, schema))
            {
                throw new InvalidOperationException(
                    "PartitionedRowSpiller already activated with a different schema.");
            }
            return;
        }

        long budget = _context.Accountant.MemoryBudgetBytes ?? long.MaxValue;
        int hint = (int)Math.Min(budget / 2, int.MaxValue);
        _schema = schema;
        _spiller = new SpillReaderWriter(
            _pool, schema, _context.SpillDirectory,
            initialArenaCapacity: hint,
            partitionCount: _partitionCount);
        _buffers = new RowBatch?[_partitionCount];
    }

    /// <summary>
    /// Maps a hash code to a partition index via modulo <see cref="PartitionCount"/>.
    /// Exposed so callers compute the partition from a hash chosen by the key-shape
    /// (e.g. <see cref="DedupKeySet.GetKeyHash(Row)"/>).
    /// </summary>
    public int AssignPartition(int hashCode) => (int)((uint)hashCode % (uint)_partitionCount);

    /// <summary>
    /// Copies row <paramref name="sourceIndex"/> from <paramref name="sourceBatch"/>
    /// into <paramref name="partition"/>'s buffer, renting the buffer on first use.
    /// When the buffer fills, it is flushed to the spill file (and returned to the pool)
    /// and the slot is cleared.
    /// </summary>
    public void Route(RowBatch sourceBatch, int sourceIndex, int partition)
    {
        if (_spiller is null || _buffers is null)
        {
            throw new InvalidOperationException("PartitionedRowSpiller.Route called before Activate.");
        }

        _buffers[partition] ??= _pool.RentRowBatch(_schema!, _context.BatchSize, _context.Store);
        _pool.RentAndCopyToOutput(sourceBatch, sourceIndex, _buffers[partition]!);

        if (_buffers[partition]!.IsFull)
        {
            _spiller.Write(_buffers[partition]!, partition);
            _buffers[partition] = null;
        }
    }

    /// <summary>
    /// Flushes any non-empty partition buffer to its spill file. Call once at the
    /// end of an input phase so <see cref="RowsWrittenInPartition"/> reflects the
    /// final on-disk count before drain.
    /// </summary>
    public void FlushAllBuffers()
    {
        if (_spiller is null || _buffers is null) return;

        for (int p = 0; p < _buffers.Length; p++)
        {
            if (_buffers[p] is not null)
            {
                _spiller.Write(_buffers[p]!, p);
                _buffers[p] = null;
            }
        }
    }

    /// <summary>The number of rows currently spilled to <paramref name="partition"/>.</summary>
    public long RowsWrittenInPartition(int partition)
        => _spiller?.RowsWrittenInPartition(partition) ?? 0;

    /// <summary>
    /// Replays every spilled row in <paramref name="partition"/>. Each yielded
    /// <see cref="RowBatch"/> is owned by the caller, which must return it to the
    /// pool (typically via the <c>context.ReturnRowBatch</c> finally pattern).
    /// </summary>
    public IAsyncEnumerable<RowBatch> ReplayPartitionAsync(int partition)
    {
        if (_spiller is null)
        {
            throw new InvalidOperationException("PartitionedRowSpiller.ReplayPartitionAsync called before Activate.");
        }
        return _spiller.ReplayPartitionAsync(_context, _schema!, partition);
    }

    /// <summary>
    /// Returns any still-rented partition buffers and disposes the underlying
    /// <see cref="SpillReaderWriter"/>. Safe to call multiple times; safe to call
    /// from a <c>finally</c> block after partial activation or on exception paths.
    /// </summary>
    public void Dispose()
    {
        if (_buffers is not null)
        {
            for (int p = 0; p < _buffers.Length; p++)
            {
                if (_buffers[p] is not null)
                {
                    _context.ReturnRowBatch(_buffers[p]!);
                    _buffers[p] = null;
                }
            }
            _buffers = null;
        }

        _spiller?.Dispose();
        _spiller = null;
    }
}
