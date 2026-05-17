using DatumIngest.Functions.Image;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Serialization.Cifar;

/// <summary>
/// Decodes records from the CIFAR binary format used by CIFAR-10 / CIFAR-100.
/// Each record is <c>labelBytes</c> uint8 label bytes followed by 3072 bytes
/// of planar RGB pixel data: 1024 bytes red, 1024 bytes green, 1024 bytes
/// blue, each plane in row-major 32×32 order. The decoder converts the
/// planar layout to interleaved RGBA8888 and PNG-encodes it so the resulting
/// <see cref="DataValue"/> flows through the same byte-span accessor path
/// as ZIP/IDX image columns.
/// </summary>
internal static class CifarRecordReader
{
    /// <summary>CIFAR images are fixed 32×32 pixels.</summary>
    public const int ImageWidth = 32;

    /// <summary>CIFAR images are fixed 32×32 pixels.</summary>
    public const int ImageHeight = 32;

    /// <summary>Each image is 32×32 with three planar channels.</summary>
    public const int ImagePixelBytes = ImageWidth * ImageHeight * 3;

    /// <summary>Record byte size given the leading label-byte count.</summary>
    public static int RecordSize(int labelBytes) => labelBytes + ImagePixelBytes;

    /// <summary>
    /// Encodes the 3072-byte planar RGB payload at <paramref name="planarRgb"/>
    /// as a PNG and stores the bytes via the provided value store, returning
    /// a typed <see cref="DataKind.Image"/> <see cref="DataValue"/>.
    /// </summary>
    public static DataValue EncodeImage(ReadOnlySpan<byte> planarRgb, IValueStore store)
    {
        if (planarRgb.Length != ImagePixelBytes)
        {
            throw new ArgumentException(
                $"CIFAR image payload must be exactly {ImagePixelBytes} bytes; got {planarRgb.Length}.",
                nameof(planarRgb));
        }

        const int planeBytes = ImageWidth * ImageHeight;
        using SKBitmap bitmap = new(ImageWidth, ImageHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        IntPtr pixelPointer = bitmap.GetPixels();

        unsafe
        {
            byte* destination = (byte*)pixelPointer;
            fixed (byte* src = planarRgb)
            {
                byte* redPlane   = src;
                byte* greenPlane = src + planeBytes;
                byte* bluePlane  = src + 2 * planeBytes;

                for (int i = 0; i < planeBytes; i++)
                {
                    destination[i * 4]     = redPlane[i];
                    destination[i * 4 + 1] = greenPlane[i];
                    destination[i * 4 + 2] = bluePlane[i];
                    destination[i * 4 + 3] = 255;
                }
            }
        }

        byte[] pngBytes = ImageEncoder.Encode(bitmap, SKEncodedImageFormat.Png);
        return DataValue.FromImage(pngBytes, store);
    }
}
