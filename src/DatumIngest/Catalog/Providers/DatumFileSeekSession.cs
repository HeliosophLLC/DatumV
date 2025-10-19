using System.Buffers;
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

    private readonly Pool _pool;
    private readonly DatumFileReader _reader;
    private readonly int[] _projectedIndices;
    private readonly string[] _projectedNames;
    private readonly Dictionary<string, int> _nameIndex;
    private readonly DataValue[][] _columnBuffers;
    private readonly byte[] _compressedBuffer;
    private readonly byte[] _decompressedBuffer;
    private bool _disposed;

    internal DatumFileSeekSession(
        Pool pool,
        DatumFileReader reader,
        int[] projectedIndices,
        string[] projectedNames,
        Dictionary<string, int> nameIndex,
        DataValue[][] columnBuffers,
        byte[] compressedBuffer,
        byte[] decompressedBuffer)
    {
        _pool = pool;
        _reader = reader;
        _projectedIndices = projectedIndices;
        _projectedNames = projectedNames;
        _nameIndex = nameIndex;
        _columnBuffers = columnBuffers;
        _compressedBuffer = compressedBuffer;
        _decompressedBuffer = decompressedBuffer;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> SeekAsync(
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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

            // Decode directly into the session's owned column buffers.
            _reader.ReadColumnsInto(
                rgIndex, _projectedIndices, _columnBuffers, _compressedBuffer, _decompressedBuffer);

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

                DataValue[] values = _pool.RentDataValues(_projectedIndices.Length);
                for (int colPos = 0; colPos < _projectedIndices.Length; colPos++)
                {
                    values[colPos] = _columnBuffers[colPos][rowIndex];
                }

                batch ??= _pool.RentRowBatch(Math.Min(count, DefaultBatchSize));
                batch.Add(new Row(_projectedNames, values, _nameIndex));
                emitted++;

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }

            cumulativeRow = rgEnd;
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _reader.Dispose();

        foreach (DataValue[] buffer in _columnBuffers)
        {
            if (buffer.Length > 0)
            {
                _pool.ReturnDataValues(buffer);
            }
        }

        if (_compressedBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_compressedBuffer);
        }

        if (_decompressedBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_decompressedBuffer);
        }
    }
}
