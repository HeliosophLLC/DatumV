using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.DatumFile.V2.Decoding;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Catalog.Providers;

/// <summary>
/// Caller-owned seek session for a v2 <c>.datum</c> file. Owns its own
/// <see cref="DatumFileReaderV2"/> and <see cref="SidecarReadStore"/> so
/// concurrent sessions on the same provider don't contend for
/// <see cref="FileStream.Position"/>; reused projection metadata and
/// per-column schema-index lookups skip the per-call resolution work.
/// </summary>
/// <remarks>
/// v2's fixed-size pages make seeks O(1) row → page math:
/// <c>pageIndex = startRow / pageSize</c>, <c>rowInPage = startRow % pageSize</c>.
/// No row-group iteration, no descriptor scan. The session yields one
/// <see cref="RowBatch"/> per page touched by <c>[startRow, startRow + count)</c>;
/// each batch's arena absorbs eagerly-materialized children (Struct field
/// arrays) for that page's decoders.
/// </remarks>
internal sealed class DatumFileSeekSessionV2 : ISeekSession
{
    private const int DefaultBatchSize = 1024;

    private readonly Pool _pool;
    private readonly ColumnLookup _columnLookup;
    private readonly int[] _schemaIndices;
    private readonly byte _sidecarStoreId;
    private readonly Arena? _targetArena;
    private readonly byte[]?[]? _chapterTombstoneBitmaps;
    private DatumFileReaderV2? _reader;
    private SidecarReadStore? _sidecar;
    private bool _disposed;

    internal DatumFileSeekSessionV2(
        Pool pool,
        DatumFileReaderV2 reader,
        SidecarReadStore? sidecar,
        ColumnLookup columnLookup,
        int[] schemaIndices,
        byte sidecarStoreId,
        Arena? targetArena = null)
    {
        _pool = pool;
        _reader = reader;
        _sidecar = sidecar;
        _columnLookup = columnLookup;
        _schemaIndices = schemaIndices;
        _sidecarStoreId = sidecarStoreId;
        _targetArena = targetArena;
        // Load tombstone bitmaps once at session construction so the
        // inner row-iteration loop fast-paths when the file has no
        // soft-deletes (the common case).
        _chapterTombstoneBitmaps = reader.LoadChapterTombstoneBitmaps();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> SeekAsync(
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0 || _reader is null) yield break;

        long fileRowCount = _reader.TotalRowCount;
        if (startRow >= fileRowCount) yield break;

        long endRow = Math.Min(startRow + count, fileRowCount);
        int pageSize = _reader.Header.PageSize;
        int pageIndex = (int)(startRow / pageSize);

        // Probe column 0 (or first projected column) for per-page row counts.
        // All columns share the page count; the writer flushes every encoder
        // at the same row cadence.
        var pages = _reader.Footer.Columns[_schemaIndices[0]].Pages;
        IPageDecoderV2[] decoders = new IPageDecoderV2[_schemaIndices.Length];

        long currentRow = startRow;

        while (currentRow < endRow && pageIndex < pages.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int pageRowCount = pages[pageIndex].RowCount;
            long pageStartAbs = (long)pageIndex * pageSize;
            int sliceStart = (int)(currentRow - pageStartAbs);
            int sliceEnd = (int)Math.Min(endRow - pageStartAbs, pageRowCount);
            int sliceCount = sliceEnd - sliceStart;

            if (sliceCount <= 0)
            {
                pageIndex++;
                continue;
            }

            // One batch per page. batch.Arena is the eager store for the
            // decoders so Struct field arrays land somewhere with a
            // batch-bounded lifetime.
            int batchCapacity = Math.Min(sliceCount, DefaultBatchSize);
            RowBatch batch = _pool.RentRowBatch(_columnLookup, batchCapacity, _targetArena);

            for (int i = 0; i < _schemaIndices.Length; i++)
            {
                decoders[i] = _reader.OpenPageDecoder(
                    columnIndex: _schemaIndices[i],
                    pageIndex: pageIndex,
                    sidecarStoreId: _sidecarStoreId,
                    sidecarSource: _sidecar,
                    eagerStore: batch.Arena);
            }

            for (int row = sliceStart; row < sliceEnd; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip soft-deleted rows. currentRow advances regardless
                // (it tracks logical row positions, not yielded rows) so
                // the seek's start/count semantics still address the
                // pre-delete row range.
                if (IsRowDeleted(pageIndex, row, pageSize))
                {
                    currentRow++;
                    continue;
                }

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = _pool.RentRowBatch(_columnLookup, Math.Min(sliceEnd - row, DefaultBatchSize), _targetArena);
                    // Re-open decoders bound to the new batch's arena so
                    // Struct/Array materializations land in the right store.
                    for (int i = 0; i < _schemaIndices.Length; i++)
                    {
                        decoders[i] = _reader.OpenPageDecoder(
                            columnIndex: _schemaIndices[i],
                            pageIndex: pageIndex,
                            sidecarStoreId: _sidecarStoreId,
                            sidecarSource: _sidecar,
                            eagerStore: batch.Arena);
                    }
                }

                DataValue[] values = _pool.RentDataValues(_schemaIndices.Length);
                for (int i = 0; i < _schemaIndices.Length; i++)
                {
                    values[i] = decoders[i].ReadValue(row);
                }
                batch.Add(values);
                currentRow++;
            }

            // Yield only non-empty batches — when every row in this
            // page is tombstoned the batch is empty and consumers
            // would otherwise misinterpret it as end-of-stream.
            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                _pool.ReturnRowBatch(batch);
            }
            pageIndex++;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the row at
    /// <c>(pageIndex, rowInPage)</c> has been soft-deleted — its bit
    /// is set in this session's chapter tombstone bitmap. Fast-paths
    /// when the file has no tombstones at all
    /// (<see cref="_chapterTombstoneBitmaps"/> is <see langword="null"/>)
    /// or no tombstones in the row's chapter.
    /// </summary>
    private bool IsRowDeleted(int pageIndex, int rowInPage, int pageSize)
    {
        if (_chapterTombstoneBitmaps is null) return false;

        int chapterIndex = pageIndex / DatumFormatV2.PagesPerChapter;
        if (chapterIndex >= _chapterTombstoneBitmaps.Length) return false;

        byte[]? bitmap = _chapterTombstoneBitmaps[chapterIndex];
        if (bitmap is null) return false;

        int pageOffsetInChapter = pageIndex % DatumFormatV2.PagesPerChapter;
        int rowInChapter = pageOffsetInChapter * pageSize + rowInPage;
        if ((uint)rowInChapter >= (uint)(bitmap.Length * 8)) return false;

        int byteIndex = rowInChapter >> 3;
        int bitMask = 1 << (rowInChapter & 7);
        return (bitmap[byteIndex] & bitMask) != 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sidecar?.Dispose();
        _sidecar = null;
        _reader?.Dispose();
        _reader = null;
    }
}
