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
    private bool _disposed;

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

    /// <summary>
    /// Replays previously spilled rows as <see cref="RowBatch"/>es whose arena is the
    /// consolidated arena (so arena-backed values resolve correctly). Returns no batches if
    /// nothing was ever written.
    /// </summary>
    /// <param name="context">Provides cancellation and the output batch size.</param>
    /// <param name="outputLookup">
    /// Schema for the output batches. May rename the spilled schema's columns (e.g. CTE
    /// explicit column names); the column count and ordinal positions must match.
    /// </param>
    public async IAsyncEnumerable<RowBatch> ReplayAsync(
        ExecutionContext context,
        ColumnLookup outputLookup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_schemaWritten)
        {
            yield break;
        }

        // Close the write side so the OS releases its exclusive write lock. After the first
        // replay the spiller is effectively read-only — Write will throw if called again.
        // (Windows file sharing rejects a Read open against a still-open exclusive-Write
        // handle even within the same process.)
        _writer?.Dispose();
        _writer = null;
        _writeStream?.Dispose();
        _writeStream = null;

        FileStream readStream = new(
            _spillFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true);

        await using (readStream.ConfigureAwait(false))
        {
            using BinaryReader reader = new(readStream);

            // Discard the on-disk schema header — caller supplies the (possibly renamed) lookup.
            RowSerializer.ReadSchema(reader, out ColumnLookup _);

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
                while (readStream.Position < readStream.Length)
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
