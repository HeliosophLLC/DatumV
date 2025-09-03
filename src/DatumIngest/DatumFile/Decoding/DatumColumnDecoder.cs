using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Converts a compressed column page payload back into an array of <see cref="DataValue"/>
/// instances, one per row.  Each concrete subclass mirrors the encoding strategy of its
/// corresponding <c>DatumColumnEncoder</c> subclass.
/// </summary>
public abstract class DatumColumnDecoder
{
    /// <summary>
    /// Decodes a column page directly into a pre-allocated <see cref="DataValue"/> buffer,
    /// writing string and binary payloads into the provided arenas instead of allocating
    /// individual heap objects.
    /// </summary>
    /// <param name="payload">Compressed page bytes as written to the <c>.datum</c> file.</param>
    /// <param name="encoding">Encoding applied before compression.</param>
    /// <param name="compression">Compression algorithm used.</param>
    /// <param name="uncompressedByteLength">Expected byte count after decompression.</param>
    /// <param name="rowCount">Number of rows in this page.</param>
    /// <param name="descriptor">Column schema descriptor.</param>
    /// <param name="context">Decoder context carrying the datum file path for sidecar blob resolution.</param>
    /// <param name="target">Pre-allocated column buffer with at least <paramref name="rowCount"/> slots.</param>
    /// <param name="stringArena">Shared string arena for UTF-8 string payloads.</param>
    /// <param name="dataArena">Shared data arena for float and byte array payloads.</param>
    public virtual void DecodeIntoColumn(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context,
        DataValue[] target,
        StringArena stringArena,
        DataArena dataArena)
    {
        DataValue[] decoded = Decode(payload, encoding, compression, uncompressedByteLength, rowCount, descriptor, context);
        decoded.AsSpan(0, rowCount).CopyTo(target);
    }

    /// <summary>
    /// Decodes a column page into a pre-allocated <see cref="DataValue"/> buffer, producing
    /// standalone (non-arena-backed) values. Numeric decoders override this to write directly
    /// into the target without allocating intermediate arrays. String and binary decoders fall
    /// back to <see cref="Decode(byte[], DatumEncoding, DatumCompression, int, int, DatumColumnDescriptor, DatumDecoderContext)"/> plus a copy, producing materialised values safe for use
    /// outside any arena lifetime.
    /// </summary>
    /// <param name="payload">
    /// Buffer containing compressed page bytes. May be larger than the actual payload when
    /// a shared buffer is reused; <paramref name="payloadLength"/> specifies the valid region.
    /// </param>
    /// <param name="encoding">Encoding applied before compression.</param>
    /// <param name="compression">Compression algorithm used.</param>
    /// <param name="uncompressedByteLength">Expected byte count after decompression.</param>
    /// <param name="rowCount">Number of rows in this page.</param>
    /// <param name="descriptor">Column schema descriptor.</param>
    /// <param name="context">Decoder context carrying the datum file path for sidecar blob resolution.</param>
    /// <param name="target">Pre-allocated column buffer with at least <paramref name="rowCount"/> slots.</param>
    /// <param name="payloadLength">
    /// Number of valid bytes in <paramref name="payload"/>. When negative, the entire
    /// array is used. Pass this when the payload buffer is a shared rental larger than the
    /// actual compressed page.
    /// </param>
    /// <param name="decompressedBuffer">
    /// Optional caller-owned buffer for decompressed page bytes. When non-null, decompression
    /// writes into this buffer instead of allocating a fresh array, avoiding Gen 2 deaths from
    /// repeated short-lived allocations. Must be at least <paramref name="uncompressedByteLength"/>
    /// bytes long.
    /// </param>
    public virtual void DecodeInto(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context,
        DataValue[] target,
        int payloadLength = -1,
        byte[]? decompressedBuffer = null)
    {
        byte[] effectivePayload = payloadLength >= 0 && payloadLength < payload.Length
            ? payload[..payloadLength]
            : payload;
        DataValue[] decoded = Decode(effectivePayload, encoding, compression, uncompressedByteLength, rowCount, descriptor, context);
        decoded.AsSpan(0, rowCount).CopyTo(target);
    }

    /// <summary>
    /// Decodes a column page using an empty decoder context.
    /// Suitable for in-memory round-trip tests and callers whose pages contain no externalized blobs.
    /// </summary>
    /// <param name="payload">Compressed page bytes as written to the <c>.datum</c> file.</param>
    /// <param name="encoding">Encoding applied before compression.</param>
    /// <param name="compression">Compression algorithm used.</param>
    /// <param name="uncompressedByteLength">Expected byte count after decompression.</param>
    /// <param name="rowCount">Number of rows in this page.</param>
    /// <param name="descriptor">Column schema descriptor.</param>
    public DataValue[] Decode(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor)
        => Decode(payload, encoding, compression, uncompressedByteLength, rowCount, descriptor, DatumDecoderContext.Empty);

    /// <summary>
    /// Decodes a column page from its compressed payload and returns one <see cref="DataValue"/>
    /// per row in row order.
    /// </summary>
    /// <param name="payload">Compressed page bytes as written to the <c>.datum</c> file.</param>
    /// <param name="encoding">Encoding applied before compression.</param>
    /// <param name="compression">Compression algorithm used.</param>
    /// <param name="uncompressedByteLength">Expected byte count after decompression.</param>
    /// <param name="rowCount">Number of rows in this page.</param>
    /// <param name="descriptor">Column schema descriptor.</param>
    /// <param name="context">Decoder context carrying the datum file path for sidecar blob resolution.</param>
    public abstract DataValue[] Decode(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context);

    /// <summary>Decompresses the page payload using the given codec.</summary>
    protected static byte[] DecompressPayload(byte[] payload, int uncompressedByteLength, DatumCompression compression)
        => DatumCompressor.Decompress(payload, uncompressedByteLength, compression);

    /// <summary>
    /// Decompresses a region of the page payload using the given codec.
    /// Avoids array allocation when the buffer is shared and larger than the actual payload.
    /// </summary>
    protected static byte[] DecompressPayload(byte[] payload, int payloadLength, int uncompressedByteLength, DatumCompression compression)
        => DatumCompressor.Decompress(payload.AsSpan(0, payloadLength), uncompressedByteLength, compression);

    /// <summary>
    /// Decompresses a region of the page payload into a caller-provided buffer,
    /// avoiding output array allocation when the same buffer is reused across pages.
    /// </summary>
    protected static int DecompressPayloadInto(byte[] payload, int payloadLength, byte[] destination, int uncompressedByteLength, DatumCompression compression)
        => DatumCompressor.DecompressInto(payload.AsSpan(0, payloadLength), destination, uncompressedByteLength, compression);

    /// <summary>
    /// Reads the null bitmap from the start of the uncompressed page bytes.
    /// The null bitmap occupies <see cref="DatumNullBitmap.ByteCount"/> bytes at offset 0.
    /// </summary>
    protected static DatumNullBitmap ReadNullBitmap(byte[] raw, int rowCount)
    {
        int byteCount = DatumNullBitmap.ByteCount(rowCount);
        return DatumNullBitmap.FromBytes(raw.AsSpan(0, byteCount), rowCount);
    }
}
