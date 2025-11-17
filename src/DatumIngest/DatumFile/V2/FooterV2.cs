namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Top-level V2 file footer. Aggregates one <see cref="ColumnFooterV2"/>
/// per column (descriptor + page directory + zone-map hierarchy). Written
/// at the position recorded in the file header's <c>footerOffset</c> field
/// and immediately followed by the file tail
/// (footerByteLength + <see cref="DatumFormatV2.TailMagic"/>).
/// </summary>
/// <param name="Columns">
/// Column footers in schema order. The column count matches the
/// <c>columnCount</c> field in the file header.
/// </param>
/// <param name="HasVolumeZoneMaps">
/// Whether volume-level zone maps were emitted. Mirrors
/// <see cref="DatumFileFlagsV2.HasVolumeZoneMaps"/>; carried on the footer
/// so the deserializer knows whether to read the volume block per column
/// without consulting the header.
/// </param>
public sealed record FooterV2(
    IReadOnlyList<ColumnFooterV2> Columns,
    bool HasVolumeZoneMaps)
{
    /// <summary>
    /// Serializes the footer body. The footer's byte length is captured by
    /// the caller (it goes in the tail), so the serializer here writes only
    /// the column blocks — no trailing length sentinel of its own.
    /// </summary>
    internal void Serialize(BinaryWriter writer)
    {
        // columnCount lives in the header; we don't repeat it here.
        // The reader uses the header's columnCount to drive the loop.
        foreach (ColumnFooterV2 column in Columns)
        {
            column.Serialize(writer, HasVolumeZoneMaps);
        }
    }

    /// <summary>
    /// Deserializes the footer body for a file whose header declared
    /// <paramref name="columnCount"/> columns and the given volume-zone-map
    /// flag.
    /// </summary>
    internal static FooterV2 Deserialize(
        BinaryReader reader,
        int columnCount,
        bool hasVolumeZoneMaps)
    {
        ColumnFooterV2[] columns = new ColumnFooterV2[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            columns[i] = ColumnFooterV2.Deserialize(reader, hasVolumeZoneMaps);
        }
        return new FooterV2(columns, hasVolumeZoneMaps);
    }
}
