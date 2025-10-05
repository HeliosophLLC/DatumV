using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.DateTime"/> column page produced by <c>DateTimeColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout:
/// <c>nullBitmap[ceil(N/8)] | baseline:int64 | tickDeltas:int64[N] | tzOffsets:int16[N]</c>.
/// Each non-null row is reconstructed as
/// <c>new DateTimeOffset(baseline + tickDelta, TimeSpan.FromMinutes(tzOffset))</c>.
/// </remarks>
internal sealed class DateTimeColumnDecoder : DatumColumnDecoder
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

        // Read all tick deltas, then all tz offsets (layout matches the encoder's two-pass write).
        long[] tickDeltas = new long[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            tickDeltas[rowIndex] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(readOffset));
            readOffset += 8;
        }

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            short tzOffset = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(readOffset));
            readOffset += 2;

            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(DataKind.DateTime);
            }
            else
            {
                DateTimeOffset dto = new(baseline + tickDeltas[rowIndex], TimeSpan.FromMinutes(tzOffset));
                result[rowIndex] = DataValue.FromDateTime(dto);
            }
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
        Arena arena)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        int readOffset = bitmapByteCount;
        long baseline = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(readOffset));
        readOffset += 8;

        long[] tickDeltas = new long[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            tickDeltas[rowIndex] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(readOffset));
            readOffset += 8;
        }

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            short tzOffset = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(readOffset));
            readOffset += 2;

            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(DataKind.DateTime);
            }
            else
            {
                DateTimeOffset dto = new(baseline + tickDeltas[rowIndex], TimeSpan.FromMinutes(tzOffset));
                target[rowIndex] = DataValue.FromDateTime(dto);
            }
        }
    }
}
