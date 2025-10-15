using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.Boolean"/> column page using <see cref="DatumEncoding.BitPacked"/>
/// layout (two packed bitmaps) with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload: <c>nullBitmap[ceil(N/8)] | valueBitmap[ceil(N/8)]</c>.
/// Bit <c>i</c> of the null bitmap is 1 when row <c>i</c> is null. Bit <c>i</c> of the value
/// bitmap is 1 when the boolean value is <c>true</c>; the bit for a null row is 0 but its
/// value must not be read by the decoder.
/// </remarks>
internal sealed class BooleanColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        int byteCount = DatumNullBitmap.ByteCount(rowCount);
        byte[] valueBits = new byte[byteCount];

        uint nullCount = 0;
        bool hasTrue = false;
        bool hasFalse = false;

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
                bool b = value.AsBoolean();
                if (b)
                {
                    valueBits[rowIndex >> 3] |= (byte)(1 << (rowIndex & 7));
                    hasTrue = true;
                }
                else
                {
                    hasFalse = true;
                }
            }
        }

        // Zone map for boolean: min = false (0), max = true (1) when both values are present.
        DatumZoneMap zoneMap = BuildZoneMap(nullCount, rowCount, hasTrue, hasFalse);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        byte[] raw = new byte[bitmapBytes.Length + valueBits.Length];
        bitmapBytes.CopyTo(raw, 0);
        valueBits.CopyTo(raw, bitmapBytes.Length);

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.BitPacked, DatumCompression.Zstd, raw.Length, zoneMap);
    }

    private static DatumZoneMap BuildZoneMap(uint nullCount, int rowCount, bool hasTrue, bool hasFalse)
    {
        if (nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount);
        }

        // min = false if any false seen, else true; max = true if any true seen, else false.
        bool minimum = !hasFalse;
        bool maximum = hasTrue;

        return new DatumZoneMap(nullCount, DataKind.Boolean, minimum, maximum);
    }
}
