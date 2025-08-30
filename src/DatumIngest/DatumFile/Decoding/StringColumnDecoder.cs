using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes <see cref="DataKind.String"/> and <see cref="DataKind.JsonValue"/> column pages
/// produced by <c>StringColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout:
/// <c>nullBitmap[ceil(N/8)] | offsets:uint32[N+1] | pool:byte[offsets[N]]</c>.
/// The pool contains back-to-back UTF-8 byte sequences; <c>offsets[i]</c> and
/// <c>offsets[i+1]</c> delimit the bytes for row <c>i</c>.
/// Null rows and empty strings both yield zero-length pool slices;
/// the null bitmap is authoritative.
/// </remarks>
internal sealed class StringColumnDecoder : DatumColumnDecoder
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

        // Read N+1 uint32 offsets.
        int offsetsStart = bitmapByteCount;
        int poolStart = offsetsStart + (rowCount + 1) * 4;

        bool isJson = descriptor.Kind == DataKind.JsonValue;

        DataValue[] result = new DataValue[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + rowIndex * 4));
            uint end   = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + (rowIndex + 1) * 4));

            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(isJson ? DataKind.JsonValue : DataKind.String);
            }
            else
            {
                string text = System.Text.Encoding.UTF8.GetString(raw, poolStart + (int)start, (int)(end - start));
                result[rowIndex] = isJson ? DataValue.FromJsonValue(text) : DataValue.FromString(text);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public override void DecodeIntoColumn(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context,
        DataValue[] target,
        StringArena stringArena,
        DataArena dataArena)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        int offsetsStart = bitmapByteCount;
        int poolStart = offsetsStart + (rowCount + 1) * 4;

        DataKind nullKind = descriptor.Kind == DataKind.JsonValue ? DataKind.JsonValue : DataKind.String;

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(nullKind);
            }
            else
            {
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + rowIndex * 4));
                uint end   = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offsetsStart + (rowIndex + 1) * 4));
                ReadOnlySpan<byte> utf8Bytes = raw.AsSpan(poolStart + (int)start, (int)(end - start));
                (int arenaOffset, int arenaLength) = stringArena.Append(utf8Bytes);
                target[rowIndex] = DataValue.FromStringSlice(arenaOffset, arenaLength);
            }
        }
    }
}
