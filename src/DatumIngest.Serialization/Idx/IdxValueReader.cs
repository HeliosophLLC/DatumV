using System.Buffers.Binary;
using DatumIngest.Model;
using SkiaSharp;
using DatumIngest.Functions.Image;

namespace DatumIngest.Serialization.Idx;

/// <summary>
/// Creates <see cref="DataValue"/> instances from raw IDX item bytes based on
/// the data type and dimensionality described by the header.
/// </summary>
internal static class IdxValueReader
{
    /// <summary>
    /// Creates a <see cref="DataValue"/> from the raw item bytes.
    /// </summary>
    internal static DataValue CreateDataValue(IdxHeader header, byte[] itemBuffer, IValueStore store)
    {
        if (header.IsUInt8)
        {
            return header.ItemDimensionCount switch
            {
                0 => DataValue.FromUInt8(itemBuffer[0]),
                1 => DataValue.FromByteArray(itemBuffer.ToArray(), store),
                _ => CreateImageFromUInt8(header, itemBuffer, store),
            };
        }

        return header.ItemDimensionCount switch
        {
            0 => DataValue.FromFloat32(ReadScalarElement(header.TypeCode, itemBuffer)),
            1 => DataValue.FromVector(ReadFloatArray(header, itemBuffer), store),
            2 => DataValue.FromMatrix(
                ReadFloatArray(header, itemBuffer),
                header.ItemShape[0],
                header.ItemShape[1],
                store),
            _ => DataValue.FromTensor(
                ReadFloatArray(header, itemBuffer),
                header.ItemShape.ToArray(),
                store),
        };
    }

    /// <summary>
    /// Reads a single numeric element from the buffer and returns it as a float.
    /// All multi-byte values are big-endian per the IDX specification.
    /// </summary>
    internal static float ReadScalarElement(byte typeCode, ReadOnlySpan<byte> buffer)
    {
        return typeCode switch
        {
            0x08 => buffer[0],
            0x09 => (sbyte)buffer[0],
            0x0B => BinaryPrimitives.ReadInt16BigEndian(buffer),
            0x0C => BinaryPrimitives.ReadInt32BigEndian(buffer),
            0x0D => BinaryPrimitives.ReadSingleBigEndian(buffer),
            0x0E => (float)BinaryPrimitives.ReadDoubleBigEndian(buffer),
            _ => throw new InvalidOperationException($"Unsupported IDX type code: 0x{typeCode:X2}.")
        };
    }

    /// <summary>
    /// Reads all elements from the item buffer into a float array.
    /// </summary>
    internal static float[] ReadFloatArray(IdxHeader header, ReadOnlySpan<byte> itemBuffer)
    {
        int elementCount = header.ItemElementCount;
        int elementSize = header.ElementByteSize;
        float[] result = new float[elementCount];

        for (int i = 0; i < elementCount; i++)
        {
            result[i] = ReadScalarElement(
                header.TypeCode,
                itemBuffer.Slice(i * elementSize, elementSize));
        }

        return result;
    }

    /// <summary>
    /// Encodes uint8 pixel data as a PNG and stores the bytes in the arena so the
    /// image flows through the same byte-span accessor path as ZIP/CSV image columns.
    /// IDX pixels are never the target storage format on disk — the datum writer needs
    /// encoded bytes regardless, so we pay the encode cost once here rather than
    /// re-encoding at flush time.
    /// </summary>
    private static DataValue CreateImageFromUInt8(IdxHeader header, byte[] pixelData, IValueStore store)
    {
        ReadOnlySpan<int> shape = header.ItemShape;
        int height = shape[0];
        int width = shape[1];

        int channels = shape.Length >= 3 ? shape[2] : 1;
        for (int d = 3; d < shape.Length; d++)
            channels *= shape[d];

        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        IntPtr pixelPointer = bitmap.GetPixels();

        unsafe
        {
            byte* destination = (byte*)pixelPointer;
            int pixelCount = width * height;

            switch (channels)
            {
                case 1:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        byte gray = pixelData[i];
                        destination[i * 4] = gray;
                        destination[i * 4 + 1] = gray;
                        destination[i * 4 + 2] = gray;
                        destination[i * 4 + 3] = 255;
                    }
                    break;

                case 3:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        destination[i * 4] = pixelData[i * 3];
                        destination[i * 4 + 1] = pixelData[i * 3 + 1];
                        destination[i * 4 + 2] = pixelData[i * 3 + 2];
                        destination[i * 4 + 3] = 255;
                    }
                    break;

                case 4:
                    new ReadOnlySpan<byte>(pixelData, 0, pixelCount * 4)
                        .CopyTo(new Span<byte>(destination, pixelCount * 4));
                    break;

                default:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int baseOffset = i * channels;
                        destination[i * 4] = pixelData[baseOffset];
                        destination[i * 4 + 1] = channels > 1 ? pixelData[baseOffset + 1] : (byte)0;
                        destination[i * 4 + 2] = channels > 2 ? pixelData[baseOffset + 2] : (byte)0;
                        destination[i * 4 + 3] = channels > 3 ? pixelData[baseOffset + 3] : (byte)255;
                    }
                    break;
            }
        }

        byte[] pngBytes = ImageEncoder.Encode(bitmap, SKEncodedImageFormat.Png);
        return DataValue.FromImage(pngBytes, store);
    }
}
