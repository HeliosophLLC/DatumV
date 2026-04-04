using System.Runtime.CompilerServices;
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
/// Reading spilled rows back via <see cref="ReadSpilledBuildRowsAsync"/> /
/// <see cref="ReadSpilledProbeRowsAsync"/> drives the spiller's async replay and
/// retains the yielded batches until <see cref="Dispose"/> — the consumer
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

    // Build and probe sides spill to separate SpillReaderWriter instances. They legitimately
    // have different schemas (different aliases, and — in a JOIN — different column counts
    // when projections trim each side independently), so a single shared spiller's per-slot
    // schema would mismatch the row stride and crash mid-write. Each spiller uses its own
    // file-backed consolidated arena; for JOIN keys (typically inline values) cross-arena
    // equality "just works", so the shared-arena invariant the doc claims for hash-partitioned
    // set operations doesn't apply here.
    private SpillReaderWriter? _buildSpiller;
    private SpillReaderWriter? _probeSpiller;
    private bool _buildSpilled;
    private bool _probeSpilled;
    private int _spilledBuildRowCount;
    private int _spilledProbeRowCount;

    private RowBatch? _buildStaging;
    private RowBatch? _probeStaging;
    private bool _disposed;

    /// <summary>
    /// Per-slot column lookup. Build and probe rows have legitimately different schemas —
    /// different aliases and (under independent projection) different column counts.
    /// Each side's spiller is constructed with the matching schema so the row stride and
    /// column-iteration loops use the correct count.
    /// </summary>
    private ColumnLookup? _buildSchema;
    private ColumnLookup? _probeSchema;

    /// <summary>
    /// Batches yielded by replay that callers still hold <see cref="Row"/> references into.
    /// Returned to the pool on <see cref="Dispose"/>.
    /// </summary>
    private List<RowBatch>? _replayBatches;

    private readonly string _spillDirectory;
    private readonly Pool _arenaPool;
    private readonly ExecutionContext _context;

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
    internal SpillPartition(
        string spillDirectory,
        int partitionIndex,
        Pool arenaPool,
        ExecutionContext context,
        int estimatedBuildRows = 0)
    {
        _ = partitionIndex;
        _spillDirectory = spillDirectory;
        _arenaPool = arenaPool;
        _context = context;
        if (estimatedBuildRows > 0)
        {
            _buildRows = new List<Row>(estimatedBuildRows);
        }
    }

    /// <summary>
    /// Retention store for in-memory rows: the per-query <see cref="ExecutionContext.Store"/>.
    /// Exposed so callers handing rows from one partition to another (e.g. recursive
    /// repartitioning) can pass it as the source arena.
    /// </summary>
    /// <remarks>
    /// Historically this was a freshly-rented per-partition arena. That broke the
    /// one-arena-per-query invariant: build-side <see cref="DataValue"/>s ended up
    /// with offsets relative to the private retention arena, but <c>JoinSchema.CombinePooledValues</c>
    /// spliced them into combined rows that downstream operators then read against
    /// <c>outputBatch.Arena</c> (the query Store) — at the same offset, in a different
    /// arena, getting whatever bytes happened to be there. The fix is to stabilize
    /// into <see cref="ExecutionContext.Store"/> from the start: same-arena stabilization
    /// is the <see cref="DataValueRetention.Stabilize"/> fast path (no copy), and
    /// cross-arena stabilization lands the bytes in the arena the consumers actually
    /// read from.
    /// </remarks>
    internal Arena RetentionArena => _context.Store;

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
        DataValue[] copy = _arenaPool.RentDataValues(source.Length);

        if (sourceArena is null)
        {
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = source[i];
            }
        }
        else
        {
            // Stabilize into the per-query Store, not a private partition arena.
            // Under one-arena-per-query (sourceArena == context.Store), this is the
            // Stabilize fast path — DataValues pass through unchanged. When source
            // and Store differ (e.g. a sidecar-resident input arena, or a fresh
            // arena from a future operator), the bytes get copied into Store so
            // every downstream consumer that reads via outputBatch.Arena resolves
            // the offset against the same arena the bytes actually live in.
            //
            // Previous design used a per-partition retention arena, which broke
            // the invariant: JoinSchema.CombinePooledValues splices left + right
            // values without re-stabilizing, and the JOIN's outputBatch.Arena is
            // context.Store (rented via context.RentRowBatch). Build-side values'
            // BackedOffsets pointed into the private retention arena; downstream
            // reads against Store at those offsets returned whatever bytes
            // happened to share the offset, manifesting as "Float32 array
            // reading as PNG image bytes" in production.
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = DataValueRetention.Stabilize(source[i], sourceArena, _context.Store);
            }
        }

        return new Row(row.ColumnLookup, copy);
    }

    private void AppendToStaging(ref RowBatch? staging, Row row, Arena? sourceArena, int spillSlot)
    {
        SpillReaderWriter spiller = EnsureSpiller(spillSlot);
        ColumnLookup slotSchema = spillSlot == BuildSlot ? _buildSchema! : _probeSchema!;
        if (staging is null)
        {
            staging = _arenaPool.RentRowBatch(slotSchema, SpillStagingCapacity, arena: null);
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
            // Null the field BEFORE handing the batch to the spiller. If
            // spiller.Write throws mid-body, its finally still disposes the
            // batch — leaving the field pointing at a disposed batch would
            // cause SpillPartition.Dispose() to throw an ObjectDisposedException
            // in its own finally, replacing the original exception and hiding
            // the real failure cause.
            int flushed = staging.Count;
            RowBatch toFlush = staging;
            staging = null;
            spiller.Write(toFlush, partition: 0);
            if (spillSlot == BuildSlot) _spilledBuildRowCount += flushed;
            else _spilledProbeRowCount += flushed;
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

        if (_buildSchema is not null)
        {
            EnsureSpiller(BuildSlot);
            FlushInMemoryRowsToSpill(_buildRows, BuildSlot);
        }
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

        if (_probeSchema is not null)
        {
            EnsureSpiller(ProbeSlot);
            FlushInMemoryRowsToSpill(_probeRows, ProbeSlot);
        }
        ReturnInMemoryArrays(_probeRows);
        _probeRows = null;
        _probeSpilled = true;
    }

    /// <summary>
    /// Returns the spiller for the given slot, lazily constructing it. Each slot has its
    /// own <see cref="SpillReaderWriter"/> with its own file-backed consolidated arena
    /// because build and probe schemas — and therefore row strides — legitimately differ.
    /// </summary>
    private SpillReaderWriter EnsureSpiller(int spillSlot)
    {
        if (spillSlot == BuildSlot)
        {
            if (_buildSpiller is null)
            {
                if (_buildSchema is null)
                {
                    throw new InvalidOperationException(
                        "SpillPartition build-side spiller requested before the first " +
                        "build row established the schema.");
                }
                Directory.CreateDirectory(_spillDirectory);
                _buildSpiller = new SpillReaderWriter(
                    _arenaPool,
                    _buildSchema,
                    Path.Combine(_spillDirectory, "build"));
            }
            return _buildSpiller;
        }
        else
        {
            if (_probeSpiller is null)
            {
                if (_probeSchema is null)
                {
                    throw new InvalidOperationException(
                        "SpillPartition probe-side spiller requested before the first " +
                        "probe row established the schema.");
                }
                Directory.CreateDirectory(_spillDirectory);
                _probeSpiller = new SpillReaderWriter(
                    _arenaPool,
                    _probeSchema,
                    Path.Combine(_spillDirectory, "probe"));
            }
            return _probeSpiller;
        }
    }

    private void FlushInMemoryRowsToSpill(List<Row>? rows, int spillSlot)
    {
        if (rows is null || rows.Count == 0)
        {
            return;
        }

        SpillReaderWriter spiller = EnsureSpiller(spillSlot);
        ColumnLookup slotSchema = spillSlot == BuildSlot ? _buildSchema! : _probeSchema!;

        // Chunk the in-memory rows into batches that share the retention arena (the per-query
        // Store). Stabilize is a no-op when source equals target, so the spiller's
        // Stabilize-on-write detects that the values already point at the retention arena and
        // copies into the consolidated arena exactly once.
        Arena retention = _context.Store;
        int total = rows.Count;
        int chunkSize = SpillStagingCapacity;

        for (int start = 0; start < total; start += chunkSize)
        {
            int batchSize = System.Math.Min(chunkSize, total - start);
            RowBatch batch = _arenaPool.RentRowBatch(slotSchema, batchSize, retention);

            for (int i = 0; i < batchSize; i++)
            {
                batch.Add(rows[start + i].RawValues);
            }

            // Spiller.Write returns the batch (releasing the retention-arena reference rented above).
            // Each Row's RawValues array was rented from _arenaPool; the spiller's pool returns it via
            // pool.ReturnRowBatch → PoolBacking.ReturnRowBatch. Both LocalBufferPool.Rent and
            // PoolBacking.RentDataValues hand out arrays from the same backing PoolBacking, so the
            // return path is symmetric.
            spiller.Write(batch, partition: 0);
            if (spillSlot == BuildSlot) _spilledBuildRowCount += batchSize;
            else _spilledProbeRowCount += batchSize;
        }
    }

    private void ReturnInMemoryArrays(List<Row>? rows)
    {
        if (rows is null || _arenaPool is null)
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
    internal IAsyncEnumerable<Row> ReadSpilledBuildRowsAsync(CancellationToken cancellationToken)
    {
        FlushStagingToSpill(ref _buildStaging, BuildSlot);
        return ReplaySlotAsync(BuildSlot, _buildSchema!, cancellationToken);
    }

    /// <summary>
    /// Reads all spilled probe-side rows. Only valid when <see cref="IsProbeSpilled"/> is true.
    /// Yielded rows reference batches retained by this partition until <see cref="Dispose"/>.
    /// </summary>
    internal IAsyncEnumerable<Row> ReadSpilledProbeRowsAsync(CancellationToken cancellationToken)
    {
        FlushStagingToSpill(ref _probeStaging, ProbeSlot);
        return ReplaySlotAsync(ProbeSlot, _probeSchema!, cancellationToken);
    }

    private void FlushStagingToSpill(ref RowBatch? staging, int spillSlot)
    {
        if (staging is null) return;

        // Same null-before-act pattern as AppendToStaging: capture the batch in a
        // local, null the field, then dispatch. If the pool/spiller throws while
        // returning or writing, the field is already null and Dispose won't trip
        // on a half-disposed batch in its own finally.
        RowBatch toFlush = staging;
        staging = null;

        if (toFlush.Count == 0)
        {
            _arenaPool.ReturnRowBatch(toFlush);
            return;
        }

        int flushed = toFlush.Count;
        SpillReaderWriter spiller = EnsureSpiller(spillSlot);
        spiller.Write(toFlush, partition: 0);
        if (spillSlot == BuildSlot) _spilledBuildRowCount += flushed;
        else _spilledProbeRowCount += flushed;
    }

    private async IAsyncEnumerable<Row> ReplaySlotAsync(
        int spillSlot,
        ColumnLookup outputLookup,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SpillReaderWriter? spiller = spillSlot == BuildSlot ? _buildSpiller : _probeSpiller;
        if (spiller is null)
        {
            yield break;
        }

        _replayBatches ??= new List<RowBatch>();

        await foreach (RowBatch batch in spiller
            .ReplayPartitionAsync(_context, outputLookup, partition: 0)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            _replayBatches.Add(batch);
            for (int i = 0; i < batch.Count; i++)
            {
                yield return batch[i];
            }
        }
    }

    /// <summary>
    /// Disposes the partition: closes spill files, returns retained replay batches and the
    /// retention arena, and deletes the temporary spill directory.
    /// Throws on double-Dispose — a second call indicates a bug in the caller's
    /// lifecycle (e.g. both a normal-path Dispose and a finally-path Dispose firing
    /// for the same partition instance), and we want the stack trace to localise it
    /// rather than silently corrupting pool accounting.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(SpillPartition),
                "SpillPartition.Dispose() called twice on the same instance. " +
                "This indicates a lifecycle bug in the join executor — each partition " +
                "should be disposed exactly once at the end of the join.");
        }
        _disposed = true;

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

        if (_buildRows is not null && _arenaPool is not null)
        {
            foreach (Row row in _buildRows)
            {
                _arenaPool.ReturnRow(row);
            }
        }

        if (_probeRows is not null && _arenaPool is not null)
        {
            foreach (Row row in _probeRows)
            {
                _arenaPool.ReturnRow(row);
            }
        }

        _buildRows = null;
        _probeRows = null;

        _buildSpiller?.Dispose();
        _buildSpiller = null;
        _probeSpiller?.Dispose();
        _probeSpiller = null;

        // No retention-arena release here: we don't own context.Store. The
        // ExecutionContext disposes it when the query ends.
    }
}
