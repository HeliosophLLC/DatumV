namespace DatumIngest.DatumFile;

/// <summary>
/// Location and metadata of a single column page within a <c>.datum</c> file.
/// Stored in the per-row-group directory in the footer, one entry per column.
/// </summary>
public readonly record struct DatumColumnChunkDescriptor
{
    /// <summary>
    /// Creates a column chunk descriptor.
    /// </summary>
    /// <param name="pageOffset">Absolute byte offset of the page header in the file.</param>
    /// <param name="compressedByteLength">Number of compressed bytes in the page payload (excluding header).</param>
    /// <param name="uncompressedByteLength">Number of bytes after decompression.</param>
    /// <param name="encoding">Encoding applied to the column data before compression.</param>
    /// <param name="compression">Compression algorithm applied to the encoded bytes.</param>
    /// <param name="zoneMap">Zone map statistics for this column within this row group.</param>
    public DatumColumnChunkDescriptor(
        long pageOffset,
        uint compressedByteLength,
        uint uncompressedByteLength,
        DatumEncoding encoding,
        DatumCompression compression,
        DatumZoneMap zoneMap)
    {
        PageOffset = pageOffset;
        CompressedByteLength = compressedByteLength;
        UncompressedByteLength = uncompressedByteLength;
        Encoding = encoding;
        Compression = compression;
        ZoneMap = zoneMap;
    }

    /// <summary>Absolute byte offset of the page header in the file.</summary>
    public long PageOffset { get; init; }

    /// <summary>Number of compressed bytes in the page payload (excluding the 14-byte page header).</summary>
    public uint CompressedByteLength { get; init; }

    /// <summary>Number of bytes after decompression. Used to pre-allocate decode buffers.</summary>
    public uint UncompressedByteLength { get; init; }

    /// <summary>Encoding applied to the column data before compression.</summary>
    public DatumEncoding Encoding { get; init; }

    /// <summary>Compression algorithm applied to the encoded bytes.</summary>
    public DatumCompression Compression { get; init; }

    /// <summary>Zone map statistics (min, max, null count) for this column within this row group.</summary>
    public DatumZoneMap ZoneMap { get; init; }

    // ──────────────────── Binary serialization ────────────────────

    /// <summary>Serializes this descriptor to the binary writer.</summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(PageOffset);
        writer.Write(CompressedByteLength);
        writer.Write(UncompressedByteLength);
        writer.Write((byte)Encoding);
        writer.Write((byte)Compression);
        ZoneMap.Serialize(writer);
    }

    /// <summary>Deserializes a column chunk descriptor from the binary reader.</summary>
    internal static DatumColumnChunkDescriptor Deserialize(BinaryReader reader)
    {
        long pageOffset = reader.ReadInt64();
        uint compressedLength = reader.ReadUInt32();
        uint uncompressedLength = reader.ReadUInt32();
        DatumEncoding encoding = (DatumEncoding)reader.ReadByte();
        DatumCompression compression = (DatumCompression)reader.ReadByte();
        DatumZoneMap zoneMap = DatumZoneMap.Deserialize(reader);

        return new DatumColumnChunkDescriptor(
            pageOffset, compressedLength, uncompressedLength, encoding, compression, zoneMap);
    }
}
