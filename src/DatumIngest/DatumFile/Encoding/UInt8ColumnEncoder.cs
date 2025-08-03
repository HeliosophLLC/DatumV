using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.UInt8"/> column page using <see cref="DatumEncoding.Raw"/>
/// layout with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload: <c>nullBitmap[ceil(N/8)] | byte[N]</c>.
/// Null rows store the value <c>0x00</c> in the byte array. The null bitmap is the authoritative
/// source of nullability; the stored zero is never returned to the caller.
/// </remarks>
internal sealed class UInt8ColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        byte[] data = new byte[rowCount];

        byte minimum = byte.MaxValue;
        byte maximum = byte.MinValue;
        uint nullCount = 0;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            DataValue value = values[rowIndex];

            if (value.IsNull)
            {
                nullBitmap.SetNull(rowIndex);
                data[rowIndex] = 0;
                nullCount++;
            }
            else
            {
                byte b = value.AsUInt8();
                data[rowIndex] = b;
                if (b < minimum) minimum = b;
                if (b > maximum) maximum = b;
            }
        }

        DatumZoneMap zoneMap = nullCount == (uint)rowCount
            ? new DatumZoneMap(nullCount, null, null)
            : new DatumZoneMap(nullCount, DataValue.FromUInt8(minimum), DataValue.FromUInt8(maximum));

        byte[] bitmapBytes = nullBitmap.ToBytes();
        byte[] raw = new byte[bitmapBytes.Length + data.Length];
        bitmapBytes.CopyTo(raw, 0);
        data.CopyTo(raw, bitmapBytes.Length);

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.Raw, DatumCompression.Zstd, raw.Length, zoneMap);
    }
}
