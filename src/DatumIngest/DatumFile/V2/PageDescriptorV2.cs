namespace DatumIngest.DatumFile.V2;

/// <summary>
/// V2 per-page directory entry. One <see cref="PageDescriptorV2"/> per
/// (column, page) pair. Lists the absolute file offset, the page's on-disk
/// byte length, and the row count for that page. Pages are uncompressed in
/// v2 so a single byte length is sufficient — there is no separate
/// compressed/uncompressed pair.
/// </summary>
/// <param name="PageOffset">Absolute byte offset of the page within the <c>.datum</c> file.</param>
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
    long PageOffset,
    uint PageByteLength,
    ushort RowCount,
    DatumZoneMap? ZoneMap)
{
    /// <summary>
    /// Serializes this page descriptor: offset(8) + length(4) + rowCount(2)
    /// + zoneMapPresent(1) + optional zone map bytes.
    /// </summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(PageOffset);
        writer.Write(PageByteLength);
        writer.Write(RowCount);
        writer.Write(ZoneMap is not null);
        ZoneMap?.Serialize(writer);
    }

    /// <summary>Deserializes a page descriptor written by <see cref="Serialize"/>.</summary>
    internal static PageDescriptorV2 Deserialize(BinaryReader reader)
    {
        long offset = reader.ReadInt64();
        uint length = reader.ReadUInt32();
        ushort rowCount = reader.ReadUInt16();
        bool hasZoneMap = reader.ReadBoolean();
        DatumZoneMap? zoneMap = hasZoneMap ? DatumZoneMap.Deserialize(reader) : null;
        return new PageDescriptorV2(offset, length, rowCount, zoneMap);
    }
}
