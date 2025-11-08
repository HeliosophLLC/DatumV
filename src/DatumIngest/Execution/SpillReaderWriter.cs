using System.Diagnostics.CodeAnalysis;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution;

/// <summary>
/// Spill-to-disk helper that owns a temp file plus a single consolidated <see cref="Arena"/>
/// holding every arena-backed payload referenced by spilled rows. Used by operators that
/// need to evict materialized <see cref="RowBatch"/> state when a memory budget is exceeded.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="Write(RowBatch)"/> call stabilises the batch's <see cref="DataValue"/>s into
/// the consolidated arena via <see cref="DataValueRetention.Stabilize"/>, writes their raw
/// 20-byte structs to the spill file, then returns the input batch to the pool. The
/// per-batch arena is released; payloads survive in the consolidated arena.
/// </para>
/// <para>
/// <see cref="ReplayAsync"/> opens the spill file for reading, materialises rows whose
/// <c>_p0</c>/<c>_p1</c> reference the consolidated arena, and yields output batches that
/// share that arena (so reads of arena-backed values resolve correctly).
/// </para>
/// <para>
/// Designed to be reusable by other spilling operators (OrderBy, GroupBy, Distinct, etc.)
/// once they migrate off the throw-stub <c>RowSerializer.WriteDataValue</c> path.
/// </para>
/// </remarks>
internal sealed class SpillReaderWriter : IDisposable
{
    private readonly Pool _pool;
    private readonly ColumnLookup _schema;
    private readonly Arena _consolidatedArena;
    private readonly string _spillDirectory;
    private readonly string _spillFilePath;

    private FileStream? _writeStream;
    private BinaryWriter? _writer;
    private bool _schemaWritten;
    private long _rowCount;
    /// <summary>
    /// Byte offset at which row data begins (i.e. the size of the schema header). Captured
    /// after WriteSchema completes; used by <see cref="ReplayRangeAsync"/> to seek directly
    /// to a row offset without re-parsing the header. Zero until the first non-empty Write.
    /// </summary>
    private long _rowDataStartByte;
    private bool _disposed;

    /// <summary>Bytes occupied by a single serialized row. Schema-stride is fixed once schema is written.</summary>
    private int RowStrideBytes => _schema.Count * DataValueRawSize;

    /// <summary>Size of a single <see cref="DataValue"/> on disk. Mirrors RowSerializer's compact codec stride.</summary>
    private const int DataValueRawSize = 20;

    /// <summary>
    /// Creates a spiller bound to <paramref name="pool"/> and <paramref name="schema"/>.
    /// Allocates a file-backed consolidated arena under <paramref name="spillDirectory"/> so
    /// the OS can page payload bytes out of working set under memory pressure — the actual
    /// memory-relief feature that "spill" implies. The arena's file and the row-metadata
    /// spill file live in the same GUID-prefixed subdirectory and are removed together on
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
    /// reduce growth churn (file-backed grow requires unmap → SetLength → remap, more
    /// expensive than anonymous grow). Callers should sum the existing per-batch arena
    /// capacities they're about to spill — and double for headroom — when they have one
    /// to hand. Floored by Arena's own minimum.
    /// </param>
    public SpillReaderWriter(
        Pool pool,
        ColumnLookup schema,
        string spillDirectory,
        int initialArenaCapacity = 1024 * 1024)
    {
        _pool = pool;
        _schema = schema;

        _spillDirectory = Path.Combine(
            spillDirectory,
            $"datum-spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_spillDirectory);
        _spillFilePath = Path.Combine(_spillDirectory, "data.spill");

        // File-backed: bytes live on disk, OS pages them in/out as touched. The dispose
        // path routes through pool.ReturnArena → PoolBacking.TryReturn, which sees
        // IsFileBacked=true and disposes (deleting data.arena) instead of pooling.
        string arenaFilePath = Path.Combine(_spillDirectory, "data.arena");
        _consolidatedArena = Arena.CreateFileBacked(arenaFilePath, initialArenaCapacity);
        _consolidatedArena.AddReference();
    }

    /// <summary>The consolidated arena that backs every spilled arena-stored payload.</summary>
    public Arena ConsolidatedArena => _consolidatedArena;

    /// <summary>The schema all spilled rows share.</summary>
    public ColumnLookup Schema => _schema;

    /// <summary>Total number of rows handed to <see cref="Write(RowBatch)"/> so far.</summary>
    public long RowCount => _rowCount;

    /// <summary>The spill file path on disk; exists only after the first non-empty Write.</summary>
    public string SpillFilePath => _spillFilePath;

    /// <summary>
    /// Stabilises every value in <paramref name="batch"/> into the consolidated arena, writes
    /// the raw DataValue structs to the spill file, and returns the batch (releasing its
    /// per-batch arena). Empty batches are returned without touching the file.
    /// </summary>
    public void Write(RowBatch batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (batch.Count == 0)
        {
            _pool.ReturnRowBatch(batch);
            return;
        }

        EnsureWriterOpen();

        if (!_schemaWritten)
        {
            RowSerializer.WriteSchema(_writer, _schema);
            // Flush so we can read the underlying stream's position — BinaryWriter buffers,
            // so the byte offset of "first row" must be captured against bytes actually
            // written to the FileStream.
            _writer.Flush();
            _rowDataStartByte = _writeStream.Position;
            _schemaWritten = true;
        }

        Arena sourceArena = batch.Arena;
        int columnCount = _schema.Count;

        try
        {
            for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
            {
                Row row = batch[rowIndex];
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    DataValue stabilized = DataValueRetention.Stabilize(
                        row[columnIndex], sourceArena, _consolidatedArena);
                    RowSerializer.WriteStabilizedDataValue(_writer, stabilized);
                }
                _rowCount++;
            }
        }
        finally
        {
            _pool.ReturnRowBatch(batch);
        }
    }

