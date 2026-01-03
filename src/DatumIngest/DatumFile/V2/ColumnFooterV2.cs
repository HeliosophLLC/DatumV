using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Per-column footer block: descriptor, page directory, and the zone-map
/// hierarchy (page → chapter → volume) used by the scanner for predicate
/// pruning. One entry per column, written in schema order in the file
/// footer.
/// </summary>
/// <param name="Descriptor">Schema-level metadata for this column.</param>
/// <param name="Pages">
/// One entry per column page in row order. Each carries the page's file
/// offset, byte length, row count, and an optional per-page zone map (null
/// for non-comparable kinds).
/// </param>
/// <param name="ChapterZoneMaps">
/// Aggregated zone maps over <see cref="DatumFormatV2.PagesPerChapter"/>
/// pages each. Length = ceil(<paramref name="Pages"/>.Count /
/// <see cref="DatumFormatV2.PagesPerChapter"/>). Always present (even when
/// only one chapter exists) so the predicate evaluator has a uniform
/// pruning path.
/// </param>
/// <param name="VolumeZoneMaps">
/// Aggregated zone maps over <see cref="DatumFormatV2.ChaptersPerVolume"/>
/// chapters each. <see langword="null"/> when the file has fewer rows than
/// <see cref="DatumFormatV2.VolumeEmitRowThreshold"/> — at that scale the
/// chapter level is sufficient and emitting volume maps just bloats the
/// footer.
/// </param>
public sealed record ColumnFooterV2(
    ColumnDescriptorV2 Descriptor,
    IReadOnlyList<PageDescriptorV2> Pages,
    IReadOnlyList<DatumZoneMap> ChapterZoneMaps,
    IReadOnlyList<DatumZoneMap>? VolumeZoneMaps)
{
    /// <summary>
    /// Serializes this column footer block. Layout:
    /// <list type="bullet">
    /// <item>name (string), kind (1B), encoder (1B), flags (1B = isNullable | isArray | hasFixedShape),
    ///       fixedShape rank+dims (when present)</item>
    /// <item>pageCount (4B), pages[]</item>
    /// <item>chapterZoneMapCount (4B), chapterZoneMaps[]</item>
    /// <item>(when <paramref name="hasVolumeZoneMaps"/>) volumeZoneMapCount (4B), volumeZoneMaps[]</item>
    /// </list>
    /// </summary>
    /// <param name="writer">Footer writer.</param>
    /// <param name="hasVolumeZoneMaps">
    /// Whether the file emits volume-level zone maps. Mirrors
    /// <see cref="DatumFileFlagsV2.HasVolumeZoneMaps"/>; passed in (rather
    /// than read from the column) so all columns serialize consistently.
    /// </param>
    internal void Serialize(BinaryWriter writer, bool hasVolumeZoneMaps)
    {
        writer.Write(Descriptor.Name);
        writer.Write((byte)Descriptor.Kind);
        writer.Write((byte)Descriptor.Encoder);

        ColumnFlagsV2 flags = ColumnFlagsV2.None;
        if (Descriptor.IsNullable) flags |= ColumnFlagsV2.Nullable;
        if (Descriptor.IsArray) flags |= ColumnFlagsV2.IsArray;
        if (Descriptor.FixedShape is not null) flags |= ColumnFlagsV2.HasFixedShape;
        writer.Write((byte)flags);

        if (Descriptor.FixedShape is not null)
        {
            writer.Write((ushort)Descriptor.FixedShape.Length);
            foreach (int dim in Descriptor.FixedShape)
            {
                writer.Write(dim);
            }
        }

        writer.Write(Pages.Count);
        foreach (PageDescriptorV2 page in Pages)
        {
            page.Serialize(writer);
        }

        writer.Write(ChapterZoneMaps.Count);
        foreach (DatumZoneMap chapter in ChapterZoneMaps)
        {
            chapter.Serialize(writer);
        }

        if (hasVolumeZoneMaps)
        {
            int volumeCount = VolumeZoneMaps?.Count ?? 0;
            writer.Write(volumeCount);
            if (VolumeZoneMaps is not null)
            {
                foreach (DatumZoneMap volume in VolumeZoneMaps)
                {
                    volume.Serialize(writer);
                }
            }
        }
    }

    /// <summary>Deserializes a column footer block written by <see cref="Serialize"/>.</summary>
    internal static ColumnFooterV2 Deserialize(BinaryReader reader, bool hasVolumeZoneMaps)
    {
        string name = reader.ReadString();
        DataKind kind = (DataKind)reader.ReadByte();
        EncoderKind encoder = (EncoderKind)reader.ReadByte();
        ColumnFlagsV2 flags = (ColumnFlagsV2)reader.ReadByte();

        int[]? fixedShape = null;
        if ((flags & ColumnFlagsV2.HasFixedShape) != 0)
        {
            ushort rank = reader.ReadUInt16();
            fixedShape = new int[rank];
            for (int i = 0; i < rank; i++)
            {
                fixedShape[i] = reader.ReadInt32();
            }
        }

        ColumnDescriptorV2 descriptor = new(
            name,
            kind,
            encoder,
            (flags & ColumnFlagsV2.Nullable) != 0,
            (flags & ColumnFlagsV2.IsArray) != 0,
            fixedShape);

        int pageCount = reader.ReadInt32();
        PageDescriptorV2[] pages = new PageDescriptorV2[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            pages[i] = PageDescriptorV2.Deserialize(reader);
        }

        int chapterCount = reader.ReadInt32();
        DatumZoneMap[] chapters = new DatumZoneMap[chapterCount];
        for (int i = 0; i < chapterCount; i++)
        {
            chapters[i] = DatumZoneMap.Deserialize(reader);
        }

        DatumZoneMap[]? volumes = null;
        if (hasVolumeZoneMaps)
        {
            int volumeCount = reader.ReadInt32();
            volumes = new DatumZoneMap[volumeCount];
            for (int i = 0; i < volumeCount; i++)
            {
                volumes[i] = DatumZoneMap.Deserialize(reader);
            }
        }

        return new ColumnFooterV2(descriptor, pages, chapters, volumes);
    }
}

/// <summary>
/// Per-column flag byte serialized into the footer. Captures the small
/// subset of column-level state that needs round-tripping past file
/// open (nullability, typed-array flag, fixed-shape presence,
/// tombstone bit). Reader ignores unknown bits rather than failing —
/// additive flag-bit allocation is a forward-compatible operation.
/// </summary>
[Flags]
internal enum ColumnFlagsV2 : byte
{
    None = 0,
    Nullable = 0x01,
    IsArray = 0x02,
    HasFixedShape = 0x04,

    /// <summary>
    /// Column has been soft-dropped via <c>ALTER TABLE DROP COLUMN</c>.
    /// The column block (descriptor + page directory + zone maps)
    /// remains in the footer for compaction-time reclamation, but the
    /// reader skips the column at schema enumeration. Inert in PR1 —
    /// the soft-drop API ships in PR4.
    /// </summary>
    Tombstoned = 0x08,
}
