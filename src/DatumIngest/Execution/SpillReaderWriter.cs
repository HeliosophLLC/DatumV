using System.Runtime.InteropServices;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Spill-to-disk helper that owns one or more partition files plus a single consolidated
/// <see cref="Arena"/> holding every arena-backed payload referenced by spilled rows. Used
/// by operators that need to evict materialized <see cref="RowBatch"/> state when a memory
/// budget is exceeded.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Partitioning.</strong> Constructed with <c>partitionCount = 1</c> (default), the
/// spiller behaves as a single-stream sink — one file, sequential append, used by the CTE
/// and recursive-CTE materialization paths. Constructed with <c>partitionCount &gt; 1</c>,
/// the caller picks a partition index per write (typically <c>hashCode % partitionCount</c>)
/// and the spiller maintains an independent file + writer per partition, all sharing the
/// same consolidated arena. Used by hash-partitioned spill operators (UNION DISTINCT,
/// INTERSECT, EXCEPT, future GroupBy/grace-hash-join).
/// </para>
/// <para>
/// <strong>Single arena, multiple partitions.</strong> The consolidated arena is shared
/// across every partition. Arena-backed payloads from any partition's writes resolve
/// correctly during any partition's replay — the partition split is purely about row
/// metadata grouping, not about isolating payload bytes. This is the load-bearing property
/// for set operations: when probing left rows against a per-partition right hash set, both
/// sides see the same arena and string equality "just works" without per-partition copies.
/// </para>
/// <para>
/// <strong>Write path</strong> (per <see cref="Write(RowBatch, int)"/>): stabilises the
/// batch's <see cref="DataValue"/>s into the consolidated arena via
/// <see cref="DataValueRetention.Stabilize"/>, writes their raw 20-byte structs to the
/// partition's spill file, returns the input batch.
/// </para>
/// <para>
/// <strong>Read path</strong> (per <see cref="ReplayPartitionAsync"/> /
/// <see cref="ReplayPartitionRangeAsync"/>): yields output batches whose arena is the
/// consolidated arena (so arena-backed values resolve correctly). The writer remains open
/// across replays — operators that interleave write and read phases (e.g. recursive CTE,
/// post-build-side join probe) can keep appending after a partial replay completes.
/// </para>
/// </remarks>
internal sealed class SpillReaderWriter : IDisposable
{
    private readonly Pool _pool;
    private readonly ColumnLookup _schema;
    private readonly Arena _consolidatedArena;
    private readonly string _spillDirectory;
    private readonly int _partitionCount;

    // All per-partition state is arrays of length _partitionCount. Index 0 is the only
    // active partition for partitionCount=1 (single-stream mode).
    private readonly FileStream?[] _writeStreams;
    private readonly BinaryWriter?[] _writers;
    private readonly bool[] _schemaWritten;
    private readonly long[] _rowCounts;
    /// <summary>
    /// Byte offset within each partition's file at which row data begins (i.e. the size of
    /// the schema header). Captured after that partition's WriteSchema completes; used by
    /// <see cref="ReplayPartitionRangeAsync"/> to seek directly to a row offset without
    /// re-parsing the header.
    /// </summary>
    private readonly long[] _rowDataStartBytes;
    private readonly string[] _spillFilePaths;
    private bool _disposed;

    /// <summary>Bytes occupied by a single serialized row. Schema-stride is fixed once schema is written.</summary>
    private int RowStrideBytes => _schema.Count * DataValueRawSize;

    /// <summary>Size of a single <see cref="DataValue"/> on disk. Mirrors RowSerializer's compact codec stride.</summary>
    private const int DataValueRawSize = DataValue.SizeBytes;

