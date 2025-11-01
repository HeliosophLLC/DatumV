using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes <see cref="DataKind.UInt8Array"/> and <see cref="DataKind.Image"/> column pages
/// produced by <c>BinaryColumnEncoder</c>.
/// </summary>
/// <remarks>
/// <para>
/// When the encoding is <see cref="DatumEncoding.VariableBytes"/> the pool
/// contains raw binary bytes.
/// </para>
/// <para>
/// When the encoding is <see cref="DatumEncoding.ExternalBytes"/> the pool
/// contains relative UTF-8 path strings.  Each non-null row's blob is loaded from the
/// sidecar file at <c>Path.Combine(datumFileDir, relativePath)</c>.
/// </para>
/// </remarks>
internal sealed class BinaryColumnDecoder : DatumColumnDecoder
{
    /// <inheritdoc/>
    public override DataValue[] Decode(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context)
    {
        if (encoding == DatumEncoding.SidecarBlobs)
        {
            return DecodeSidecar(payload, compression, uncompressedByteLength, rowCount, descriptor);
        }

        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        bool isImage = descriptor.Kind == DataKind.Image;
        bool isExternalized = encoding == DatumEncoding.ExternalBytes;

        int offsetsStart = bitmapByteCount;
        int poolStart = offsetsStart + (rowCount + 1) * 4;

        string datumFileDirectory = isExternalized
            ? Path.GetDirectoryName(context.DatumFilePath) ?? string.Empty
            : string.Empty;

        IValueStore store = context.Store
            ?? throw new InvalidOperationException("DatumDecoderContext.Store must be set for string decoding.");

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + rowIndex * 4));
            uint end   = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + (rowIndex + 1) * 4));

            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(isImage ? DataKind.Image : DataKind.UInt8Array);
                continue;
            }

            byte[] bytes;
            if (isExternalized)
            {
                string relativePath = System.Text.Encoding.UTF8.GetString(
                    raw, poolStart + (int)start, (int)(end - start));
                string absolutePath = Path.Combine(datumFileDirectory, relativePath);
                bytes = File.ReadAllBytes(absolutePath);
            }
            else
            {
                bytes = raw[(poolStart + (int)start)..(poolStart + (int)end)];
            }

            result[rowIndex] = isImage ? DataValue.FromImage(bytes, store) : DataValue.FromUInt8Array(bytes, store);
        }

        return result;
    }

    private static DataValue[] DecodeSidecar(
        byte[] payload,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        int offsetsStart = bitmapByteCount;
        int lengthsStart = offsetsStart + 8 * rowCount;

        bool isImage = descriptor.Kind == DataKind.Image;
        DataValue[] result = new DataValue[rowCount];

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(isImage ? DataKind.Image : DataKind.UInt8Array);
                continue;
            }

            long offset = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(offsetsStart + 8 * rowIndex));
            long length = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(lengthsStart + 8 * rowIndex));

            result[rowIndex] = isImage
                ? DataValue.FromImageInSidecar(offset, length)
                : DataValue.FromUInt8ArrayInSidecar(offset, length);
        }

        return result;
    }
}
