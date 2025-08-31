using System.Buffers;
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
        int bitmapLength = DatumNullBitmap.ByteCount(rowCount);
        // bitmap | baseline(4) | deltas[N](4 each)
        int rawLength = bitmapLength + 4 + rowCount * 4;

        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            int baseline = 0;
            bool baselineSet = false;
            int minimum = int.MaxValue;
            int maximum = int.MinValue;
            uint nullCount = 0;
            int deltaOffset = bitmapLength + 4;

            // Zero the delta region; null rows must store zero.
            Array.Clear(raw, deltaOffset, rowCount * 4);

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

            // Second pass: write deltas directly into raw.
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
                    int delta = value.AsDate().DayNumber - baseline;
                    BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(deltaOffset + rowIndex * 4), delta);
                    if (delta < minimum) minimum = delta;
                    if (delta > maximum) maximum = delta;
                }
            }

            DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, baseline, minimum, maximum, baselineSet);

            Buffer.BlockCopy(nullBitmap.ToBytes(), 0, raw, 0, bitmapLength);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(bitmapLength), baseline);

            byte[] compressed = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

            return new DatumEncodedPage(compressed, DatumEncoding.DeltaInt32, DatumCompression.Zstd, rawLength, zoneMap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
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
