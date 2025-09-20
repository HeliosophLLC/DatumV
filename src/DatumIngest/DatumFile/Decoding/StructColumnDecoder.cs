using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a <see cref="DataKind.Struct"/> column page produced by <c>StructColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout:
/// <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// The pool contains back-to-back field sequences for each row, where each field is
/// serialized in the <c>IndexWriter.WriteDataValue</c> wire format.
/// Null rows contribute zero bytes to the pool.
/// </remarks>
internal sealed class StructColumnDecoder : DatumColumnDecoder
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
                result[rowIndex] = DataValue.Null(DataKind.Struct);
                continue;
            }

            int poolByteCount = (int)(end - start);
            using MemoryStream rowStream = new(raw, poolStart + (int)start, poolByteCount);
            using BinaryReader rowReader = new(rowStream, System.Text.Encoding.UTF8, leaveOpen: true);

            List<DataValue> fields = new();
            while (rowStream.Position < poolByteCount)
            {
                fields.Add(IndexReader.ReadDataValue(rowReader));
            }

            result[rowIndex] = DataValue.FromStruct((short)fields.Count, fields.ToArray());
        }

        return result;
    }
}
