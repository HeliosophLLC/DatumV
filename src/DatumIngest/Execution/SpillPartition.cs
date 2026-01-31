using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution;

/// <summary>
/// A single hash partition in a Grace hash join that can hold rows in memory
/// or spill them to disk when memory pressure is reached.
/// </summary>
/// <remarks>
/// <para>
/// Each partition maintains separate build-side and probe-side row collections.
/// In-memory rows are stabilised into a per-partition retention <see cref="Arena"/>
/// so input batches can be returned without invalidating the rows we keep.
/// </para>
/// <para>
/// When <see cref="SpillBuildToDisk"/> or <see cref="SpillProbeToDisk"/> is called,
/// rows are flushed to a per-partition <see cref="SpillReaderWriter"/> with two
/// internal partitions (build = 0, probe = 1) that share a single consolidated arena.
/// The shared arena is the critical invariant: build-side keys and probe-side keys
/// land in the same arena, so equality comparison "just works" without per-side copies.
/// </para>
/// <para>
/// Reading spilled rows back via <see cref="ReadSpilledBuildRows"/> /
/// <see cref="ReadSpilledProbeRows"/> drives the spiller's async replay synchronously
/// and retains the yielded batches until <see cref="Dispose"/> — the consumer
/// (Grace hash join) holds <see cref="Row"/> references into those batches throughout
/// the join phase.
/// </para>
/// </remarks>
internal sealed class SpillPartition : IDisposable
{
    /// <summary>SpillReaderWriter sub-partition index for build rows.</summary>
    private const int BuildSlot = 0;

    /// <summary>SpillReaderWriter sub-partition index for probe rows.</summary>
    private const int ProbeSlot = 1;

    /// <summary>Capacity of the staging buffer used to accumulate post-spill rows before flushing.</summary>
    private const int SpillStagingCapacity = 256;

    private List<Row>? _buildRows = new();
    private List<Row>? _probeRows = new();

    private SpillReaderWriter? _spiller;
    private bool _buildSpilled;
    private bool _probeSpilled;
    private int _spilledBuildRowCount;
    private int _spilledProbeRowCount;

    private RowBatch? _buildStaging;
    private RowBatch? _probeStaging;

    /// <summary>
    /// Per-slot column lookup. Build and probe rows typically have different
    /// schemas (different aliases), so a single shared <see cref="_spillSchema"/>
    /// would carry the wrong column names for one side at replay time. The
    /// spiller's row-stride uses <see cref="_spillSchema"/>'s column count
    /// (which must match both sides' counts), but each replay yields rows
    /// rebound to its slot's lookup so column-name resolution works on
    /// either side.
    /// </summary>
    private ColumnLookup? _buildSchema;
    private ColumnLookup? _probeSchema;

    /// <summary>
    /// Batches yielded by replay that callers still hold <see cref="Row"/> references into.
    /// Returned to the pool on <see cref="Dispose"/>.
    /// </summary>
    private List<RowBatch>? _replayBatches;

    private readonly string _spillDirectory;
    private readonly LocalBufferPool? _pool;
    private readonly Pool _arenaPool;
    private readonly ExecutionContext _context;
    private Arena? _retentionArena;
    private ColumnLookup? _spillSchema;

    /// <summary>
    /// Creates a new partition.
    /// </summary>
    /// <param name="spillDirectory">Parent directory for spill files.</param>
    /// <param name="partitionIndex">Zero-based index of this partition (used in file names / diagnostics).</param>
    /// <param name="arenaPool">Arena-aware pool used for retention arena and spill replay batches.</param>
    /// <param name="context">Execution context — supplies <c>BatchSize</c> and <c>CancellationToken</c> for replay.</param>
    /// <param name="estimatedBuildRows">
    /// Estimated number of build-side rows. Used to pre-size the in-memory list and avoid
    /// LOH-crossing list doublings. Zero or negative falls back to default capacity.
    /// </param>
    /// <param name="pool">
    /// Optional buffer pool for renting <see cref="DataValue"/> array copies. When provided,
    /// <see cref="AddBuildRow"/> / <see cref="AddProbeRow"/> rent each in-memory row's value
    /// array from this pool; otherwise they allocate.
    /// </param>
    internal SpillPartition(
        string spillDirectory,
        int partitionIndex,
        Pool arenaPool,
        ExecutionContext context,
        int estimatedBuildRows = 0,
        LocalBufferPool? pool = null)
    {
        _ = partitionIndex;
        _spillDirectory = spillDirectory;
        _arenaPool = arenaPool;
        _context = context;
        _pool = pool;
        if (estimatedBuildRows > 0)
        {
            _buildRows = new List<Row>(estimatedBuildRows);
        }
    }

