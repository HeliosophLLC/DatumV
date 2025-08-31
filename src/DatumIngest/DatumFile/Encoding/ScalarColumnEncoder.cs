using System.Buffers;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.Float32"/> column page using <see cref="DatumEncoding.FixedFloat32"/>
/// with a byte-shuffle pre-filter and Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload: <c>nullBitmap[ceil(N/8)] | float32[N]</c>.
/// Null rows store <c>float.NaN</c> in the float array so element offsets remain implicit
/// (the reader does not need to skip over variable-length data to reach row <c>i</c>).
/// The float array is passed through <see cref="FloatByteShuffle.Shuffle"/> before Zstd to
/// interleave byte lanes and improve compression ratio.
/// </remarks>
internal sealed class ScalarColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        int bitmapLength = DatumNullBitmap.ByteCount(rowCount);
        int shuffledLength = rowCount * sizeof(float);
        int rawLength = bitmapLength + shuffledLength;

        DatumNullBitmap nullBitmap = new(rowCount);
        float[] floatData = ArrayPool<float>.Shared.Rent(rowCount);
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            uint nullCount = 0;

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                DataValue value = values[rowIndex];

                if (value.IsNull)
                {
                    nullBitmap.SetNull(rowIndex);
                    floatData[rowIndex] = float.NaN;
                    nullCount++;
                }
                else
                {
                    float scalar = value.AsFloat32();
                    floatData[rowIndex] = scalar;

                    // Skip NaN values in min/max so that intentionally-stored NaN user data
                    // does not corrupt the zone map. Null rows are already excluded via IsNull.
                    if (!float.IsNaN(scalar))
                    {
                        if (scalar < minimum) minimum = scalar;
                        if (scalar > maximum) maximum = scalar;
                    }
                }
            }

            DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, minimum, maximum);

            Buffer.BlockCopy(nullBitmap.ToBytes(), 0, raw, 0, bitmapLength);
            FloatByteShuffle.Shuffle(floatData.AsSpan(0, rowCount), raw.AsSpan(bitmapLength, shuffledLength));

            byte[] compressed = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

            return new DatumEncodedPage(compressed, DatumEncoding.FixedFloat32, DatumCompression.Zstd, rawLength, zoneMap);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatData);
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static DatumZoneMap BuildZoneMap(uint nullCount, int rowCount, float minimum, float maximum)
    {
        // All rows are null → no min/max available.
        if (nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        // If every non-null value was NaN, min/max remain at sentinel values — treat as no min/max.
        if (minimum > maximum)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        return new DatumZoneMap(nullCount, DataValue.FromFloat32(minimum), DataValue.FromFloat32(maximum));
    }
}
