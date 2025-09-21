using System.Buffers;
using System.Runtime.InteropServices;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes <see cref="DataKind.Float32"/> and <see cref="DataKind.Float64"/> scalar column pages
/// using <see cref="DatumEncoding.FixedFloat"/> with a byte-lane shuffle pre-filter and Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload: <c>nullBitmap[ceil(N/8)] | floatValues[N * bytesPerElement]</c>.
/// Null rows store NaN in the value array so element offsets remain implicit.
/// The value array is passed through <see cref="ByteLaneShuffle"/> before Zstd to
/// interleave byte lanes and improve compression ratio.
/// </remarks>
internal sealed class FloatColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        int bytesPerElement = descriptor.Kind == DataKind.Float64 ? sizeof(double) : sizeof(float);
        int bitmapLength = DatumNullBitmap.ByteCount(rowCount);
        int dataLength = rowCount * bytesPerElement;
        int rawLength = bitmapLength + dataLength;

        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] valueBytes = ArrayPool<byte>.Shared.Rent(dataLength);
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

        try
        {
            uint nullCount = 0;

            if (descriptor.Kind == DataKind.Float64)
            {
                double minimum = double.MaxValue;
                double maximum = double.MinValue;

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    DataValue value = values[rowIndex];

                    if (value.IsNull)
                    {
                        nullBitmap.SetNull(rowIndex);
                        BitConverter.TryWriteBytes(valueBytes.AsSpan(rowIndex * sizeof(double)), double.NaN);
                        nullCount++;
                    }
                    else
                    {
                        double d = value.AsFloat64();
                        BitConverter.TryWriteBytes(valueBytes.AsSpan(rowIndex * sizeof(double)), d);

                        if (!double.IsNaN(d))
                        {
                            if (d < minimum) minimum = d;
                            if (d > maximum) maximum = d;
                        }
                    }
                }

                DatumZoneMap zoneMap = BuildFloat64ZoneMap(nullCount, rowCount, minimum, maximum);
                return EncodePage(nullBitmap, valueBytes, raw, bitmapLength, dataLength, rawLength, bytesPerElement, nullCount, zoneMap);
            }
            else
            {
                float minimum = float.MaxValue;
                float maximum = float.MinValue;

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    DataValue value = values[rowIndex];

                    if (value.IsNull)
                    {
                        nullBitmap.SetNull(rowIndex);
                        BitConverter.TryWriteBytes(valueBytes.AsSpan(rowIndex * sizeof(float)), float.NaN);
                        nullCount++;
                    }
                    else
                    {
                        float f = value.AsFloat32();
                        BitConverter.TryWriteBytes(valueBytes.AsSpan(rowIndex * sizeof(float)), f);

                        if (!float.IsNaN(f))
                        {
                            if (f < minimum) minimum = f;
                            if (f > maximum) maximum = f;
                        }
                    }
                }

                DatumZoneMap zoneMap = BuildFloat32ZoneMap(nullCount, rowCount, minimum, maximum);
                return EncodePage(nullBitmap, valueBytes, raw, bitmapLength, dataLength, rawLength, bytesPerElement, nullCount, zoneMap);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(valueBytes);
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static DatumEncodedPage EncodePage(
        DatumNullBitmap nullBitmap,
        byte[] valueBytes,
        byte[] raw,
        int bitmapLength,
        int dataLength,
        int rawLength,
        int bytesPerElement,
        uint nullCount,
        DatumZoneMap zoneMap)
    {
        Buffer.BlockCopy(nullBitmap.ToBytes(), 0, raw, 0, bitmapLength);
        ByteLaneShuffle.Shuffle(
            valueBytes.AsSpan(0, dataLength),
            raw.AsSpan(bitmapLength, dataLength),
            bytesPerElement);

        byte[] compressed = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.FixedFloat, DatumCompression.Zstd, rawLength, zoneMap);
    }

    private static DatumZoneMap BuildFloat32ZoneMap(uint nullCount, int rowCount, float minimum, float maximum)
    {
        if (nullCount == (uint)rowCount || minimum > maximum)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        return new DatumZoneMap(nullCount, DataValue.FromFloat32(minimum), DataValue.FromFloat32(maximum));
    }

    private static DatumZoneMap BuildFloat64ZoneMap(uint nullCount, int rowCount, double minimum, double maximum)
    {
        if (nullCount == (uint)rowCount || minimum > maximum)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        return new DatumZoneMap(nullCount, DataValue.FromFloat64(minimum), DataValue.FromFloat64(maximum));
    }
}
