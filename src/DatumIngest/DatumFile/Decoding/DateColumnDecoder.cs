using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.Date"/> column page produced by <c>DateColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | baseline:int32 | deltas:int32[N]</c>.
/// Each non-null row is reconstructed as <c>DateOnly.FromDayNumber(baseline + delta)</c>.
/// </remarks>
internal sealed class DateColumnDecoder : DatumColumnDecoder
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
        int baseline = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(readOffset));
        readOffset += 4;

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            int delta = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(readOffset));
            readOffset += 4;

            result[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Date)
                : DataValue.FromDate(DateOnly.FromDayNumber(baseline + delta));
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
        int baseline = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(readOffset));
        readOffset += 4;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            int delta = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(readOffset));
            readOffset += 4;

            target[rowIndex] = nullBitmap.IsNull(rowIndex)
                ? DataValue.Null(DataKind.Date)
                : DataValue.FromDate(DateOnly.FromDayNumber(baseline + delta));
        }
    }
}
