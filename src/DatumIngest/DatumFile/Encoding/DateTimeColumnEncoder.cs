using System.Buffers;
using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.DateTime"/> column page using <see cref="DatumEncoding.DeltaInt64"/>
/// with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload:
/// <c>nullBitmap[ceil(N/8)] | baseline:int64 | tickDeltas:int64[N] | tzOffsets:int16[N]</c>.
/// <list type="bullet">
///   <item><c>baseline</c> is the UTC <see cref="DateTimeOffset.Ticks"/> of the first non-null row.</item>
///   <item><c>tickDeltas[i]</c> = <c>row[i].Ticks - baseline</c> for non-null rows; <c>0</c> for null rows.</item>
///   <item><c>tzOffsets[i]</c> = <c>(short)(row[i].Offset.TotalMinutes)</c>; <c>0</c> for null rows.</item>
/// </list>
/// Storing UTC tick deltas rather than absolute ticks keeps values in a narrow range for timestamps
/// clustered around a common epoch, substantially improving delta compression.
/// </remarks>
internal sealed class DateTimeColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        int bitmapLength = DatumNullBitmap.ByteCount(rowCount);
        int tickDeltasSize = rowCount * 8;
        int tzOffsetsSize = rowCount * 2;
        // bitmap | baseline(8) | tickDeltas[N](8 each) | tzOffsets[N](2 each)
        int rawLength = bitmapLength + 8 + tickDeltasSize + tzOffsetsSize;

        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            int deltasOffset = bitmapLength + 8;
            int tzOffset = deltasOffset + tickDeltasSize;

            // Zero the deltas and tz regions; null rows must store zero.
            Array.Clear(raw, deltasOffset, tickDeltasSize + tzOffsetsSize);

            long baseline = 0;
            bool baselineSet = false;
            long minimumTick = long.MaxValue;
            long maximumTick = long.MinValue;
            uint nullCount = 0;

            foreach (DataValue value in values)
            {
                if (!value.IsNull)
                {
                    baseline = value.AsDateTime().Ticks;
                    baselineSet = true;
                    break;
                }
            }

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                DataValue value = values[rowIndex];

                if (value.IsNull)
                {
                    nullBitmap.SetNull(rowIndex);
                    nullCount++;
                }
                else
                {
                    DateTimeOffset dto = value.AsDateTime();
                    long delta = dto.Ticks - baseline;
                    BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(deltasOffset + rowIndex * 8), delta);
                    BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(tzOffset + rowIndex * 2), (short)dto.Offset.TotalMinutes);
                    if (delta < minimumTick) minimumTick = delta;
                    if (delta > maximumTick) maximumTick = delta;
                }
            }

            DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, baseline, minimumTick, maximumTick, baselineSet);

            Buffer.BlockCopy(nullBitmap.ToBytes(), 0, raw, 0, bitmapLength);
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(bitmapLength), baseline);

            (byte[] compressed, int compressedLength) = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

            return new DatumEncodedPage(compressed, compressedLength, DatumEncoding.DeltaInt64, DatumCompression.Zstd, rawLength, zoneMap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static DatumZoneMap BuildZoneMap(
        uint nullCount,
        int rowCount,
        long baseline,
        long minimumTick,
        long maximumTick,
        bool baselineSet)
    {
        if (!baselineSet || nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount);
        }

        // Reconstruct absolute ticks for the zone map min/max. Use offset 0 for the zone map
        // DateTimeOffset values so predicate comparisons are UTC-normalized.
        DateTimeOffset minDto = new(baseline + minimumTick, TimeSpan.Zero);
        DateTimeOffset maxDto = new(baseline + maximumTick, TimeSpan.Zero);

        return new DatumZoneMap(nullCount, DataKind.DateTime, minDto, maxDto);
    }
}
