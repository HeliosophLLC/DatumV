using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Indexing;
using DatumIngest.IO;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Encodes a <see cref="DataKind.Array"/> column page using <see cref="DatumEncoding.VariableDataValue"/>
/// with Zstd compression.
/// </summary>
/// <remarks>
/// <para>
/// Array columns contain rows whose values are each a variable-length sequence of
/// <see cref="DataValue"/> elements sharing a common element kind.
/// </para>
/// <para>
/// Layout of the uncompressed payload:
/// <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// The pool contains the serialized element streams for each row, concatenated. Each element is
/// serialized using the same wire format as <c>IndexWriter.WriteDataValue</c>, where the first byte
/// is the <see cref="DataKind"/> tag followed by kind-specific bytes. Null rows contribute
/// zero bytes to the pool; <c>offsets[i] == offsets[i+1]</c> and the null bitmap bit is set.
/// </para>
/// </remarks>
internal sealed class ArrayColumnEncoder : DatumColumnEncoder
{
    /// <inheritdoc/>
    public override DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context)
    {
        int rowCount = values.Count;
        DatumNullBitmap nullBitmap = new(rowCount);
        byte[][] rowPools = ArrayPool<byte[]>.Shared.Rent(rowCount);
        uint nullCount = 0;
        int totalPoolBytes = 0;

        try
        {
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
                        rowPools[rowIndex] = [];
                        nullCount++;
                        continue;
                    }

                    DataValue[] elements = value.AsArray(pageStore);
                    using MemoryStream rowStream = new();
                    using BinaryWriter rowWriter = new(rowStream, System.Text.Encoding.UTF8, leaveOpen: true);

                    foreach (DataValue element in elements)
                    {
                        DataValueWriter.WriteDataValue(rowWriter, element, pageStore);
                    }

                    rowWriter.Flush();
                    rowPools[rowIndex] = rowStream.ToArray();
                    totalPoolBytes += rowPools[rowIndex].Length;
                }
            }

            DatumZoneMap zoneMap = new(nullCount);
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
                    byte[] pool = rowPools[rowIndex];
                    runningOffset += (uint)pool.Length;
                    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offsetWrite), runningOffset);
                    offsetWrite += 4;

                    if (pool.Length > 0)
                    {
                        Buffer.BlockCopy(pool, 0, raw, poolWrite, pool.Length);
                        poolWrite += pool.Length;
                    }
                }

                (byte[] compressed, int compressedLength) = DatumCompressor.Compress(raw.AsSpan(0, rawLength), DatumCompression.Zstd);

                return new DatumEncodedPage(compressed, compressedLength, DatumEncoding.VariableDataValue, DatumCompression.Zstd, rawLength, zoneMap);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(rowPools, clearArray: true);
        }
    }
}
