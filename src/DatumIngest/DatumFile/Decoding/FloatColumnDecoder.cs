using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes <see cref="DataKind.Float32"/> and <see cref="DataKind.Float64"/> scalar column pages
/// produced by <c>FloatColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | shuffledValues[N * bytesPerElement]</c>.
/// The byte-lane shuffle applied before compression is reversed by
/// <see cref="ByteLaneShuffle.Unshuffle(ReadOnlySpan{byte}, Span{byte}, int)"/> before values
/// are converted to <see cref="DataValue"/> instances.
/// </remarks>
internal sealed class FloatColumnDecoder : DatumColumnDecoder
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
        DataValue[] result = new DataValue[rowCount];
        DecodeCore(payload, payload.Length, compression, uncompressedByteLength, rowCount, descriptor.Kind, result, decompressedBuffer: null);
        return result;
    }

    /// <inheritdoc/>
    public override void DecodeInto(
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
        int effectiveLength = payloadLength >= 0 ? payloadLength : payload.Length;
        DecodeCore(payload, effectiveLength, compression, uncompressedByteLength, rowCount, descriptor.Kind, target, decompressedBuffer);
    }

    private void DecodeCore(
        byte[] payload,
        int payloadLength,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DataKind kind,
        DataValue[] target,
        byte[]? decompressedBuffer)
    {
        byte[] raw;
        if (decompressedBuffer is not null)
        {
            DecompressPayloadInto(payload, payloadLength, decompressedBuffer, uncompressedByteLength, compression);
            raw = decompressedBuffer;
        }
        else
        {
            raw = DecompressPayload(payload, payloadLength, uncompressedByteLength, compression);
        }

        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        if (kind == DataKind.Float64)
        {
            DecodeFloat64(raw, bitmapByteCount, rowCount, nullBitmap, target);
        }
        else
        {
            DecodeFloat32(raw, bitmapByteCount, rowCount, nullBitmap, target);
        }
    }

    /// <inheritdoc/>
    public override void DecodeIntoColumn(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context,
        DataValue[] target,
        Arena arena)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        if (descriptor.Kind == DataKind.Float64)
        {
            DecodeFloat64(raw, bitmapByteCount, rowCount, nullBitmap, target);
        }
        else
        {
            DecodeFloat32(raw, bitmapByteCount, rowCount, nullBitmap, target);
        }
    }

    private static void DecodeFloat32(byte[] raw, int bitmapByteCount, int rowCount, DatumNullBitmap nullBitmap, DataValue[] target)
    {
        float[] floats = new float[rowCount];
        ByteLaneShuffle.Unshuffle(raw.AsSpan(bitmapByteCount), floats);

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            target[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Float32)
                : DataValue.FromFloat32(floats[rowIndex]);
        }
    }

    private static void DecodeFloat64(byte[] raw, int bitmapByteCount, int rowCount, DatumNullBitmap nullBitmap, DataValue[] target)
    {
        int dataLength = rowCount * sizeof(double);
        byte[] unshuffled = new byte[dataLength];
        ByteLaneShuffle.Unshuffle(
            raw.AsSpan(bitmapByteCount, dataLength),
            unshuffled,
            sizeof(double));

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(DataKind.Float64);
            }
            else
            {
                double d = BinaryPrimitives.ReadDoubleLittleEndian(unshuffled.AsSpan(rowIndex * sizeof(double)));
                target[rowIndex] = DataValue.FromFloat64(d);
            }
        }
    }
}
