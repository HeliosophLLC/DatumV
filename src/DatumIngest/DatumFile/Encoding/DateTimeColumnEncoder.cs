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
        DatumNullBitmap nullBitmap = new(rowCount);
        long[] tickDeltas = new long[rowCount];
        short[] tzOffsets = new short[rowCount];

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
                tickDeltas[rowIndex] = 0;
                tzOffsets[rowIndex] = 0;
                nullCount++;
            }
            else
            {
                DateTimeOffset dto = value.AsDateTime();
                long delta = dto.Ticks - baseline;
                tickDeltas[rowIndex] = delta;
                tzOffsets[rowIndex] = (short)dto.Offset.TotalMinutes;
                if (delta < minimumTick) minimumTick = delta;
                if (delta > maximumTick) maximumTick = delta;
            }
        }

        DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, baseline, minimumTick, maximumTick, tzOffsets, baselineSet);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        // baseline(8) + tickDeltas[N](8 each) + tzOffsets[N](2 each)
        byte[] raw = new byte[bitmapBytes.Length + 8 + rowCount * 8 + rowCount * 2];
        bitmapBytes.CopyTo(raw, 0);
        int writeOffset = bitmapBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(writeOffset), baseline);
        writeOffset += 8;
        foreach (long delta in tickDeltas)
        {
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(writeOffset), delta);
            writeOffset += 8;
        }
        foreach (short tz in tzOffsets)
        {
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(writeOffset), tz);
            writeOffset += 2;
        }

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.DeltaInt64, DatumCompression.Zstd, raw.Length, zoneMap);
    }

    private static DatumZoneMap BuildZoneMap(
        uint nullCount,
        int rowCount,
        long baseline,
        long minimumTick,
        long maximumTick,
        short[] tzOffsets,
        bool baselineSet)
    {
        if (!baselineSet || nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        // Reconstruct absolute ticks for the zone map min/max. Use offset 0 for the zone map
        // DateTimeOffset values so predicate comparisons are UTC-normalized.
        DateTimeOffset minDto = new(baseline + minimumTick, TimeSpan.Zero);
        DateTimeOffset maxDto = new(baseline + maximumTick, TimeSpan.Zero);

        return new DatumZoneMap(
            nullCount,
            DataValue.FromDateTime(minDto),
            DataValue.FromDateTime(maxDto));
    }
}
