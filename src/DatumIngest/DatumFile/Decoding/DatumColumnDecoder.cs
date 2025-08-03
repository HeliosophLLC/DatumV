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
    /// Reads the null bitmap from the start of the uncompressed page bytes.
    /// The null bitmap occupies <see cref="DatumNullBitmap.ByteCount"/> bytes at offset 0.
    /// </summary>
    protected static DatumNullBitmap ReadNullBitmap(byte[] raw, int rowCount)
    {
        int byteCount = DatumNullBitmap.ByteCount(rowCount);
        return DatumNullBitmap.FromBytes(raw[0..byteCount], rowCount);
    }
}
