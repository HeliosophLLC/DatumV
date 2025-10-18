using System.Buffers;
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
        int bitmapLength = DatumNullBitmap.ByteCount(rowCount);
        int deltasSize = rowCount * 8;
        // bitmap | baseline(8) | deltas[N](8 each)
        int rawLength = bitmapLength + 8 + deltasSize;

        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            int deltasOffset = bitmapLength + 8;

            // Zero the deltas region; null rows must store zero.
            Array.Clear(raw, deltasOffset, deltasSize);

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
                    nullCount++;
                }
                else
                {
                    long delta = value.AsDuration().Ticks - baseline;
                    BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(deltasOffset + rowIndex * 8), delta);
                    if (delta < minimum) minimum = delta;
                    if (delta > maximum) maximum = delta;
                }
            }

            DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, baseline, minimum, maximum, baselineSet);

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

    private static DatumZoneMap BuildZoneMap(uint nullCount, int rowCount, long baseline, long minimum, long maximum, bool baselineSet)
    {
        if (!baselineSet || nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount);
        }

        TimeSpan minDuration = TimeSpan.FromTicks(baseline + minimum);
        TimeSpan maxDuration = TimeSpan.FromTicks(baseline + maximum);

        return new DatumZoneMap(nullCount, DataKind.Duration, minDuration, maxDuration);
    }
}
