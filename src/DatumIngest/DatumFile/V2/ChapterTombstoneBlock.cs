namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Mutable in-memory view of a chapter's 8 KiB tombstone bitmap. One
/// bit per logical row in the chapter range
/// (<see cref="DatumFormatV2.PagesPerChapter"/> × pageSize = 65536 rows
/// at the default page size). A set bit means the corresponding row
/// has been soft-deleted; readers union the bitmap into their
/// materialization mask alongside per-page null bitmaps.
/// </summary>
/// <remarks>
/// <para>
/// The block is exactly <see cref="DatumFormatV2.ChapterTombstoneBlockBytes"/>
/// (8 KiB) regardless of how many rows the chapter actually contains.
/// Trailing chapters with fewer than 65536 rows leave the unused
/// trailing bits zero; readers ignore them by clamping to the page's
/// row count.
/// </para>
/// <para>
/// Each soft-delete commit writes a fresh block at a new file offset
/// (copy-on-write). The footer prologue's
/// <see cref="FooterPrologueV4.ChapterTombstoneOffsets"/> is updated
/// to point at the new block, and old blocks become orphan bytes that
/// only get reclaimed at compaction time. This is cheap (8 KiB per
/// affected chapter per commit) and gives in-flight readers stable
/// snapshots — they keep referencing whatever block their open-time
/// footer pointed at.
/// </para>
/// </remarks>
internal sealed class ChapterTombstoneBlock
{
    private readonly byte[] _bytes;

    /// <summary>
    /// Maximum logical row offset addressable by this bitmap. Rows
    /// beyond this index aren't representable — callers must split
    /// across chapters before calling <see cref="MarkDeleted"/>.
    /// </summary>
    public const int MaxRowsPerChapter =
        DatumFormatV2.PagesPerChapter * DatumFormatV2.DefaultPageSize;

    /// <summary>Creates an all-zeros block (no tombstones).</summary>
    public ChapterTombstoneBlock()
    {
        _bytes = new byte[DatumFormatV2.ChapterTombstoneBlockBytes];
    }

    /// <summary>
    /// Creates a block from existing bytes. <paramref name="bytes"/>
    /// must be exactly <see cref="DatumFormatV2.ChapterTombstoneBlockBytes"/>
    /// long; the constructor copies so subsequent mutations don't
    /// alias the source span.
    /// </summary>
    public ChapterTombstoneBlock(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != DatumFormatV2.ChapterTombstoneBlockBytes)
        {
            throw new ArgumentException(
                $"Tombstone block must be exactly {DatumFormatV2.ChapterTombstoneBlockBytes} bytes " +
                $"(got {bytes.Length}).",
                nameof(bytes));
        }
        _bytes = bytes.ToArray();
    }

    /// <summary>
    /// Whether the row at <paramref name="rowInChapter"/> is marked as
    /// soft-deleted. Out-of-range indices return <see langword="false"/>
    /// (readers stop iterating before exceeding the chapter's actual
    /// row count, but defensive bounds clamp here too).
    /// </summary>
    public bool IsDeleted(int rowInChapter)
    {
        if ((uint)rowInChapter >= (uint)MaxRowsPerChapter) return false;
        int byteIndex = rowInChapter >> 3;
        int bitMask = 1 << (rowInChapter & 7);
        return (_bytes[byteIndex] & bitMask) != 0;
    }

    /// <summary>
    /// Marks the row at <paramref name="rowInChapter"/> as soft-deleted.
    /// Idempotent — setting an already-set bit is a no-op. Throws if
    /// the index is out of range.
    /// </summary>
    public void MarkDeleted(int rowInChapter)
    {
        if ((uint)rowInChapter >= (uint)MaxRowsPerChapter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowInChapter), rowInChapter,
                $"Row index within chapter must be in [0, {MaxRowsPerChapter}).");
        }
        int byteIndex = rowInChapter >> 3;
        int bitMask = 1 << (rowInChapter & 7);
        _bytes[byteIndex] |= (byte)bitMask;
    }

    /// <summary>
    /// True if any bit is set. The writer uses this to skip emitting
    /// the block when a chapter ends up with all-zero tombstones (the
    /// caller marked rows then unmarked them, or the operation
    /// targeted no rows in this chapter).
    /// </summary>
    public bool HasAnyDeletes()
    {
        for (int i = 0; i < _bytes.Length; i++)
        {
            if (_bytes[i] != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a read-only view of the block bytes for serialization.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => _bytes;
}
