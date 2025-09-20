using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.Float32"/> column page produced by <c>ScalarColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | shuffledFloat32[N*4]</c>.
/// The byte-shuffle pre-filter applied before compression is undone by
/// <see cref="FloatByteShuffle.Unshuffle"/> before values are converted to
/// <see cref="DataValue"/> instances.
/// </remarks>
internal sealed class ScalarColumnDecoder : DatumColumnDecoder
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
        DecodeCore(payload, payload.Length, compression, uncompressedByteLength, rowCount, result, decompressedBuffer: null);
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
        DecodeCore(payload, effectiveLength, compression, uncompressedByteLength, rowCount, target, decompressedBuffer);
    }

    private void DecodeCore(
        byte[] payload,
        int payloadLength,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
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

        float[] floats = new float[rowCount];
        FloatByteShuffle.Unshuffle(raw.AsSpan(bitmapByteCount), floats);

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            target[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Float32)
                : DataValue.FromFloat32(floats[rowIndex]);
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
        StringArena stringArena,
        DataArena dataArena)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        float[] floats = new float[rowCount];
        FloatByteShuffle.Unshuffle(raw.AsSpan(bitmapByteCount), floats);

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            target[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Float32)
                : DataValue.FromFloat32(floats[rowIndex]);
        }
    }
}
