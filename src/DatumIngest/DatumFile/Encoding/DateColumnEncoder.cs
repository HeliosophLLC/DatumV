using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.Date"/> column page using <see cref="DatumEncoding.DeltaInt32"/>
/// with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload:
/// <c>nullBitmap[ceil(N/8)] | baseline:int32 | deltas:int32[N]</c>.
/// <list type="bullet">
///   <item><c>baseline</c> is the <see cref="DateOnly.DayNumber"/> of the first non-null row.
///   Zero when all rows are null.</item>
///   <item><c>deltas[i]</c> = <c>DayNumber(row[i]) - baseline</c> for non-null rows; <c>0</c> for null rows.</item>
/// </list>
/// Storing deltas rather than absolute day numbers reduces the range of values, which
/// improves Zstd's ability to exploit LZ patterns in the delta stream.
/// </remarks>
internal sealed class DateColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        int[] deltas = new int[rowCount];

        int baseline = 0;
        bool baselineSet = false;
        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        uint nullCount = 0;

        // First pass: determine baseline from first non-null row.
        foreach (DataValue value in values)
        {
            if (!value.IsNull)
            {
                baseline = value.AsDate().DayNumber;
                baselineSet = true;
                break;
            }
        }

        // Second pass: compute deltas.
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
                int delta = value.AsDate().DayNumber - baseline;
                deltas[rowIndex] = delta;
                if (delta < minimum) minimum = delta;
                if (delta > maximum) maximum = delta;
            }
        }

        DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, baseline, minimum, maximum, baselineSet);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        // baseline(4) + deltas[N](4 each)
        byte[] raw = new byte[bitmapBytes.Length + 4 + rowCount * 4];
        bitmapBytes.CopyTo(raw, 0);
        int writeOffset = bitmapBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(writeOffset), baseline);
        writeOffset += 4;
        foreach (int delta in deltas)
        {
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(writeOffset), delta);
            writeOffset += 4;
        }

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.DeltaInt32, DatumCompression.Zstd, raw.Length, zoneMap);
    }

    private static DatumZoneMap BuildZoneMap(uint nullCount, int rowCount, int baseline, int minimum, int maximum, bool baselineSet)
    {
        if (!baselineSet || nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        DateOnly minDate = DateOnly.FromDayNumber(baseline + minimum);
        DateOnly maxDate = DateOnly.FromDayNumber(baseline + maximum);

        return new DatumZoneMap(
            nullCount,
            DataValue.FromDate(minDate),
            DataValue.FromDate(maxDate));
    }
}
