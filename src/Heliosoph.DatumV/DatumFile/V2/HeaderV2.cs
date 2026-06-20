using System.Buffers.Binary;

namespace Heliosoph.DatumV.DatumFile.V2;

/// <summary>
/// Fixed-size V2 file header. Sits at byte 0 of every <c>.datum</c> file
/// and records the format version, the minimum reader version required
/// to parse the file, file-level flags, schema width, page size, total
/// row count, and footer offset. <see cref="TotalRowCount"/> and
/// <see cref="FooterOffset"/> are placeholder zeros during streaming
/// writes and patched on finalize at
/// <see cref="DatumFormatV2.TotalRowCountFieldOffset"/> and
/// <see cref="DatumFormatV2.FooterOffsetFieldOffset"/>.
/// </summary>
/// <param name="Flags">File-level flags (sidecar presence, volume zone maps, etc.).</param>
/// <param name="ColumnCount">Number of columns in the schema.</param>
/// <param name="PageSize">
/// Rows per page. Locked to <see cref="DatumFormatV2.DefaultPageSize"/>
/// in v1; the field exists for forward-compatible page-size tuning.
/// Stored on disk as <c>uint16</c> (max 65535).
/// </param>
/// <param name="TotalRowCount">Row count summed across all pages of any column.</param>
/// <param name="FooterOffset">Absolute byte offset of the footer body.</param>
/// <param name="MinReaderVersion">
/// Cooperative gate introduced in v7: the writer's assertion of the
/// lowest reader version that can correctly parse this file. Reader
/// rule is <c>header.MinReaderVersion ≤ DatumFormatV2.FormatVersion</c>
/// — files claiming a floor newer than this reader's binary are
/// rejected. Defaults to <see cref="DatumFormatV2.FormatVersion"/>
/// (the safe, conservative value: assume the file uses every feature
/// the writer's binary supports). Future writers that add purely
/// additive features (new prologue extensions, new flag-gated
/// end-of-footer blocks) can lower this to the historical floor so
/// older readers continue to load the file; writers that change the
/// wire layout must keep it at their own version.
/// </param>
public readonly record struct HeaderV2(
    DatumFileFlagsV2 Flags,
    int ColumnCount,
    int PageSize,
    long TotalRowCount,
    long FooterOffset,
    ushort MinReaderVersion = DatumFormatV2.FormatVersion)
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

        // v7 header layout (32 bytes):
        //   0-3   magic ("DTMF")
        //   4-5   FormatVersion       (uint16 = writer-stamped)
        //   6-7   MinReaderVersion    (uint16 = cooperative reader floor)
        //   8-9   Flags               (uint16)
        //   10-11 PageSize            (uint16; locked at 1024 today)
        //   12-15 ColumnCount         (int32)
        //   16-23 TotalRowCount       (int64; patched on finalize)
        //   24-31 FooterOffset        (int64; patched on finalize)
        DatumFormatV2.Magic.CopyTo(destination[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], DatumFormatV2.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], MinReaderVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..10], (ushort)Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..12], checked((ushort)PageSize));
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..16], ColumnCount);
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
        ushort minReaderVersion = BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]);

        // Defensive floor: pre-v7 files don't exist outside dev machines,
        // and their header layout differs (no MinReaderVersion field, wider
        // PageSize encoding) so we'd mis-parse the rest of the header even
        // if we tried to read them.
        if (version < DatumFormatV2.MinReadableFormatVersion)
        {
            throw new InvalidDataException(
                $".datum format version {version} is older than this reader's floor " +
                $"({DatumFormatV2.MinReadableFormatVersion}). Re-emit the file with a current writer.");
        }

        // Cooperative gate: the writer asserts the lowest reader version
        // that can correctly parse this file. We do NOT gate on
        // header.FormatVersion exceeding our own — a future writer can
        // legitimately stamp version=8 with minReaderVersion=7 when the
        // file only uses purely-additive features (new prologue extension
        // tags, new flag-gated end-of-footer blocks); this reader will
        // load that file, see the unknown flag bits, and skip the unknown
        // trailing blocks. minReaderVersion is the load-bearing field.
        if (minReaderVersion > DatumFormatV2.FormatVersion)
        {
            throw new InvalidDataException(
                $".datum file requires reader version {minReaderVersion} or higher; " +
                $"this reader is {DatumFormatV2.FormatVersion}. Upgrade the binary.");
        }

        DatumFileFlagsV2 flags = (DatumFileFlagsV2)BinaryPrimitives.ReadUInt16LittleEndian(source[8..10]);
        int pageSize = BinaryPrimitives.ReadUInt16LittleEndian(source[10..12]);
        int columnCount = BinaryPrimitives.ReadInt32LittleEndian(source[12..16]);
        long totalRowCount = BinaryPrimitives.ReadInt64LittleEndian(
            source[DatumFormatV2.TotalRowCountFieldOffset..(DatumFormatV2.TotalRowCountFieldOffset + 8)]);
        long footerOffset = BinaryPrimitives.ReadInt64LittleEndian(
            source[DatumFormatV2.FooterOffsetFieldOffset..(DatumFormatV2.FooterOffsetFieldOffset + 8)]);

        return new HeaderV2(flags, columnCount, pageSize, totalRowCount, footerOffset, minReaderVersion);
    }
}
