using System.Buffers.Binary;
using System.Text;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Indexing;
using DatumIngest.IO;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.Array"/> column page produced by <c>ArrayColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout:
/// <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// The pool contains back-to-back element sequences for each row, where each element is
/// serialized in the <c>IndexWriter.WriteDataValue</c> wire format.
/// Null rows contribute zero bytes to the pool.
/// </remarks>
internal sealed class ArrayColumnDecoder : DatumColumnDecoder
{
    /// <inheritdoc/>
    public override DataValue[] Decode(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        int offsetsStart = bitmapByteCount;
        int poolStart = offsetsStart + (rowCount + 1) * 4;

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + rowIndex * 4));
            uint end   = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + (rowIndex + 1) * 4));

            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(DataKind.Array);
                continue;
            }

            int poolByteCount = (int)(end - start);
            using MemoryStream rowStream = new(raw, poolStart + (int)start, poolByteCount);
            using BinaryReader rowReader = new(rowStream, System.Text.Encoding.UTF8, leaveOpen: true);

            List<DataValue> elements = new();
            while (rowStream.Position < poolByteCount)
            {
                elements.Add(DataValueReader.ReadDataValue(rowReader));
            }

            // Infer element kind from the first element; default to Scalar for empty arrays.
            DataKind elementKind = elements.Count > 0 ? elements[0].Kind : DataKind.Float32;
            result[rowIndex] = DataValue.FromArray(elementKind, elements.ToArray());
        }

        return result;
    }
}
