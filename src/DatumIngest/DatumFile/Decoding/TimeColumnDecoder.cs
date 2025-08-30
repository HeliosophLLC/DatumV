using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.Time"/> column page produced by <c>TimeColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | baseline:int64 | deltas:int64[N]</c>.
/// Each non-null row is reconstructed as <c>new TimeOnly(baseline + delta)</c>.
/// </remarks>
internal sealed class TimeColumnDecoder : DatumColumnDecoder
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

        int readOffset = bitmapByteCount;
        long baseline = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(readOffset));
        readOffset += 8;

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            long delta = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(readOffset));
            readOffset += 8;

            result[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Time)
                : DataValue.FromTime(new TimeOnly(baseline + delta));
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

        int readOffset = bitmapByteCount;
        long baseline = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(readOffset));
        readOffset += 8;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            long delta = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(readOffset));
            readOffset += 8;

            target[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Time)
                : DataValue.FromTime(new TimeOnly(baseline + delta));
        }
    }
}