    /// <summary>
    /// Creates a spiller bound to <paramref name="pool"/> and <paramref name="schema"/>.
    /// Allocates a file-backed consolidated arena under <paramref name="spillDirectory"/> so
    /// the OS can page payload bytes out of working set under memory pressure — the actual
    /// memory-relief feature that "spill" implies. The arena's file and the partition spill
    /// files all live in the same GUID-prefixed subdirectory and are removed together on
    /// <see cref="Dispose"/>.
    /// </summary>
    /// <param name="pool">Pool used for replay-batch allocations and refcount lifecycle.</param>
    /// <param name="schema">Schema all spilled rows share.</param>
    /// <param name="spillDirectory">
    /// Parent directory for this spiller's temp subdirectory. Typically
    /// <c>ExecutionContext.SpillDirectory</c> — the deployment-configured spill spindle.
    /// </param>
    /// <param name="initialArenaCapacity">
    /// Pre-size the consolidated arena's backing file to this many bytes. Generous values
    /// reduce growth churn (file-backed grow requires unmap → SetLength → remap). Floored by
    /// Arena's own minimum.
    /// </param>
    /// <param name="partitionCount">
    /// Number of independent spill files. Defaults to 1 (single-stream behaviour). Pass
    /// <c>N &gt; 1</c> for hash-partitioned spill (set operations, hash-aggregate, grace
    /// join). Each partition has its own file, schema-written flag, and row count, but all
    /// partitions share the consolidated arena.
    /// </param>
    public SpillReaderWriter(
        Pool pool,
        ColumnLookup schema,
        string spillDirectory,
        long initialArenaCapacity = 1024 * 1024,
        int partitionCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);

        _pool = pool;
        _schema = schema;
        _partitionCount = partitionCount;

        _spillDirectory = Path.Combine(
            spillDirectory,
            $"datum-spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_spillDirectory);

        _writeStreams = new FileStream?[partitionCount];
        _writers = new BinaryWriter?[partitionCount];
        _schemaWritten = new bool[partitionCount];
        _rowCounts = new long[partitionCount];
        _rowDataStartBytes = new long[partitionCount];
        _spillFilePaths = new string[partitionCount];
        for (int i = 0; i < partitionCount; i++)
        {
            // For partitionCount=1 the file name is data_0.spill — slight churn from the
            // pre-partition-aware "data.spill", but file names are an internal detail.
            // The SpillFilePath / SpillDirectory properties remain the public surface.
            _spillFilePaths[i] = Path.Combine(_spillDirectory, $"data_{i}.spill");
        }

        // File-backed: bytes live on disk, OS pages them in/out as touched. Rented through
        // the pool so the rent counter covers it; the dispose path routes through
        // pool.ReturnArena → PoolBacking.TryReturn, which sees IsFileBacked=true and
        // disposes (deleting data.arena) instead of pooling. The pool's RentFileBackedArena
        // already AddReferences before returning, so the arena starts with refcount 1.
        string arenaFilePath = Path.Combine(_spillDirectory, "data.arena");
        _consolidatedArena = pool.RentFileBackedArena(arenaFilePath, initialArenaCapacity);
    }

    /// <summary>The consolidated arena that backs every spilled arena-stored payload.</summary>
    public Arena ConsolidatedArena => _consolidatedArena;

    /// <summary>The schema all spilled rows share.</summary>
    public ColumnLookup Schema => _schema;

    /// <summary>The directory containing every partition's spill file plus the arena file.</summary>
    public string SpillDirectory => _spillDirectory;

    /// <summary>The number of partitions this spiller was constructed with.</summary>
    public int PartitionCount => _partitionCount;

