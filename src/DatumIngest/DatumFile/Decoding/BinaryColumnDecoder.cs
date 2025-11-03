using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes <see cref="DataKind.UInt8Array"/> and <see cref="DataKind.Image"/> column pages
/// produced by <c>BinaryColumnEncoder</c>. Supports <see cref="DatumEncoding.VariableBytes"/>
/// (bytes embedded in the pool) and <see cref="DatumEncoding.SidecarBlobs"/> (pointer-only
/// pages whose payloads live in the companion <c>.datum-blob</c> sidecar).
/// </summary>
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
            return DecodeSidecar(payload, compression, uncompressedByteLength, rowCount, descriptor, context);
        }

        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        bool isImage = descriptor.Kind == DataKind.Image;

        int offsetsStart = bitmapByteCount;
        int poolStart = offsetsStart + (rowCount + 1) * 4;

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

            byte[] bytes = raw[(poolStart + (int)start)..(poolStart + (int)end)];

            result[rowIndex] = isImage ? DataValue.FromImage(bytes, store) : DataValue.FromByteArray(bytes, store);
        }

        return result;
    }

    private static DataValue[] DecodeSidecar(
        byte[] payload,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        int offsetsStart = bitmapByteCount;
        int lengthsStart = offsetsStart + 8 * rowCount;

        bool isImage = descriptor.Kind == DataKind.Image;
        byte storeId = context.SidecarStoreId;
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
                ? DataValue.FromImageInSidecar(offset, length, storeId)
                : DataValue.FromByteArrayInSidecar(offset, length, storeId);
        }

        return result;
    }
}