    /// <summary>Total number of rows handed to <see cref="Write(RowBatch)"/> so far. Useful for
    /// recursive-CTE iteration tracking — capture the value at iteration start to compute the
    /// working-table row range for the next iteration.</summary>
    public long RowsWritten => _rowCount;

    /// <summary>
    /// Replays every spilled row as <see cref="RowBatch"/>es whose arena is the consolidated
    /// arena (so arena-backed values resolve correctly). Returns no batches if nothing was
    /// ever written. The writer remains open across replays — callers may continue to
    /// <see cref="Write(RowBatch)"/> additional rows after a replay completes; subsequent
    /// replays will see them.
    /// </summary>
    public IAsyncEnumerable<RowBatch> ReplayAsync(
        ExecutionContext context,
        ColumnLookup outputLookup)
    {
        return ReplayRangeAsync(context, outputLookup, startRow: 0, rowCount: long.MaxValue);
    }

    /// <summary>
    /// Replays a specific row range — useful for recursive CTE working-table semantics where
    /// each iteration only needs the previous iteration's output (i.e. rows
    /// <c>[previousIterationEnd, currentIterationEnd)</c>). Pass <c>rowCount = long.MaxValue</c>
    /// to read from <paramref name="startRow"/> to the end of what's been written so far.
    /// </summary>
    /// <param name="context">Provides cancellation and the output batch size.</param>
    /// <param name="outputLookup">Schema for the output batches; column count must match the spilled schema.</param>
    /// <param name="startRow">Zero-based row index to start reading from. Must be ≤ <see cref="RowsWritten"/>.</param>
    /// <param name="rowCount">Maximum number of rows to read. Capped at <c>RowsWritten - startRow</c>.</param>
    public async IAsyncEnumerable<RowBatch> ReplayRangeAsync(
        ExecutionContext context,
        ColumnLookup outputLookup,
        long startRow,
        long rowCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_schemaWritten || startRow >= _rowCount || rowCount <= 0)
        {
            yield break;
        }

        // Cap rowCount at what's actually written.
        long maxAvailable = _rowCount - startRow;
        if (rowCount > maxAvailable) rowCount = maxAvailable;

        // Pending writes must hit the OS file cache before the reader can see them — readers
        // observe FileStream-level bytes, not BinaryWriter's internal buffer.
        _writer?.Flush();
        _writeStream?.Flush();

        // Reader uses FileShare.ReadWrite so it can coexist with the still-open writer
        // (which has FileAccess.Write + FileShare.Read). Each handle's FileShare must
        // include the access modes of every other open handle: writer's "I'm Write, allow
        // Read" matches reader's "I'm Read, allow ReadWrite".
        FileStream readStream = new(
            _spillFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536,
            useAsync: true);

        long rowStride = RowStrideBytes;
        long startByte = _rowDataStartByte + (startRow * rowStride);
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
                            values[columnIndex] = RowSerializer.ReadStabilizedDataValue(reader);
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
                    _pool.ReturnRowBatch(outputBatch);
                }
            }
        }
    }

    [MemberNotNull(nameof(_writer), nameof(_writeStream))]
    private void EnsureWriterOpen()
    {
        if (_writer is null || _writeStream is null)
        {
            _writeStream = new FileStream(
                _spillFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536);
            _writer = new BinaryWriter(_writeStream);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _writer?.Dispose();
        _writer = null;
        _writeStream?.Dispose();
        _writeStream = null;

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
}
