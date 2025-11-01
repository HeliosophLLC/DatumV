using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.DatumFile;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Caller-owned seek session for a <c>.datum</c> file. Owns its own
/// <see cref="DatumFileReader"/>, decode scratch buffers, and projection metadata so
/// concurrent sessions on the same provider cannot corrupt each other's decode state.
/// </summary>
/// <remarks>
/// Buffers are sized to the largest row group / page at open time and returned to the
/// pool on <see cref="Dispose"/>. The reader is likewise opened once and disposed with
/// the session — repeated seeks reuse all state at zero per-call cost.
/// </remarks>
internal sealed class DatumFileSeekSession : ISeekSession
{
    private const int DefaultBatchSize = 1024;

    private Pool? _pool;
    private DatumFileReader? _reader;
    private ColumnBatch? _columnBatch;
    private byte[]? _compressedBuffer;
    private byte[]? _decompressedBuffer;

    internal DatumFileSeekSession(
        Pool pool,
        DatumFileReader reader,
        ColumnBatch columnBatch,
        byte[] compressedBuffer,
        byte[] decompressedBuffer)
    {
        _pool = pool;
        _reader = reader;
        _columnBatch = columnBatch;
        _compressedBuffer = compressedBuffer;
        _decompressedBuffer = decompressedBuffer;
    }

    /// <summary>
    /// Gets a value indicating whether the session has been disposed. Disposed sessions
    /// should not be used or seeked; they have already returned their buffers to the pool,
    /// so using them risks data corruption and memory safety issues.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_columnBatch))]
    [MemberNotNullWhen(false, nameof(_reader))]
    [MemberNotNullWhen(false, nameof(_pool))]
    [MemberNotNullWhen(false, nameof(_compressedBuffer))]
    [MemberNotNullWhen(false, nameof(_decompressedBuffer))]
    public bool Disposed { get; private set;}

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> SeekAsync(
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        long endRow = startRow + count;
        long cumulativeRow = 0;
        int emitted = 0;
        RowBatch? batch = null;

        for (int rgIndex = 0; rgIndex < _reader.RowGroupCount && emitted < count; rgIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DatumRowGroupDescriptor rowGroupDescriptor = _reader.GetRowGroupDescriptor(rgIndex);
            long rgRowCount = rowGroupDescriptor.RowCount;
            long rgEnd = cumulativeRow + rgRowCount;

            // Skip row groups entirely before the requested range.
            if (rgEnd <= startRow)
            {
                cumulativeRow = rgEnd;
                continue;
            }

            // Stop once we've passed the requested range.
            if (cumulativeRow >= endRow)
            {
                break;
            }

            // Rewind the column batch's arena before decoding the next row group.
            // The previously yielded batch has been consumed and its arena-backed
            // payloads stabilised into downstream output arenas by now (await-foreach
            // semantics guarantee the consumer finished before asking for the next),
            // so the arena's bytes are expendable. Without this reset the arena grew
            // monotonically with every row group — hundreds of MB over a full scan,
            // triggering repeated mmap doublings (copy + unmap) that dominated CPU
            // in filtered full-scan queries.
            _columnBatch.Arena.Reset();

            // Decode directly into the session's owned column buffers.
            _reader.ReadColumnsInto(rgIndex, _columnBatch, _compressedBuffer, _decompressedBuffer);

            int sliceStart = (int)Math.Max(startRow - cumulativeRow, 0);
            int sliceEnd = (int)Math.Min(endRow - cumulativeRow, rgRowCount);

            for (int rowIndex = sliceStart; rowIndex < sliceEnd && emitted < count; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip tombstoned rows.
                if (rowGroupDescriptor.IsRowDeleted(rowIndex))
                {
                    continue;
                }

                batch ??= _pool.RentRowBatch(_columnBatch.ColumnLookup, Math.Min(count, DefaultBatchSize), _columnBatch.Arena);

                DataValue[] values = _pool.RentDataValues(_columnBatch.ColumnLookup.Count);

                _columnBatch.CopyRow(rowIndex, values);

                batch.Add(values);
                emitted++;

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }

            if (batch is not null && batch.Count > 0)
            {
                yield return batch;
                batch = null;
            }

            cumulativeRow = rgEnd;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        _reader.Dispose();
        _reader = null;
        _pool.ReturnColumnBatch(_columnBatch);
        _pool = null;
        _columnBatch = null;
        
        if (_compressedBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_compressedBuffer);
            _compressedBuffer = null;
        }

        if (_decompressedBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_decompressedBuffer);
            _decompressedBuffer = null;
        }
        
        Disposed = true;
    }
}
