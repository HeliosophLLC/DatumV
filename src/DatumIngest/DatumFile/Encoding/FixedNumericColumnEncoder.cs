using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes fixed-width numeric columns (Int8, Int16, UInt16, Int32, UInt32, Int64, UInt64,
/// Float64) using <see cref="DatumEncoding.Raw"/> with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload: <c>nullBitmap[ceil(N/8)] | values[N * bytesPerElement]</c>.
/// Null rows store zeroed bytes in the value array. The null bitmap is the authoritative
/// source of nullability.
/// </remarks>
internal sealed class FixedNumericColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        int bytesPerElement = BytesPerElement(descriptor.Kind);
        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] data = new byte[rowCount * bytesPerElement];

        uint nullCount = 0;
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;

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
                double numericValue = WriteValue(descriptor.Kind, value, data, rowIndex * bytesPerElement);

                if (numericValue < minimum)
                {
                    minimum = numericValue;
                }

                if (numericValue > maximum)
                {
                    maximum = numericValue;
                }
            }
        }

        DatumZoneMap zoneMap = BuildZoneMap(descriptor.Kind, nullCount, rowCount, minimum, maximum);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        byte[] raw = new byte[bitmapBytes.Length + data.Length];
        bitmapBytes.CopyTo(raw, 0);
        data.CopyTo(raw, bitmapBytes.Length);

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.Raw, DatumCompression.Zstd, raw.Length, zoneMap);
    }

    /// <summary>
    /// Returns the number of bytes per element for the given numeric <see cref="DataKind"/>.
    /// </summary>
    internal static int BytesPerElement(DataKind kind)
    {
        return kind switch
        {
            DataKind.Int8 => 1,
            DataKind.Int16 or DataKind.UInt16 => 2,
            DataKind.Int32 or DataKind.UInt32 => 4,
            DataKind.Int64 or DataKind.UInt64 or DataKind.Float64 => 8,
            _ => throw new NotSupportedException($"FixedNumericColumnEncoder does not support DataKind.{kind}.")
        };
    }

    /// <summary>
    /// Writes a single value to the byte array at the given offset and returns the value as a double
    /// for zone map tracking.
    /// </summary>
    private static double WriteValue(DataKind kind, DataValue value, byte[] buffer, int offset)
    {
        switch (kind)
        {
            case DataKind.Int8:
                buffer[offset] = unchecked((byte)value.AsInt8());
                return value.AsInt8();

            case DataKind.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset), value.AsInt16());
                return value.AsInt16();

            case DataKind.UInt16:
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), value.AsUInt16());
                return value.AsUInt16();

            case DataKind.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), value.AsInt32());
                return value.AsInt32();

            case DataKind.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value.AsUInt32());
                return value.AsUInt32();

            case DataKind.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), value.AsInt64());
                return value.AsInt64();

            case DataKind.UInt64:
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), value.AsUInt64());
                return value.AsUInt64();

            case DataKind.Float64:
                BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset), value.AsFloat64());
                return value.AsFloat64();

            default:
                throw new NotSupportedException($"FixedNumericColumnEncoder does not support DataKind.{kind}.");
        }
    }

    private static DatumZoneMap BuildZoneMap(DataKind kind, uint nullCount, int rowCount, double minimum, double maximum)
    {
        if (nullCount == (uint)rowCount || minimum > maximum)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        DataValue minValue = CreateFromDouble(kind, minimum);
        DataValue maxValue = CreateFromDouble(kind, maximum);
        return new DatumZoneMap(nullCount, minValue, maxValue);
    }

    private static DataValue CreateFromDouble(DataKind kind, double value)
    {
        return kind switch
        {
            DataKind.Int8 => DataValue.FromInt8((sbyte)value),
            DataKind.Int16 => DataValue.FromInt16((short)value),
            DataKind.UInt16 => DataValue.FromUInt16((ushort)value),
            DataKind.Int32 => DataValue.FromInt32((int)value),
            DataKind.UInt32 => DataValue.FromUInt32((uint)value),
            DataKind.Int64 => DataValue.FromInt64((long)value),
            DataKind.UInt64 => DataValue.FromUInt64((ulong)value),
            DataKind.Float64 => DataValue.FromFloat64(value),
            _ => throw new NotSupportedException($"FixedNumericColumnEncoder does not support DataKind.{kind}.")
        };
    }
}
