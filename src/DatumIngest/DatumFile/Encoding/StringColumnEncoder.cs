using System.Buffers;
using System.Buffers.Binary;
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

        try
        {
            // Iterate pages so each row resolves against the slice of the writer's arena
            // that contains its original batch-relative bytes.
            foreach (PageSpan page in context.Pages)
            {
                IValueStore pageStore = page.ArenaLength > 0
                    ? context.Store.Slice(page.ArenaBase, page.ArenaLength)
                    : context.Store;

                int endRow = page.RowStart + page.RowCount;
                for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
                {
                    DataValue value = values[rowIndex];

                    if (value.IsNull)
                    {
                        nullBitmap.SetNull(rowIndex);
                        encoded[rowIndex] = [];
                        nullCount++;
                        continue;
                    }

                    // Zero-copy: read UTF-8 bytes directly from the page store.
                    ReadOnlySpan<byte> utf8 = value.AsUtf8Span(pageStore);
                    encoded[rowIndex] = utf8.ToArray();
                    totalPoolBytes += encoded[rowIndex].Length;
                }
            }

            bool isJson = descriptor.Kind == DataKind.JsonValue;
            DatumZoneMap zoneMap = BuildZoneMap(nullCount, values, isJson, context);

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

    private static DatumZoneMap BuildZoneMap(
        uint nullCount,
        IReadOnlyList<DataValue> values,
        bool isJson,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        if (nullCount == (uint)rowCount)
        {
            return new DatumZoneMap(nullCount);
        }

        // JSON values do not have a meaningful lexicographic range for zone map pruning.
        if (isJson)
        {
            return new DatumZoneMap(nullCount);
        }

        string? minimum = null;
        string? maximum = null;

        foreach (PageSpan page in context.Pages)
        {
            IValueStore pageStore = page.ArenaLength > 0
                ? context.Store.Slice(page.ArenaBase, page.ArenaLength)
                : context.Store;

            int endRow = page.RowStart + page.RowCount;
            for (int rowIndex = page.RowStart; rowIndex < endRow; rowIndex++)
            {
                DataValue value = values[rowIndex];
                if (value.IsNull) continue;

                string text = value.AsString(pageStore);

                if (minimum is null || string.CompareOrdinal(text, minimum) < 0) minimum = text;
                if (maximum is null || string.CompareOrdinal(text, maximum) > 0) maximum = text;
            }
        }

        if (minimum is null)
        {
            return new DatumZoneMap(nullCount);
        }

        return new DatumZoneMap(nullCount, DataKind.String, minimum, maximum!);
    }
}