    /// <summary>
    /// Retention arena for in-memory rows. Lazily allocated on first stabilization.
    /// Exposed so callers handing rows from one partition to another (e.g. recursive
    /// repartitioning) can pass it as the source arena.
    /// </summary>
    internal Arena? RetentionArena => _retentionArena;

    /// <summary>The number of build-side rows currently held in memory.</summary>
    internal int InMemoryBuildRowCount => _buildRows?.Count ?? 0;

    /// <summary>The number of probe-side rows currently held in memory.</summary>
    internal int InMemoryProbeRowCount => _probeRows?.Count ?? 0;

    /// <summary>The total number of build-side rows (in-memory + spilled, including any staged-but-not-flushed rows).</summary>
    internal int TotalBuildRowCount => InMemoryBuildRowCount + _spilledBuildRowCount + (_buildStaging?.Count ?? 0);

    /// <summary>The total number of probe-side rows (in-memory + spilled, including any staged-but-not-flushed rows).</summary>
    internal int TotalProbeRowCount => InMemoryProbeRowCount + _spilledProbeRowCount + (_probeStaging?.Count ?? 0);

    /// <summary>Whether build-side rows have been spilled to disk.</summary>
    internal bool IsBuildSpilled => _buildSpilled;

    /// <summary>Whether probe-side rows have been spilled to disk.</summary>
    internal bool IsProbeSpilled => _probeSpilled;

    /// <summary>
    /// Adds a build-side row. While the partition is in memory, the row's values are stabilised
    /// into the retention arena; once spilled, the row is staged into a buffer batch and flushed
    /// to the spiller when the buffer is full.
    /// </summary>
    /// <param name="row">The row to add.</param>
    /// <param name="sourceArena">
    /// The arena holding the row's payload bytes (typically the source batch's arena, or the
    /// prior partition's <see cref="RetentionArena"/> when re-partitioning). May be null for
    /// inline-only rows; arena-backed values with a null source arena will be stored without
    /// stabilization and may become stale once their original arena is recycled.
    /// </param>
    internal void AddBuildRow(Row row, Arena? sourceArena)
    {
        _spillSchema ??= row.ColumnLookup;
        _buildSchema ??= row.ColumnLookup;

        if (_buildSpilled)
        {
            AppendToStaging(ref _buildStaging, row, sourceArena, BuildSlot);
        }
        else
        {
            _buildRows!.Add(StabilizeRow(row, sourceArena));
        }
    }

    /// <summary>
    /// Adds a probe-side row. See <see cref="AddBuildRow"/> for stabilization semantics.
    /// </summary>
    internal void AddProbeRow(Row row, Arena? sourceArena)
    {
        _spillSchema ??= row.ColumnLookup;
        _probeSchema ??= row.ColumnLookup;

        if (_probeSpilled)
        {
            AppendToStaging(ref _probeStaging, row, sourceArena, ProbeSlot);
        }
        else
        {
            _probeRows!.Add(StabilizeRow(row, sourceArena));
        }
    }

