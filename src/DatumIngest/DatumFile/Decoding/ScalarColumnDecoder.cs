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
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        float[] floats = new float[rowCount];
        FloatByteShuffle.Unshuffle(raw.AsSpan(bitmapByteCount), floats);

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            result[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Float32)
                : DataValue.FromFloat32(floats[rowIndex]);
        }

        return result;
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
