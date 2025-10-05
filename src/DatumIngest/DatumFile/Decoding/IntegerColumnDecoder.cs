using System.Buffers.Binary;
using DatumIngest.DatumFile.Compression;
using DatumIngest.DatumFile.Encoding;
using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Decodes fixed-width integer column pages (UInt8, Int8, Int16, UInt16, Int32, UInt32, Int64,
/// UInt64) produced by <see cref="IntegerColumnEncoder"/>.
/// </summary>
/// <remarks>
/// Uncompressed layout: <c>nullBitmap[ceil(N/8)] | values[N * bytesPerElement]</c>.
/// The stored zeroed bytes for null rows are never exposed to callers.
/// </remarks>
internal sealed class IntegerColumnDecoder : DatumColumnDecoder
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
        int bytesPerElement = IntegerColumnEncoder.BytesPerElement(descriptor.Kind);

        DataValue[] result = new DataValue[rowCount];

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                result[rowIndex] = DataValue.Null(descriptor.Kind);
            }
            else
            {
                int offset = bitmapByteCount + rowIndex * bytesPerElement;
                result[rowIndex] = ReadValue(descriptor.Kind, raw, offset);
            }
        }

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
        byte[] raw;
        if (decompressedBuffer is not null)
        {
            DecompressPayloadInto(payload, effectiveLength, decompressedBuffer, uncompressedByteLength, compression);
            raw = decompressedBuffer;
        }
        else
        {
            raw = DecompressPayload(payload, effectiveLength, uncompressedByteLength, compression);
        }
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);
        int bytesPerElement = IntegerColumnEncoder.BytesPerElement(descriptor.Kind);

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(descriptor.Kind);
            }
            else
            {
                int offset = bitmapByteCount + rowIndex * bytesPerElement;
                target[rowIndex] = ReadValue(descriptor.Kind, raw, offset);
            }
        }
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
        Arena arena)
    {
        byte[] raw = DecompressPayload(payload, uncompressedByteLength, compression);
        int bitmapByteCount = DatumNullBitmap.ByteCount(rowCount);
        DatumNullBitmap nullBitmap = ReadNullBitmap(raw, rowCount);
        int bytesPerElement = IntegerColumnEncoder.BytesPerElement(descriptor.Kind);

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (nullBitmap.IsNull(rowIndex))
            {
                target[rowIndex] = DataValue.Null(descriptor.Kind);
            }
            else
            {
                int offset = bitmapByteCount + rowIndex * bytesPerElement;
                target[rowIndex] = ReadValue(descriptor.Kind, raw, offset);
            }
        }
    }

    private static DataValue ReadValue(DataKind kind, byte[] buffer, int offset)
    {
        return kind switch
        {
            DataKind.UInt8 => DataValue.FromUInt8(buffer[offset]),
            DataKind.Int8 => DataValue.FromInt8(unchecked((sbyte)buffer[offset])),
            DataKind.Int16 => DataValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset))),
            DataKind.UInt16 => DataValue.FromUInt16(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset))),
            DataKind.Int32 => DataValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset))),
            DataKind.UInt32 => DataValue.FromUInt32(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset))),
            DataKind.Int64 => DataValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset))),
            DataKind.UInt64 => DataValue.FromUInt64(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset))),
            _ => throw new NotSupportedException($"IntegerColumnDecoder does not support DataKind.{kind}.")
        };
    }
}
