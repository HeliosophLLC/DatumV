using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.Duration"/> column page using <see cref="DatumEncoding.DeltaInt64"/>
/// with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload:
/// <c>nullBitmap[ceil(N/8)] | baseline:int64 | deltas:int64[N]</c>.
/// The baseline and deltas are expressed in <see cref="TimeSpan.Ticks"/> (100-nanosecond intervals).
/// Null rows store <c>0</c> as the delta.
/// </remarks>
internal sealed class DurationColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        long[] deltas = new long[rowCount];

        long baseline = 0;
        bool baselineSet = false;
        long minimum = long.MaxValue;
        long maximum = long.MinValue;
        uint nullCount = 0;

        foreach (DataValue value in values)
        {
            if (!value.IsNull)
            {
                baseline = value.AsDuration().Ticks;
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
                deltas[rowIndex] = 0;
                nullCount++;
            }
            else
            {
                long delta = value.AsDuration().Ticks - baseline;
                deltas[rowIndex] = delta;
                if (delta < minimum) minimum = delta;
                if (delta > maximum) maximum = delta;
            }
        }

        DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, baseline, minimum, maximum, baselineSet);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        byte[] raw = new byte[bitmapBytes.Length + 8 + rowCount * 8];
        bitmapBytes.CopyTo(raw, 0);
        int writeOffset = bitmapBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(writeOffset), baseline);
        writeOffset += 8;
        foreach (long delta in deltas)
        {
            BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(writeOffset), delta);
            writeOffset += 8;
        }

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.DeltaInt64, DatumCompression.Zstd, raw.Length, zoneMap);
    }

    private static DatumZoneMap BuildZoneMap(uint nullCount, int rowCount, long baseline, long minimum, long maximum, bool baselineSet)
    {
        if (!baselineSet || nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        TimeSpan minDuration = TimeSpan.FromTicks(baseline + minimum);
        TimeSpan maxDuration = TimeSpan.FromTicks(baseline + maximum);

        return new DatumZoneMap(nullCount, DataValue.FromDuration(minDuration), DataValue.FromDuration(maxDuration));
    }
}