    private Row StabilizeRow(Row row, Arena? sourceArena)
    {
        ReadOnlySpan<DataValue> source = row.RawValues;
        DataValue[] copy = _pool is not null
            ? _pool.Rent(source.Length)
            : new DataValue[source.Length];

        if (sourceArena is null)
        {
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = source[i];
            }
        }
        else
        {
            _retentionArena ??= _arenaPool.Backing.RentArena();
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = DataValueRetention.Stabilize(source[i], sourceArena, _retentionArena);
            }
        }

        return new Row(row.ColumnLookup, copy);
    }

    private void AppendToStaging(ref RowBatch? staging, Row row, Arena? sourceArena, int spillSlot)
    {
        EnsureSpiller();
        if (staging is null)
        {
            staging = _arenaPool.RentRowBatch(_spillSchema!, SpillStagingCapacity, arena: null);
        }

        DataValue[] values = _arenaPool.RentDataValues(row.RawValues.Length);
        if (sourceArena is null)
        {
            for (int i = 0; i < row.RawValues.Length; i++)
            {
                values[i] = row.RawValues[i];
            }
        }
        else
        {
            for (int i = 0; i < row.RawValues.Length; i++)
            {
                values[i] = DataValueRetention.Stabilize(row.RawValues[i], sourceArena, staging.Arena);
            }
        }
        staging.Add(values);

        if (staging.IsFull)
        {
            int flushed = staging.Count;
            _spiller!.Write(staging, spillSlot);
            if (spillSlot == BuildSlot) _spilledBuildRowCount += flushed;
            else _spilledProbeRowCount += flushed;
            staging = null;
        }
    }

    /// <summary>
    /// Flushes in-memory build rows to disk. Subsequent <see cref="AddBuildRow"/> calls
    /// stage rows into a buffer batch that is flushed when full.
    /// </summary>
    internal void SpillBuildToDisk()
    {
        if (_buildSpilled)
        {
            return;
        }

        EnsureSpiller();
        FlushInMemoryRowsToSpill(_buildRows, BuildSlot);
        ReturnInMemoryArrays(_buildRows);
        _buildRows = null;
        _buildSpilled = true;
    }

    /// <summary>
    /// Flushes in-memory probe rows to disk. Subsequent <see cref="AddProbeRow"/> calls
    /// stage rows into a buffer batch that is flushed when full.
    /// </summary>
    internal void SpillProbeToDisk()
    {
        if (_probeSpilled)
        {
            return;
        }

        EnsureSpiller();
        FlushInMemoryRowsToSpill(_probeRows, ProbeSlot);
        ReturnInMemoryArrays(_probeRows);
        _probeRows = null;
        _probeSpilled = true;
    }

    private void EnsureSpiller()
    {
        if (_spiller is not null)
        {
            return;
        }

        if (_spillSchema is null)
        {
            // No rows ever added — defer until something is actually written. The spill
            // flag is set regardless so future AddBuildRow/AddProbeRow take the spilled path,
            // which captures the schema from the first row.
            return;
        }

        Directory.CreateDirectory(_spillDirectory);
        _spiller = new SpillReaderWriter(_arenaPool, _spillSchema, _spillDirectory, partitionCount: 2);
    }

    private void FlushInMemoryRowsToSpill(List<Row>? rows, int spillSlot)
    {
        if (rows is null || rows.Count == 0 || _spillSchema is null)
        {
            return;
        }

        EnsureSpiller();

        // Chunk the in-memory rows into batches that share the retention arena. Stabilize is a
        // no-op when source equals target, so the spiller's Stabilize-on-write detects that the
        // values already point at the retention arena and copies into the consolidated arena
        // exactly once.
        Arena? retention = _retentionArena;
        int total = rows.Count;
        int chunkSize = SpillStagingCapacity;

        for (int start = 0; start < total; start += chunkSize)
        {
            int batchSize = System.Math.Min(chunkSize, total - start);
            RowBatch batch = _arenaPool.RentRowBatch(_spillSchema, batchSize, retention);

            for (int i = 0; i < batchSize; i++)
            {
                batch.Add(rows[start + i].RawValues);
            }

            // Spiller.Write returns the batch (releasing the retention-arena reference rented above).
            // Each Row's RawValues array was rented from _pool; the spiller's pool returns it via
            // pool.ReturnRowBatch → PoolBacking.ReturnRowBatch. Both LocalBufferPool.Rent and
            // PoolBacking.RentDataValues hand out arrays from the same backing PoolBacking, so the
            // return path is symmetric.
            _spiller!.Write(batch, spillSlot);
            if (spillSlot == BuildSlot) _spilledBuildRowCount += batchSize;
            else _spilledProbeRowCount += batchSize;
        }
    }

    private void ReturnInMemoryArrays(List<Row>? rows)
    {
        if (rows is null || _pool is null)
        {
            return;
        }

        // The spiller's Write already returned the DataValue arrays via pool.ReturnRowBatch,
        // so this is a defensive no-op for the post-spill path. Kept for the (currently
        // unreachable) future case where in-memory rows are dropped without being spilled.
        _ = rows;
    }

    /// <summary>
    /// Returns the in-memory build rows. Only valid when <see cref="IsBuildSpilled"/> is false.
    /// </summary>
    internal IReadOnlyList<Row> GetInMemoryBuildRows()
    {
        return _buildRows ?? (IReadOnlyList<Row>)[];
    }

    /// <summary>
    /// Returns the in-memory probe rows. Only valid when <see cref="IsProbeSpilled"/> is false.
    /// </summary>
    internal IReadOnlyList<Row> GetInMemoryProbeRows()
    {
        return _probeRows ?? (IReadOnlyList<Row>)[];
    }

    /// <summary>
    /// Reads all spilled build-side rows. Only valid when <see cref="IsBuildSpilled"/> is true.
    /// Yielded rows reference batches retained by this partition until <see cref="Dispose"/>.
    /// </summary>
    internal IEnumerable<Row> ReadSpilledBuildRows()
    {
        FlushStagingToSpill(ref _buildStaging, BuildSlot);
        return ReplaySlot(BuildSlot, _buildSchema!);
    }

    /// <summary>
    /// Reads all spilled probe-side rows. Only valid when <see cref="IsProbeSpilled"/> is true.
    /// Yielded rows reference batches retained by this partition until <see cref="Dispose"/>.
    /// </summary>
    internal IEnumerable<Row> ReadSpilledProbeRows()
    {
        FlushStagingToSpill(ref _probeStaging, ProbeSlot);
        return ReplaySlot(ProbeSlot, _probeSchema!);
    }

    private void FlushStagingToSpill(ref RowBatch? staging, int spillSlot)
    {
        if (staging is null || staging.Count == 0)
        {
            if (staging is not null)
            {
                _arenaPool.ReturnRowBatch(staging);
                staging = null;
            }
            return;
        }

        int flushed = staging.Count;
        _spiller!.Write(staging, spillSlot);
        if (spillSlot == BuildSlot) _spilledBuildRowCount += flushed;
        else _spilledProbeRowCount += flushed;
        staging = null;
    }

    private IEnumerable<Row> ReplaySlot(int spillSlot, ColumnLookup outputLookup)
    {
        if (_spiller is null)
        {
            yield break;
        }

        _replayBatches ??= new List<RowBatch>();

        IAsyncEnumerator<RowBatch> enumerator = _spiller
            .ReplayPartitionAsync(_context, outputLookup, spillSlot)
            .GetAsyncEnumerator(_context.CancellationToken);

        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                RowBatch batch = enumerator.Current;
                _replayBatches.Add(batch);
                for (int i = 0; i < batch.Count; i++)
                {
                    yield return batch[i];
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Disposes the partition: closes spill files, returns retained replay batches and the
    /// retention arena, and deletes the temporary spill directory.
    /// </summary>
    public void Dispose()
    {
        if (_buildStaging is not null)
        {
            _arenaPool.ReturnRowBatch(_buildStaging);
            _buildStaging = null;
        }

        if (_probeStaging is not null)
        {
            _arenaPool.ReturnRowBatch(_probeStaging);
            _probeStaging = null;
        }

        if (_replayBatches is not null)
        {
            foreach (RowBatch batch in _replayBatches)
            {
                _arenaPool.ReturnRowBatch(batch);
            }
            _replayBatches = null;
        }

        if (_buildRows is not null && _pool is not null)
        {
            foreach (Row row in _buildRows)
            {
                _pool.ReturnValues(row);
            }
        }

        if (_probeRows is not null && _pool is not null)
        {
            foreach (Row row in _probeRows)
            {
                _pool.ReturnValues(row);
            }
        }

        _buildRows = null;
        _probeRows = null;

        _spiller?.Dispose();
        _spiller = null;

        if (_retentionArena is not null)
        {
            _arenaPool.Backing.TryReturn(_retentionArena);
            _retentionArena = null;
        }
    }
}
