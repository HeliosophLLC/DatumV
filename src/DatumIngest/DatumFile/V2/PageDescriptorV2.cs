namespace DatumIngest.DatumFile.V2;

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
public sealed record PageDescriptorV2(
    ushort FileId,
    long PageOffset,
    uint PageByteLength,
    ushort RowCount,
    DatumZoneMap? ZoneMap)
{
    /// <summary>
    /// Convenience constructor for callers that always produce
    /// local-file pages (the only path used in v4 PR1). Equivalent to
    /// passing <see cref="DatumFormatV2.LocalFileId"/> as
    /// <c>FileId</c>.
    /// </summary>
    public PageDescriptorV2(long pageOffset, uint pageByteLength, ushort rowCount, DatumZoneMap? zoneMap)
        : this(DatumFormatV2.LocalFileId, pageOffset, pageByteLength, rowCount, zoneMap)
    {
    }

    /// <summary>
    /// Serializes this page descriptor: fileId(2) + offset(8) +
    /// length(4) + rowCount(2) + zoneMapPresent(1) + optional zone map
    /// bytes.
    /// </summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(FileId);
        writer.Write(PageOffset);
        writer.Write(PageByteLength);
        writer.Write(RowCount);
        writer.Write(ZoneMap is not null);
        ZoneMap?.Serialize(writer);
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
        return new PageDescriptorV2(fileId, offset, length, rowCount, zoneMap);
    }
}
