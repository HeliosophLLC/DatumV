using System.Buffers;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// The compressed, encoded payload produced by a <see cref="DatumColumnEncoder"/> for a single
/// column page (one row group's worth of values). The writer consumes this object to flush the
/// page bytes to disk and to populate the corresponding <see cref="DatumColumnChunkDescriptor"/>
/// entry in the footer directory.
/// </summary>
/// <remarks>
/// <para>
/// The payload bytes live in a buffer rented from <see cref="ArrayPool{T}.Shared"/>. After the
/// consumer has written <see cref="Payload"/> to its output stream, it <em>must</em> call
/// <see cref="ReturnBuffer"/> to return the rented buffer to the pool. Double-return and
/// use-after-return are both incorrect.
/// </para>
/// <para>
/// <see cref="PayloadBuffer"/> may be larger than <see cref="PayloadLength"/>; always read or
/// write from <see cref="Payload"/> which is sliced to the exact payload size.
/// </para>
/// </remarks>
public sealed class DatumEncodedPage
{
    /// <summary>
    /// Initializes a <see cref="DatumEncodedPage"/> with all required fields.
    /// </summary>
    /// <param name="payloadBuffer">
    /// Pooled buffer (from <see cref="ArrayPool{T}.Shared"/>) whose first <paramref name="payloadLength"/>
    /// bytes are the compressed payload. Caller must return this buffer to the pool via
    /// <see cref="ReturnBuffer"/> after writing the payload.
    /// </param>
    /// <param name="payloadLength">Number of compressed bytes in <paramref name="payloadBuffer"/>.</param>
    /// <param name="encoding">The logical encoding applied before compression.</param>
    /// <param name="compression">The compression codec applied to produce the payload.</param>
    /// <param name="uncompressedByteLength">
    /// The byte count of the payload before compression. Stored in the footer so the reader can
    /// pre-allocate the decompression buffer without needing an extra read.
    /// </param>
    /// <param name="zoneMap">
    /// Per-row-group statistics (null count, min, max) for predicate pushdown.
    /// </param>
    public DatumEncodedPage(
        byte[] payloadBuffer,
        int payloadLength,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        DatumZoneMap zoneMap)
    {
        PayloadBuffer = payloadBuffer;
        PayloadLength = payloadLength;
        Encoding = encoding;
        Compression = compression;
        UncompressedByteLength = uncompressedByteLength;
        ZoneMap = zoneMap;
    }

    /// <summary>
    /// Rented backing buffer. Typically larger than <see cref="PayloadLength"/>; use
    /// <see cref="Payload"/> for the exact bytes to write.
    /// </summary>
    public byte[] PayloadBuffer { get; }

    /// <summary>Number of compressed bytes in <see cref="PayloadBuffer"/>.</summary>
    public int PayloadLength { get; }

    /// <summary>The bytes to write verbatim to the column chunk in the file.</summary>
    public ReadOnlySpan<byte> Payload => PayloadBuffer.AsSpan(0, PayloadLength);

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

    /// <summary>
    /// Returns <see cref="PayloadBuffer"/> to <see cref="ArrayPool{T}.Shared"/>. Must be called
    /// exactly once after the payload has been consumed. After this call the page's payload
    /// fields must not be read.
    /// </summary>
    public void ReturnBuffer()
    {
        ArrayPool<byte>.Shared.Return(PayloadBuffer);
    }
}
