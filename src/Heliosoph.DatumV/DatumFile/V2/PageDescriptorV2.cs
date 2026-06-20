namespace Heliosoph.DatumV.DatumFile.V2;

/// <summary>
/// Per-page directory entry. One <see cref="PageDescriptorV2"/> per
/// (column, page) pair. Lists the file id, absolute byte offset within
/// that file, on-disk byte length, and row count for the page. Pages
/// are uncompressed so a single byte length is sufficient — there is no
/// separate compressed/uncompressed pair.
/// </summary>
/// <param name="FileId">
/// Identifies which file holds the page bytes.
/// <see cref="DatumFormatV2.LocalFileId"/> (the default) means "this
/// <c>.datum</c> file"; any non-zero value indexes into the footer
/// prologue's file table for cross-file (post-compaction) references.
/// Always <c>0</c> in v4 PR1.
/// </param>
/// <param name="PageOffset">Absolute byte offset of the page within the file named by <paramref name="FileId"/>.</param>
/// <param name="PageByteLength">
/// On-disk byte length of the page (header bitmaps + payload bytes). Pages
/// are sized to fit a single batch (1024 rows by default), so this is
/// bounded by the encoder's per-row stride and never exceeds a few hundred
/// kilobytes for fixed-width / boolean encoders.
/// </param>
/// <param name="RowCount">
/// Rows in this page. <see cref="DatumFormatV2.DefaultPageSize"/> for all
/// pages except possibly the trailing page of a column.
/// </param>
/// <param name="ZoneMap">
/// Page-level zone map for this column page. <see langword="null"/> for
/// non-comparable kinds (Vector, Image, Array, Struct, byte arrays) —
/// for those kinds only chapter+volume null counts are meaningful.
/// </param>
/// <param name="HasNullBitmap">
/// Whether this page's payload is preceded by a per-row null bitmap.
/// Per-page (not per-column) so a column's pages can be heterogeneous —
/// e.g. after <c>ALTER TABLE … ALTER COLUMN c DROP NOT NULL</c>,
/// historical pages stay no-bitmap and new pages carry bitmaps. The
/// decoder reads this flag instead of the column descriptor's
/// <c>IsNullable</c>; the encoder stamps it from the column descriptor
/// at flush time. Serialized as bit 0 of the v7
/// <see cref="PageFlagsV7"/> word.
/// </param>
/// <param name="PageCrc">
/// Optional CRC32C over the page's on-disk bytes, computed at flush
/// time and verified at decode time. <see langword="null"/> when the
/// writer did not stamp a CRC for this page (the default today —
/// per-page CRCs are reserved hardware for a future "verified read"
/// mode). Serialized as a trailing <c>uint32</c> gated by
/// <see cref="PageFlagsV7.HasPageCrc"/>.
/// </param>
public sealed record PageDescriptorV2(
    ushort FileId,
    long PageOffset,
    uint PageByteLength,
    ushort RowCount,
    DatumZoneMap? ZoneMap,
    bool HasNullBitmap = false,
    uint? PageCrc = null)
{
    /// <summary>
    /// Convenience constructor for callers that always produce
    /// local-file pages (the only path used in v4 PR1). Equivalent to
    /// passing <see cref="DatumFormatV2.LocalFileId"/> as
    /// <c>FileId</c>.
    /// </summary>
    public PageDescriptorV2(long pageOffset, uint pageByteLength, ushort rowCount, DatumZoneMap? zoneMap, bool hasNullBitmap = false)
        : this(DatumFormatV2.LocalFileId, pageOffset, pageByteLength, rowCount, zoneMap, hasNullBitmap, PageCrc: null)
    {
    }

    /// <summary>
    /// Serializes this page descriptor: fileId(2) + offset(8) +
    /// length(4) + rowCount(2) + zoneMapPresent(1) + optional zone map
    /// bytes + pageFlags(2) + optional pageCrc(4 when
    /// <see cref="PageFlagsV7.HasPageCrc"/> is set).
    /// </summary>
    /// <remarks>
    /// v4-v6 wrote a single <c>bool HasNullBitmap</c> byte in place of
    /// the v7 <c>pageFlags</c> word. The format-version raise to 7 lets
    /// the reader assume the 2-byte layout unconditionally.
    /// </remarks>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(FileId);
        writer.Write(PageOffset);
        writer.Write(PageByteLength);
        writer.Write(RowCount);
        writer.Write(ZoneMap is not null);
        ZoneMap?.Serialize(writer);

        PageFlagsV7 flags = PageFlagsV7.None;
        if (HasNullBitmap) flags |= PageFlagsV7.HasNullBitmap;
        if (PageCrc.HasValue) flags |= PageFlagsV7.HasPageCrc;
        writer.Write((ushort)flags);
        if (PageCrc is { } crc) writer.Write(crc);
    }

    /// <summary>Deserializes a page descriptor written by <see cref="Serialize"/>.</summary>
    internal static PageDescriptorV2 Deserialize(BinaryReader reader)
    {
        ushort fileId = reader.ReadUInt16();
        long offset = reader.ReadInt64();
        uint length = reader.ReadUInt32();
        ushort rowCount = reader.ReadUInt16();
        bool hasZoneMap = reader.ReadBoolean();
        DatumZoneMap? zoneMap = hasZoneMap ? DatumZoneMap.Deserialize(reader) : null;
        PageFlagsV7 flags = (PageFlagsV7)reader.ReadUInt16();
        bool hasNullBitmap = (flags & PageFlagsV7.HasNullBitmap) != 0;
        uint? pageCrc = (flags & PageFlagsV7.HasPageCrc) != 0 ? reader.ReadUInt32() : null;
        return new PageDescriptorV2(fileId, offset, length, rowCount, zoneMap, hasNullBitmap, pageCrc);
    }
}
