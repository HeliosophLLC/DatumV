using System.Buffers.Binary;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Fixed-size V2 file header. Sits at byte 0 of every <c>.datum</c> file
/// and records the format version, file-level flags, schema width, page
/// size, total row count, and footer offset. <see cref="TotalRowCount"/>
/// and <see cref="FooterOffset"/> are placeholder zeros during streaming
/// writes and patched on finalize at
/// <see cref="DatumFormatV2.TotalRowCountFieldOffset"/> and
/// <see cref="DatumFormatV2.FooterOffsetFieldOffset"/>.
/// </summary>
/// <param name="Flags">File-level flags (sidecar presence, volume zone maps).</param>
/// <param name="ColumnCount">Number of columns in the schema.</param>
/// <param name="PageSize">
/// Rows per page. Locked to <see cref="DatumFormatV2.DefaultPageSize"/>
/// in v1; the field exists for forward-compatible page-size tuning.
/// </param>
/// <param name="TotalRowCount">Row count summed across all pages of any column.</param>
/// <param name="FooterOffset">Absolute byte offset of the footer body.</param>
public readonly record struct HeaderV2(
    DatumFileFlagsV2 Flags,
    int ColumnCount,
    int PageSize,
    long TotalRowCount,
    long FooterOffset)
{
    /// <summary>
    /// Writes this header to the first <see cref="DatumFormatV2.HeaderSize"/>
    /// bytes of <paramref name="destination"/>. Caller positions the stream
    /// at byte 0; <c>totalRowCount</c> and <c>footerOffset</c> are written
    /// as their current values (zero is the conventional placeholder).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is shorter than
    /// <see cref="DatumFormatV2.HeaderSize"/>.
    /// </exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < DatumFormatV2.HeaderSize)
        {
            throw new ArgumentException(
                $"Header destination must be at least {DatumFormatV2.HeaderSize} bytes.",
                nameof(destination));
        }

        DatumFormatV2.Magic.CopyTo(destination[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], DatumFormatV2.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], (ushort)Flags);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..12], ColumnCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..16], PageSize);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[DatumFormatV2.TotalRowCountFieldOffset..(DatumFormatV2.TotalRowCountFieldOffset + 8)],
            TotalRowCount);
        BinaryPrimitives.WriteInt64LittleEndian(
            destination[DatumFormatV2.FooterOffsetFieldOffset..(DatumFormatV2.FooterOffsetFieldOffset + 8)],
            FooterOffset);
    }

    /// <summary>
    /// Parses a V2 header from the first <see cref="DatumFormatV2.HeaderSize"/>
    /// bytes of <paramref name="source"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when the magic bytes don't match or the version is not
    /// <see cref="DatumFormatV2.FormatVersion"/>.
    /// </exception>
    public static HeaderV2 ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < DatumFormatV2.HeaderSize)
        {
            throw new ArgumentException(
                $"Header source must be at least {DatumFormatV2.HeaderSize} bytes.",
                nameof(source));
        }

        if (!source[..4].SequenceEqual(DatumFormatV2.Magic))
        {
            throw new InvalidDataException(
                "File magic does not match v2 .datum format (expected ASCII 'DTMF').");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]);
        if (version < DatumFormatV2.MinReadableFormatVersion || version > DatumFormatV2.FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported .datum format version {version} (reader accepts " +
                $"{DatumFormatV2.MinReadableFormatVersion}..{DatumFormatV2.FormatVersion}).");
        }

        DatumFileFlagsV2 flags = (DatumFileFlagsV2)BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]);
        int columnCount = BinaryPrimitives.ReadInt32LittleEndian(source[8..12]);
        int pageSize = BinaryPrimitives.ReadInt32LittleEndian(source[12..16]);
        long totalRowCount = BinaryPrimitives.ReadInt64LittleEndian(
            source[DatumFormatV2.TotalRowCountFieldOffset..(DatumFormatV2.TotalRowCountFieldOffset + 8)]);
        long footerOffset = BinaryPrimitives.ReadInt64LittleEndian(
            source[DatumFormatV2.FooterOffsetFieldOffset..(DatumFormatV2.FooterOffsetFieldOffset + 8)]);

        return new HeaderV2(flags, columnCount, pageSize, totalRowCount, footerOffset);
    }
}
