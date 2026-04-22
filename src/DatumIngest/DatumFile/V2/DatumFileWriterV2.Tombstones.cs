using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2.Encoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2;

public sealed partial class DatumFileWriterV2
{
    /// <summary>
    /// Marks the row at logical index <paramref name="rowIndex"/> as
    /// soft-deleted. The data bytes stay on disk (page directory, zone
    /// maps, sidecar refs all unchanged) but readers skip the row at
    /// materialization time. Idempotent — marking an already-deleted
    /// row is a no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mutation is buffered in memory; <see cref="FinalizeWriter"/>
    /// writes a fresh tombstone bitmap block per affected chapter at a
    /// new file offset (copy-on-write) and updates the footer's
    /// <see cref="FooterPrologueV4.ChapterTombstoneOffsets"/> to point
    /// at it. Old blocks become orphan bytes (reachable only from
    /// pre-commit footers — i.e., reader snapshots opened before this
    /// commit).
    /// </para>
    /// <para>
    /// Only valid in append mode (writer opened via
    /// <see cref="OpenForAppend"/>). Throws on out-of-range
    /// <paramref name="rowIndex"/>.
    /// </para>
    /// </remarks>
    public void MarkRowDeleted(long rowIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException(
            "MarkRowDeleted requires an initialized writer; open the file with OpenForAppend.");
        if (_finalized) throw new InvalidOperationException("Writer is finalized.");
        if (rowIndex < 0 || rowIndex >= _totalRowsWritten)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowIndex), rowIndex,
                $"Row index must be in [0, {_totalRowsWritten}).");
        }

        int chapterRowSpan = ChapterTombstoneBlock.MaxRowsPerChapter;
        int chapterIndex = (int)(rowIndex / chapterRowSpan);
        int rowInChapter = (int)(rowIndex - (long)chapterIndex * chapterRowSpan);

        ChapterTombstoneBlock block = GetOrLoadPendingBlock(chapterIndex);
        block.MarkDeleted(rowInChapter);
    }

    /// <summary>
    /// Marks <paramref name="count"/> consecutive rows starting at
    /// <paramref name="startRow"/> as soft-deleted. Equivalent to
    /// looping <see cref="MarkRowDeleted"/> but skips repeated chapter
    /// resolution for ranges that span only a few chapters. Idempotent.
    /// </summary>
    public void MarkRowsDeleted(long startRow, long count)
    {
        if (count == 0) return;
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be non-negative.");
        long endExclusive = checked(startRow + count);
        if (startRow < 0 || endExclusive > _totalRowsWritten)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"[{startRow}..{endExclusive}) out of range; file has {_totalRowsWritten} rows.");
        }

        int chapterRowSpan = ChapterTombstoneBlock.MaxRowsPerChapter;
        long current = startRow;
        while (current < endExclusive)
        {
            int chapterIndex = (int)(current / chapterRowSpan);
            long chapterStart = (long)chapterIndex * chapterRowSpan;
            long chapterEnd = Math.Min(chapterStart + chapterRowSpan, endExclusive);
            ChapterTombstoneBlock block = GetOrLoadPendingBlock(chapterIndex);
            for (long r = current; r < chapterEnd; r++)
            {
                block.MarkDeleted((int)(r - chapterStart));
            }
            current = chapterEnd;
        }
    }

    /// <summary>
    /// Lazy-loads the pending tombstone block for
    /// <paramref name="chapterIndex"/>. If the file already has a
    /// committed block for this chapter, the bytes are read in so
    /// further <see cref="MarkRowDeleted"/> calls accumulate on top.
    /// Otherwise a fresh all-zeros block is created.
    /// </summary>
    private ChapterTombstoneBlock GetOrLoadPendingBlock(int chapterIndex)
    {
        _pendingTombstoneEdits ??= new Dictionary<int, ChapterTombstoneBlock>();
        if (_pendingTombstoneEdits.TryGetValue(chapterIndex, out ChapterTombstoneBlock? cached))
        {
            return cached;
        }

        ChapterTombstoneBlock block;
        if (_existingTombstoneOffsets is not null
            && chapterIndex < _existingTombstoneOffsets.Length
            && _existingTombstoneOffsets[chapterIndex] != DatumFormatV2.NoTombstoneBlock)
        {
            // Read existing committed block; new edits OR'd on top.
            byte[] bytes = new byte[DatumFormatV2.ChapterTombstoneBlockBytes];
            long savedPosition = _stream.Position;
            try
            {
                _stream.Position = _existingTombstoneOffsets[chapterIndex];
                _stream.ReadExactly(bytes);
            }
            finally
            {
                _stream.Position = savedPosition;
            }
            block = new ChapterTombstoneBlock(bytes);
        }
        else
        {
            block = new ChapterTombstoneBlock();
        }

        _pendingTombstoneEdits[chapterIndex] = block;
        return block;
    }

    /// <summary>
    /// One-shot helper that opens <paramref name="datumPath"/>, marks
    /// the given row indices as soft-deleted, and commits via tail
    /// flip. Generation bumps by one regardless of how many rows are
    /// affected.
    /// </summary>
    public static void SoftDeleteRows(string datumPath, IReadOnlyList<long> rowIndices)
    {
        ArgumentNullException.ThrowIfNull(datumPath);
        ArgumentNullException.ThrowIfNull(rowIndices);
        if (rowIndices.Count == 0) return;

        string? sidecarPath = ResolveSidecarPathIfNeeded(datumPath);

        using DatumFileWriterV2 writer = OpenForAppend(datumPath, sidecarPath);
        foreach (long rowIndex in rowIndices)
        {
            writer.MarkRowDeleted(rowIndex);
        }
        writer.FinalizeWriter();
    }
}
