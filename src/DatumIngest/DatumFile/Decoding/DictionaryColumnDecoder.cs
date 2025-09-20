using System.Text;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes a dictionary-encoded column page produced by <c>DictionaryColumnEncoder</c>.
/// </summary>
/// <remarks>
/// Uncompressed layout:
/// <list type="number">
///   <item><c>nullBitmap[ceil(N/8)]</c></item>
///   <item><c>dictionaryEntryCount:uint16</c></item>
///   <item>One <c>IndexWriter.WriteDataValue</c> serialized entry per distinct value.</item>
///   <item>
///     <c>codes:byte[N]</c> when <c>dictionaryEntryCount ≤ 255</c>,
///     or <c>codes:uint16[N]</c> when <c>dictionaryEntryCount ≤ 65 535</c>.
///     Code 0 is the null sentinel and is never looked up in the dictionary.
///     Non-null codes are 1-based dictionary indices.
///   </item>
/// </list>
/// </remarks>
internal sealed class DictionaryColumnDecoder : DatumColumnDecoder
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
        DataValue[] result = new DataValue[rowCount];
        DecodeCore(payload, payload.Length, compression, uncompressedByteLength, rowCount, descriptor, result, decompressedBuffer: null);
        return result;
    }

    /// <inheritdoc/>
    public override void DecodeInto(
        byte[] payload,
        DatumEncoding encoding,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DatumDecoderContext context,
        DataValue[] target,
        int payloadLength = -1,
        byte[]? decompressedBuffer = null)
    {
        int effectiveLength = payloadLength >= 0 ? payloadLength : payload.Length;
        DecodeCore(payload, effectiveLength, compression, uncompressedByteLength, rowCount, descriptor, target, decompressedBuffer);
    }

    private void DecodeCore(
        byte[] payload,
        int payloadLength,
        DatumCompression compression,
        int uncompressedByteLength,
        int rowCount,
        DatumColumnDescriptor descriptor,
        DataValue[] target,
        byte[]? decompressedBuffer)
    {
        byte[] raw;
        if (decompressedBuffer is not null)
        {
            DecompressPayloadInto(payload, payloadLength, decompressedBuffer, uncompressedByteLength, compression);
            raw = decompressedBuffer;
        }
        else
        {
            raw = DecompressPayload(payload, payloadLength, uncompressedByteLength, compression);
        }

        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);

        using MemoryStream stream = new(raw, bitmapByteCount, uncompressedByteLength - bitmapByteCount);
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        ushort dictionaryEntryCount = reader.ReadUInt16();
        DataValue[] dictionary = new DataValue[dictionaryEntryCount];
        for (int entryIndex = 0; entryIndex < dictionaryEntryCount; entryIndex++)
        {
            dictionary[entryIndex] = IndexReader.ReadDataValue(reader);
        }

        bool wideCode = dictionaryEntryCount > 255;
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                int code = wideCode ? reader.ReadUInt16() : reader.ReadByte();
                _ = code; // consume but discard — null bitmap is authoritative
                target[rowIndex] = DataValue.Null(descriptor.Kind);
            }
            else
            {
                int code = wideCode ? reader.ReadUInt16() : reader.ReadByte();
                // Codes are 1-based; subtract 1 to get the dictionary index.
                target[rowIndex] = dictionary[code - 1];
            }
        }
    }
}
