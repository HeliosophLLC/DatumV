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
/// <param name="TypeTable">
/// Per-file struct type table introduced in format v5. Maps each
/// on-disk struct type-id to a descriptor blob in the sidecar so the
/// reader can rebuild the per-query <see cref="Model.TypeRegistry"/> at
/// open. Empty list when the file has no Struct columns or was written
/// in v4 (no <see cref="DatumFileFlagsV2.HasTypeTable"/> bit).
/// </param>
public sealed record FooterV2(
    FooterPrologueV4 Prologue,
    IReadOnlyList<ColumnFooterV2> Columns,
    bool HasVolumeZoneMaps,
    IReadOnlyList<TypeTableEntryV5> TypeTable)
{
    /// <summary>
    /// Backwards-compatible constructor without a type table (empty list).
    /// Existing v4 callers that don't carry struct types pass through this
    /// overload without changes.
    /// </summary>
    public FooterV2(
        FooterPrologueV4 prologue,
        IReadOnlyList<ColumnFooterV2> columns,
        bool hasVolumeZoneMaps)
        : this(prologue, columns, hasVolumeZoneMaps, Array.Empty<TypeTableEntryV5>())
    { }

    /// <summary>
    /// Serializes the footer body. The footer's byte length is captured
    /// by the caller (it goes in the tail), so the serializer here
    /// writes only the prologue + column blocks + type table — no
    /// trailing length sentinel of its own.
    /// </summary>
    /// <param name="writer">Footer writer.</param>
    /// <param name="hasTypeTable">
    /// Mirrors <see cref="DatumFileFlagsV2.HasTypeTable"/>. When false
    /// the type-table block is omitted entirely so v4 files stay
    /// byte-identical to their pre-v5 layout.
    /// </param>
    internal void Serialize(BinaryWriter writer, bool hasTypeTable)
    {
        Prologue.Serialize(writer);
        foreach (ColumnFooterV2 column in Columns)
        {
            column.Serialize(writer, HasVolumeZoneMaps);
        }
        if (hasTypeTable)
        {
            writer.Write(TypeTable.Count);
            foreach (TypeTableEntryV5 entry in TypeTable)
            {
                entry.Serialize(writer);
            }
        }
    }

    /// <summary>
    /// Deserializes the footer body. The prologue's <c>columnCount</c>
    /// drives the per-column block loop — the header's
    /// <c>ColumnCount</c> is informational only in v4 and may lag the
    /// authoritative footer value during a column-add commit window.
    /// </summary>
    /// <param name="reader">Footer reader.</param>
    /// <param name="hasVolumeZoneMaps">Mirror of the file-level flag.</param>
    /// <param name="hasTypeTable">
    /// Mirror of <see cref="DatumFileFlagsV2.HasTypeTable"/>. Controls
    /// whether the trailing type-table block is read; v4 files always
    /// pass <see langword="false"/> here.
    /// </param>
    internal static FooterV2 Deserialize(BinaryReader reader, bool hasVolumeZoneMaps, bool hasTypeTable)
    {
        FooterPrologueV4 prologue = FooterPrologueV4.Deserialize(reader);
        ColumnFooterV2[] columns = new ColumnFooterV2[prologue.ColumnCount];
        for (int i = 0; i < prologue.ColumnCount; i++)
        {
            columns[i] = ColumnFooterV2.Deserialize(reader, hasVolumeZoneMaps);
        }

        IReadOnlyList<TypeTableEntryV5> typeTable;
        if (hasTypeTable)
        {
            int count = reader.ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException(
                    $"Footer declares negative type-table entry count ({count}).");
            }
            TypeTableEntryV5[] entries = new TypeTableEntryV5[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = TypeTableEntryV5.Deserialize(reader);
            }
            typeTable = entries;
        }
        else
        {
            typeTable = Array.Empty<TypeTableEntryV5>();
        }

        return new FooterV2(prologue, columns, hasVolumeZoneMaps, typeTable);
    }
}

/// <summary>
/// One entry in the file footer's type table (v5+). Maps an on-disk
/// struct type-id to the byte range in the sidecar holding the
/// recursive <see cref="Model.TypeDescriptor"/> blob produced by
/// <see cref="TypeDescriptorSerializer"/>.
/// </summary>
/// <param name="OnDiskTypeId">
/// Stable per-file type-id. Allocated by the writer in emission order
/// (1..N); referenced from <see cref="ColumnFooterV2.StructTypeId"/> and
/// from per-element TypeId bytes in <c>Array&lt;Struct&gt;</c> slot blocks.
/// </param>
/// <param name="SidecarOffset">Absolute byte offset of the descriptor blob in <c>.datum-blob</c>.</param>
/// <param name="DescriptorLength">Length of the descriptor blob in bytes.</param>
public sealed record TypeTableEntryV5(
    ushort OnDiskTypeId,
    long SidecarOffset,
    int DescriptorLength)
{
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(OnDiskTypeId);
        writer.Write(SidecarOffset);
        writer.Write(DescriptorLength);
    }

    internal static TypeTableEntryV5 Deserialize(BinaryReader reader)
    {
        ushort onDiskTypeId = reader.ReadUInt16();
        long offset = reader.ReadInt64();
        int length = reader.ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException(
                $"TypeTableEntryV5 declares negative descriptor length ({length}).");
        }
        return new TypeTableEntryV5(onDiskTypeId, offset, length);
    }
}
