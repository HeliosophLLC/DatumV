using System.Buffers.Binary;
using System.Text;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes <see cref="DataKind.String"/> and <see cref="DataKind.JsonValue"/> column pages
/// using <see cref="DatumEncoding.VariableBytes"/> layout with Zstd compression.
/// </summary>
/// <remarks>
/// Layout of the uncompressed payload:
/// <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// <list type="bullet">
///   <item><c>offsets[0]</c> = 0 always.</item>
///   <item><c>offsets[i+1]</c> = <c>offsets[i] + UTF-8 byte length of row[i]</c>.</item>
///   <item>Null rows and empty strings both produce <c>offsets[i] == offsets[i+1]</c>;
///   the null bitmap distinguishes them.</item>
/// </list>
/// Storing <c>N+1</c> offsets rather than <c>N</c> (length, offset) pairs avoids an
/// addition per row during decoding and enables vectorised range reads.
/// </remarks>
internal sealed class StringColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        byte[][] encoded = new byte[rowCount][];
        uint nullCount = 0;
        int totalPoolBytes = 0;

        bool isJson = descriptor.Kind == DataKind.JsonValue;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            DataValue value = values[rowIndex];

            if (value.IsNull)
            {
                nullBitmap.SetNull(rowIndex);
                encoded[rowIndex] = [];
                nullCount++;
            }
            else
            {
                string text = isJson ? value.AsJsonValue() : value.AsString();
                encoded[rowIndex] = System.Text.Encoding.UTF8.GetBytes(text);
                totalPoolBytes += encoded[rowIndex].Length;
            }
        }

        // Zone map for strings: min and max by ordinal comparison.
        DatumZoneMap zoneMap = BuildZoneMap(nullCount, values, isJson);

        byte[] bitmapBytes = nullBitmap.ToBytes();
        // offsets: (N+1) * 4 bytes
        // pool: totalPoolBytes bytes
        int offsetsSize = (rowCount + 1) * 4;
        byte[] raw = new byte[bitmapBytes.Length + offsetsSize + totalPoolBytes];
        bitmapBytes.CopyTo(raw, 0);

        int offsetWrite = bitmapBytes.Length;
        int poolWrite = bitmapBytes.Length + offsetsSize;
        uint runningOffset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
        offsetWrite += 4;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            byte[] rowBytes = encoded[rowIndex];
            runningOffset += (uint)rowBytes.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
            offsetWrite += 4;

            if (rowBytes.Length > 0)
            {
                rowBytes.CopyTo(raw, poolWrite);
                poolWrite += rowBytes.Length;
            }
        }

        byte[] compressed = DatumCompressor.Compress(raw, DatumCompression.Zstd);

        return new DatumEncodedPage(compressed, DatumEncoding.VariableBytes, DatumCompression.Zstd, raw.Length, zoneMap);
    }

    private static DatumZoneMap BuildZoneMap(uint nullCount, IReadOnlyList<DataValue> values, bool isJson)
    {
        int rowCount = values.Count;
        if (nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        // JSON values do not have a meaningful lexicographic range for zone map pruning.
        if (isJson)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        string? minimum = null;
        string? maximum = null;

        foreach (DataValue value in values)
        {
            if (value.IsNull) continue;

            string text = value.AsString();

            if (minimum is null || string.CompareOrdinal(text, minimum) < 0) minimum = text;
            if (maximum is null || string.CompareOrdinal(text, maximum) > 0) maximum = text;
        }

        if (minimum is null)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        return new DatumZoneMap(
            nullCount,
            DataValue.FromString(minimum),
            DataValue.FromString(maximum!));
    }
}
