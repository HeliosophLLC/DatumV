using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.Boolean"/> column page produced by <c>BooleanColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | valueBitmap[ceil(N/8)]</c>.
/// Bit <c>i</c> of the value bitmap is 1 when the row is <c>true</c>.
/// The value bit for a null row is always 0 and must not be read by callers.
/// </remarks>
internal sealed class BooleanColumnDecoder : DatumColumnDecoder
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

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(DataKind.Boolean);
            }
            else
            {
                bool value = (raw[bitmapByteCount + (rowIndex >> 3)] & (1 << (rowIndex & 7))) != 0;
                result[rowIndex] = DataValue.FromBoolean(value);
            }
        }

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
        DataValue[] target)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(DataKind.Boolean);
            }
            else
            {
                bool value = (raw[bitmapByteCount + (rowIndex >> 3)] & (1 << (rowIndex & 7))) != 0;
                target[rowIndex] = DataValue.FromBoolean(value);
            }
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

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(DataKind.Boolean);
            }
            else
            {
                bool value = (raw[bitmapByteCount + (rowIndex >> 3)] & (1 << (rowIndex & 7))) != 0;
                target[rowIndex] = DataValue.FromBoolean(value);
            }
        }
    }
}