    /// <summary>
    /// Total number of rows handed to <see cref="Write(RowBatch)"/> / <see cref="Write(RowBatch, int)"/>
    /// across all partitions. For per-partition counts, see <see cref="RowsWrittenInPartition"/>.
    /// </summary>
    public long RowsWritten
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _partitionCount; i++) total += _rowCounts[i];
            return total;
        }
    }

    /// <summary>
    /// Number of rows written to a specific partition. Used by recursive-CTE iteration tracking
    /// (single-stream, partition=0) and by hash-partitioned operators when computing per-partition
    /// drain bounds.
    /// </summary>
    public long RowsWrittenInPartition(int partition)
    {
        ValidatePartition(partition);
        return _rowCounts[partition];
    }

    /// <summary>
    /// Backward-compatible single-stream property: returns the partition-0 file path. For
    /// partitioned spillers, prefer iterating <see cref="SpillDirectory"/> contents or using
    /// per-partition replay.
    /// </summary>
    public string SpillFilePath => _spillFilePaths[0];

    /// <summary>
    /// Single-stream write — equivalent to <c>Write(batch, partition: 0)</c>. Available on
    /// any spiller; partitionCount &gt; 1 spillers should prefer the partition-aware overload.
    /// </summary>
    public void Write(RowBatch batch) => Write(batch, partition: 0);

    /// <summary>
    /// Stabilises every value in <paramref name="batch"/> into the consolidated arena, writes
    /// the raw DataValue structs to <paramref name="partition"/>'s spill file, and returns the
    /// batch (releasing its per-batch arena). Empty batches are returned without touching any
    /// file.
    /// </summary>
    public void Write(RowBatch batch, int partition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePartition(partition);

        if (batch.Count == 0)
        {
            _pool.ReturnRowBatch(batch);
            return;
        }

        EnsureWriterOpen(partition);

        if (!_schemaWritten[partition])
        {
            WriteSchemaHeader(_writers[partition]!, _schema);
            // Flush so we can read the underlying stream's position — BinaryWriter buffers,
            // so the byte offset of "first row" must be captured against bytes actually
            // written to the FileStream.
            _writers[partition]!.Flush();
            _rowDataStartBytes[partition] = _writeStreams[partition]!.Position;
            _schemaWritten[partition] = true;
        }

        Arena sourceArena = batch.Arena;
        int columnCount = _schema.Count;
        BinaryWriter writer = _writers[partition]!;

        try
        {
            for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
            {
                Row row = batch[rowIndex];
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    DataValue stabilized = DataValueRetention.Stabilize(
                        row[columnIndex], sourceArena, _consolidatedArena);
                    WriteRawDataValue(writer, stabilized);
                }
                _rowCounts[partition]++;
            }
        }
        finally
        {
            _pool.ReturnRowBatch(batch);
        }
    }

    /// <summary>
    /// Replays every spilled row from partition 0. Equivalent to
    /// <c>ReplayPartitionAsync(context, lookup, partition: 0)</c>. Useful for single-stream
    /// callers (CTE, recursive CTE in single-stream mode).
    /// </summary>
    public IAsyncEnumerable<RowBatch> ReplayAsync(ExecutionContext context, ColumnLookup outputLookup)
        => ReplayPartitionRangeAsync(context, outputLookup, partition: 0, startRow: 0, rowCount: long.MaxValue);

    /// <summary>
    /// Replays a row range from partition 0. Equivalent to
    /// <c>ReplayPartitionRangeAsync(context, lookup, partition: 0, startRow, rowCount)</c>.
    /// Used by recursive-CTE working-table semantics (read only the previous iteration's slice).
    /// </summary>
    public IAsyncEnumerable<RowBatch> ReplayRangeAsync(
        ExecutionContext context, ColumnLookup outputLookup, long startRow, long rowCount)
        => ReplayPartitionRangeAsync(context, outputLookup, partition: 0, startRow, rowCount);

    /// <summary>
    /// Replays every row written to <paramref name="partition"/>. The writer remains open
    /// across replays — additional writes to any partition (including this one) will be
    /// visible to subsequent replays.
    /// </summary>
    public IAsyncEnumerable<RowBatch> ReplayPartitionAsync(
        ExecutionContext context, ColumnLookup outputLookup, int partition)
        => ReplayPartitionRangeAsync(context, outputLookup, partition, startRow: 0, rowCount: long.MaxValue);

    /// <summary>
    /// Replays a specific row range within <paramref name="partition"/>. Pass
    /// <c>rowCount = long.MaxValue</c> for "from <paramref name="startRow"/> to end of what's
    /// been written".
    /// </summary>
    /// <param name="context">Provides cancellation and the output batch size.</param>
    /// <param name="outputLookup">Schema for the output batches; column count must match the spilled schema.</param>
    /// <param name="partition">Partition index. Must be in <c>[0, PartitionCount)</c>.</param>
    /// <param name="startRow">Zero-based row index within the partition to start reading from.</param>
    /// <param name="rowCount">Maximum number of rows to read. Capped at <c>RowsWrittenInPartition(partition) - startRow</c>.</param>
    public IAsyncEnumerable<RowBatch> ReplayPartitionRangeAsync(
        ExecutionContext context,
        ColumnLookup outputLookup,
        int partition,
        long startRow,
        long rowCount)
    {
        // Eager validation — async iterators defer the body until first MoveNextAsync, so
        // arg-checks inside the iterator wouldn't fire until the consumer pulls. Split the
        // public entry point into a non-iterator validator + an iterator implementation.
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePartition(partition);
        return ReplayPartitionRangeImplAsync(context, outputLookup, partition, startRow, rowCount);
    }

    private async IAsyncEnumerable<RowBatch> ReplayPartitionRangeImplAsync(
        ExecutionContext context,
        ColumnLookup outputLookup,
        int partition,
        long startRow,
        long rowCount)
    {
        long partitionRowCount = _rowCounts[partition];
        if (!_schemaWritten[partition] || startRow >= partitionRowCount || rowCount <= 0)
        {
            yield break;
        }

        // Cap rowCount at what's actually written to this partition.
        long maxAvailable = partitionRowCount - startRow;
        if (rowCount > maxAvailable) rowCount = maxAvailable;

        // Pending writes must hit the OS file cache before the reader can see them — readers
        // observe FileStream-level bytes, not BinaryWriter's internal buffer. Only flush this
        // partition's writer (other partitions' bytes don't affect this read).
        _writers[partition]?.Flush();
        _writeStreams[partition]?.Flush();

        // Reader uses FileShare.ReadWrite so it can coexist with the still-open writer
        // (which has FileAccess.Write + FileShare.Read). Each handle's FileShare must
        // include the access modes of every other open handle: writer's "I'm Write, allow
        // Read" matches reader's "I'm Read, allow ReadWrite".
        FileStream readStream = new(
            _spillFilePaths[partition],
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536,
            useAsync: true);

        long rowStride = RowStrideBytes;
        long startByte = _rowDataStartBytes[partition] + (startRow * rowStride);
        long endByte = startByte + (rowCount * rowStride);

        await using (readStream.ConfigureAwait(false))
        {
            // leaveOpen so the BinaryReader's dispose doesn't take the FileStream with it —
            // FileStream is owned by the await-using above.
            using BinaryReader reader = new(readStream, System.Text.Encoding.UTF8, leaveOpen: true);

            readStream.Seek(startByte, SeekOrigin.Begin);

            int columnCount = outputLookup.Count;
            // Invariant: outputBatch != null ⟺ the producer still owns it. Yielding hands
            // ownership to the consumer, so we null the local *before* yield — otherwise a
            // consumer that breaks/throws/cancels mid-iteration would leave the producer's
            // finally pointing at a batch the consumer already returned to the pool, and we'd
            // double-return it. The post-yield assignment trick doesn't work because that
            // statement only runs on resumption (next MoveNextAsync), not on iterator
            // disposal — disposal jumps straight to finally.
            RowBatch? outputBatch = null;

            try
            {
                while (readStream.Position < endByte)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    DataValue[] values = _pool.RentDataValues(columnCount);
                    try
                    {
                        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                        {
                            values[columnIndex] = ReadRawDataValue(reader);
                        }

                        outputBatch ??= _pool.RentRowBatch(
                            outputLookup, context.BatchSize, _consolidatedArena);
                        outputBatch.Add(values);
                    }
                    catch
                    {
                        _pool.ReturnDataValues(values);
                        throw;
                    }

                    if (outputBatch.IsFull)
                    {
                        RowBatch toYield = outputBatch;
                        outputBatch = null;
                        yield return toYield;
                    }
                }

                if (outputBatch is not null)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }
            }
            finally
            {
                // Only fires for a partially-filled batch we never managed to yield —
                // the consumer doesn't know about it, so we own its cleanup.
                if (outputBatch is not null)
                {
                    context.ReturnRowBatch(outputBatch);
                }
            }
        }
    }

    private void EnsureWriterOpen(int partition)
    {
        if (_writers[partition] is null || _writeStreams[partition] is null)
        {
            _writeStreams[partition] = new FileStream(
                _spillFilePaths[partition],
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536);
            _writers[partition] = new BinaryWriter(_writeStreams[partition]!);
        }
    }

    private void ValidatePartition(int partition)
    {
        if ((uint)partition >= (uint)_partitionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(partition),
                $"Partition {partition} is out of range; spiller has {_partitionCount} partition(s).");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        for (int i = 0; i < _partitionCount; i++)
        {
            _writers[i]?.Dispose();
            _writers[i] = null;
            _writeStreams[i]?.Dispose();
            _writeStreams[i] = null;
        }

        _pool.ReturnArena(_consolidatedArena);

        if (Directory.Exists(_spillDirectory))
        {
            try
            {
                Directory.Delete(_spillDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup — OS handles temp files on shutdown.
            }
        }
    }

    // ─────────────── Wire format helpers (formerly RowSerializer) ───────────────

    /// <summary>
    /// Writes a schema header (column count + names) to a partition stream.
    /// Read back via <see cref="ReadSchemaHeader"/>.
    /// </summary>
    private static void WriteSchemaHeader(BinaryWriter writer, ColumnLookup columnLookup)
    {
        writer.Write(columnLookup.Count);
        for (int index = 0; index < columnLookup.Count; index++)
        {
            writer.Write(columnLookup.ColumnNames[index]);
        }
    }

    /// <summary>
    /// Reads a schema header written by <see cref="WriteSchemaHeader"/>.
    /// Currently unused — replay paths take the schema from the spiller in hand —
    /// kept for symmetry with the writer.
    /// </summary>
    private static ColumnLookup ReadSchemaHeader(BinaryReader reader)
    {
        int fieldCount = reader.ReadInt32();
        string[] names = new string[fieldCount];
        for (int index = 0; index < fieldCount; index++)
        {
            names[index] = reader.ReadString();
        }
        return new ColumnLookup(names);
    }

    /// <summary>
    /// Writes a <see cref="DataValue"/>'s raw 20-byte struct image. The caller
    /// must stabilise any arena-backed payload into <see cref="ConsolidatedArena"/>
    /// before calling so the offsets resolve on replay. Inline and sidecar-backed
    /// values round-trip without arena dependency.
    /// </summary>
    private static void WriteRawDataValue(BinaryWriter writer, DataValue value)
    {
        Span<byte> buffer = stackalloc byte[DataValueRawSize];
        MemoryMarshal.Write(buffer, in value);
        writer.Write(buffer);
    }

    /// <summary>
    /// Inverse of <see cref="WriteRawDataValue"/>. The returned value's
    /// arena-backed offsets resolve only when read against the same arena
    /// the writer stabilised into (the spiller's <see cref="ConsolidatedArena"/>).
    /// </summary>
    private static DataValue ReadRawDataValue(BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[DataValueRawSize];
        int read = reader.Read(buffer);
        if (read != DataValueRawSize)
        {
            throw new EndOfStreamException(
                $"Expected {DataValueRawSize} bytes for a DataValue, got {read}.");
        }
        return MemoryMarshal.Read<DataValue>(buffer);
    }
}
