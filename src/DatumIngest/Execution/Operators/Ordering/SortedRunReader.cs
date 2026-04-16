using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Ordering;

/// <summary>
/// Pull-stream over a single sorted run produced by <see cref="SortedRunSpiller.Spill"/>.
/// Exposes <see cref="Current"/> + <see cref="ReadNextAsync"/> so the k-way merge heap
/// can peek the next row, recompute its sort keys via the supplied
/// <see cref="SortKeyEvaluator"/>, and advance when the row is consumed.
/// </summary>
/// <remarks>
/// <para>
/// Intra-batch advance is just an index bump (no I/O). When the current batch is
/// exhausted it is returned to the pool and the underlying enumerator pulls the next
/// from the run's replay stream. Spilled runs hold only payloads — keys are
/// re-evaluated each step against the current batch's arena, matching the existing
/// no-keys-on-disk encoding.
/// </para>
/// </remarks>
internal sealed class SortedRunReader : IAsyncDisposable
{
    private readonly Pool _pool;
    private readonly IAsyncEnumerator<RowBatch> _enumerator;
    private readonly ExecutionContext _context;
    private readonly SortKeyEvaluator _keyEvaluator;
    private RowBatch? _currentBatch;
    private int _currentIndex;
    private DataValue[] _currentKeys;
    private bool _disposed;

    public SortedRunReader(
        SpillReaderWriter run,
        ColumnLookup schema,
        ExecutionContext context,
        SortKeyEvaluator keyEvaluator)
    {
        _pool = context.Pool;
        _enumerator = run.ReplayPartitionAsync(context, schema, partition: 0)
            .GetAsyncEnumerator(context.CancellationToken);
        _currentIndex = -1;
        _context = context;
        _keyEvaluator = keyEvaluator;
        _currentKeys = Array.Empty<DataValue>();
    }

    public Row Current => _currentBatch![_currentIndex];

    public RowBatch CurrentBatch => _currentBatch!;

    public int CurrentIndex => _currentIndex;

    public DataValue[] CurrentKeys => _currentKeys;

    /// <summary>
    /// Advances to the next row in the run, pulling a new batch when the current one
    /// is exhausted. Returns <see langword="false"/> when the run is fully drained.
    /// </summary>
    public async ValueTask<bool> ReadNextAsync()
    {
        if (_currentBatch is not null && _currentIndex + 1 < _currentBatch.Count)
        {
            _currentIndex++;
            _currentKeys = await _keyEvaluator
                .EvaluateAsync(Current, _currentBatch.Arena, _context.CancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (_currentBatch is not null)
        {
            _pool.ReturnRowBatch(_currentBatch);
            _currentBatch = null;
        }

        while (await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            RowBatch next = _enumerator.Current;
            if (next.Count > 0)
            {
                _currentBatch = next;
                _currentIndex = 0;
                _currentKeys = await _keyEvaluator
                    .EvaluateAsync(Current, _currentBatch.Arena, _context.CancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            _pool.ReturnRowBatch(next);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_currentBatch is not null)
        {
            _pool.ReturnRowBatch(_currentBatch);
            _currentBatch = null;
        }
        await _enumerator.DisposeAsync().ConfigureAwait(false);
    }
}
