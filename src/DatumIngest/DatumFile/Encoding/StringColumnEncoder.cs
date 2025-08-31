using System.Buffers;
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
        byte[][] encoded = ArrayPool<byte[]>.Shared.Rent(rowCount);
        uint nullCount = 0;
        int totalPoolBytes = 0;

        bool isJson = descriptor.Kind == DataKind.JsonValue;

        try
        {
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
            int offsetsSize = (rowCount + 1) * 4;
            int rawLength = bitmapBytes.Length + offsetsSize + totalPoolBytes;
            byte[] raw = ArrayPool<byte>.Shared.Rent(rawLength);

            try
            {
                Buffer.BlockCopy(bitmapBytes, 0, raw, 0, bitmapBytes.Length);

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
                        Buffer.BlockCopy(rowBytes, 0, raw, poolWrite, rowBytes.Length);
                        poolWrite += rowBytes.Length;
                    }
                }

                byte[] compressed = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

                return new DatumEncodedPage(compressed, DatumEncoding.VariableBytes, DatumCompression.Zstd, rawLength, zoneMap);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(encoded, clearArray: true);
        }
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
