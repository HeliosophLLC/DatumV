namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Top-level file footer. Carries the v4 prologue (commit lineage,
/// file table, chapter tombstone offsets) followed by one
/// <see cref="ColumnFooterV2"/> per column (descriptor + page directory
/// + zone-map hierarchy). Written at the position recorded in the file
/// header's <c>footerOffset</c> field and immediately followed by the
/// file tail (footerByteLength + <see cref="DatumFormatV2.TailMagic"/>).
/// </summary>
/// <param name="Prologue">
/// File-level metadata: generation, writerId, baseGeneration, tombstone
/// granularity, authoritative column count, file table, chapter
/// tombstone offset table.
/// </param>
/// <param name="Columns">
/// Column footers in schema order. <see cref="FooterPrologueV4.ColumnCount"/>
/// is authoritative for the count; the prologue's value drives the
/// per-column block loop on read.
/// </param>
/// <param name="HasVolumeZoneMaps">
/// Whether volume-level zone maps were emitted. Mirrors
/// <see cref="DatumFileFlagsV2.HasVolumeZoneMaps"/>; carried on the footer
/// so the deserializer knows whether to read the volume block per column
/// without consulting the header.
/// </param>
public sealed record FooterV2(
    FooterPrologueV4 Prologue,
    IReadOnlyList<ColumnFooterV2> Columns,
    bool HasVolumeZoneMaps)
{
    /// <summary>
    /// Serializes the footer body. The footer's byte length is captured
    /// by the caller (it goes in the tail), so the serializer here
    /// writes only the prologue + column blocks — no trailing length
    /// sentinel of its own.
    /// </summary>
    internal void Serialize(BinaryWriter writer)
    {
        Prologue.Serialize(writer);
        foreach (ColumnFooterV2 column in Columns)
        {
            column.Serialize(writer, HasVolumeZoneMaps);
        }
    }

    /// <summary>
    /// Deserializes the footer body. The prologue's <c>columnCount</c>
    /// drives the per-column block loop — the header's
    /// <c>ColumnCount</c> is informational only in v4 and may lag the
    /// authoritative footer value during a column-add commit window.
    /// </summary>
    internal static FooterV2 Deserialize(BinaryReader reader, bool hasVolumeZoneMaps)
    {
        FooterPrologueV4 prologue = FooterPrologueV4.Deserialize(reader);
        ColumnFooterV2[] columns = new ColumnFooterV2[prologue.ColumnCount];
        for (int i = 0; i < prologue.ColumnCount; i++)
        {
            columns[i] = ColumnFooterV2.Deserialize(reader, hasVolumeZoneMaps);
        }
        return new FooterV2(prologue, columns, hasVolumeZoneMaps);
    }
}
