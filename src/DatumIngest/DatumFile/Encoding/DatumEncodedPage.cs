namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// The compressed, encoded payload produced by a <see cref="DatumColumnEncoder"/> for a single
/// column page (one row group's worth of values). The writer consumes this object to flush the
/// page bytes to disk and to populate the corresponding <see cref="DatumColumnChunkDescriptor"/>
/// entry in the footer directory.
/// </summary>
public sealed class DatumEncodedPage
{
    /// <summary>
    /// Initializes a <see cref="DatumEncodedPage"/> with all required fields.
    /// </summary>
    /// <param name="payload">The final compressed bytes to write to the column chunk.</param>
    /// <param name="encoding">The logical encoding applied before compression.</param>
    /// <param name="compression">The compression codec applied to produce <paramref name="payload"/>.</param>
    /// <param name="uncompressedByteLength">
    /// The byte count of the payload before compression. Stored in the footer so the reader can
    /// pre-allocate the decompression buffer without needing an extra read.
    /// </param>
    /// <param name="zoneMap">
    /// Per-row-group statistics (null count, min, max) for predicate pushdown.
    /// </param>
    public DatumEncodedPage(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        DatumZoneMap zoneMap)
    {
        Payload = payload;
        Encoding = encoding;
        Compression = compression;
        UncompressedByteLength = uncompressedByteLength;
        ZoneMap = zoneMap;
    }

    /// <summary>The bytes to write verbatim to the column chunk in the file.</summary>
    public byte[] Payload { get; }

    /// <summary>The logical encoding strategy identifier stored in the footer.</summary>
    public DatumEncoding Encoding { get; }

    /// <summary>The compression codec identifier stored in the footer.</summary>
    public DatumCompression Compression { get; }

    /// <summary>
    /// The byte count of the logical page payload before compression was applied.
    /// Zero for <see cref="DatumCompression.None"/> pages.
    /// </summary>
    public int UncompressedByteLength { get; }

    /// <summary>Zone-map statistics for this row group slice of the column.</summary>
    public DatumZoneMap ZoneMap { get; }
}
