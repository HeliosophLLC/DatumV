using System.Buffers.Binary;
using DatumIngest.Functions.Image;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Serialization.Fits;

/// <summary>
/// Decodes the pixel data section of a FITS image HDU into two
/// representations: a scientific <c>Float32</c> array (BSCALE/BZERO
/// applied so the values are in physical units), and an optional
/// PNG-encoded grayscale preview suitable for browser display.
/// </summary>
/// <remarks>
/// <para>
/// All multi-byte FITS values are big-endian on disk; this reader
/// converts them to host-order during the scale-and-convert pass.
/// </para>
/// <para>
/// The PNG preview is a linear min/max stretch to 8-bit grayscale. It's
/// produced only for 2-D HDUs (NAXIS == 2) — 1-D spectra and higher-rank
/// cubes need user-defined projection to be meaningfully displayable, so
/// for those the reader leaves the image cell null. Scientific data is
/// always exposed in the <c>sci</c> column.
/// </para>
/// </remarks>
internal static class FitsImageReader
{
    /// <summary>
    /// Reads the HDU's data section from <paramref name="stream"/>, returns
    /// the scientific Float32 representation and an optional displayable
    /// PNG preview. The caller is responsible for positioning the stream
    /// at the HDU's data offset before calling.
    /// </summary>
    internal static (DataValue Sci, DataValue Image) ReadImage(
        FitsHduDescriptor hdu,
        Stream stream,
        IValueStore arena)
    {
        long pixelCountLong = hdu.PixelCount;
        if (pixelCountLong == 0)
        {
            return (DataValue.NullArrayOf(DataKind.Float32), DataValue.Null(DataKind.Image));
        }
        if (pixelCountLong > int.MaxValue)
        {
            throw new NotSupportedException(
                $"FITS image with {pixelCountLong:N0} pixels exceeds the int32 cap on a single .NET array. " +
                "Read it as a typed column with shape metadata once typed-tensor surfaces land.");
        }
        int pixelCount = (int)pixelCountLong;

        float[] sciData = new float[pixelCount];
        DecodePixels(hdu, stream, sciData);

        DataValue sci = DataValue.FromArenaArray<float>(sciData, DataKind.Float32, arena);

        DataValue image = hdu.NAxis == 2
            ? EncodeGrayscalePreview(sciData, width: hdu.NAxisN[0], height: hdu.NAxisN[1], arena)
            : DataValue.Null(DataKind.Image);

        return (sci, image);
    }

    /// <summary>
    /// Reads <paramref name="output"/>.Length pixels from <paramref name="stream"/>,
    /// decoding each per <paramref name="hdu"/>.BitPix and applying the linear
    /// BSCALE/BZERO transform <c>physical = BZERO + BSCALE * raw</c>.
    /// </summary>
    private static void DecodePixels(FitsHduDescriptor hdu, Stream stream, Span<float> output)
    {
        int bytesPerPixel = hdu.BytesPerPixel;
        long byteCount = (long)output.Length * bytesPerPixel;
        byte[] buffer = new byte[byteCount];
        FitsHduDescriptor.ReadExactly(stream, buffer);

        double scale = hdu.BScale;
        double zero = hdu.BZero;
        ReadOnlySpan<byte> bytes = buffer;

        switch (hdu.BitPix)
        {
            case 8:
                // 8-bit unsigned by FITS convention.
                for (int i = 0; i < output.Length; i++)
                {
                    output[i] = (float)(zero + scale * bytes[i]);
                }
                break;
            case 16:
                for (int i = 0; i < output.Length; i++)
                {
                    short raw = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(i * 2, 2));
                    output[i] = (float)(zero + scale * raw);
                }
                break;
            case 32:
                for (int i = 0; i < output.Length; i++)
                {
                    int raw = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(i * 4, 4));
                    output[i] = (float)(zero + scale * raw);
                }
                break;
            case 64:
                for (int i = 0; i < output.Length; i++)
                {
                    long raw = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(i * 8, 8));
                    output[i] = (float)(zero + scale * raw);
                }
                break;
            case -32:
                for (int i = 0; i < output.Length; i++)
                {
                    float raw = BinaryPrimitives.ReadSingleBigEndian(bytes.Slice(i * 4, 4));
                    output[i] = (float)(zero + scale * raw);
                }
                break;
            case -64:
                for (int i = 0; i < output.Length; i++)
                {
                    double raw = BinaryPrimitives.ReadDoubleBigEndian(bytes.Slice(i * 8, 8));
                    output[i] = (float)(zero + scale * raw);
                }
                break;
            default:
                throw new NotSupportedException(
                    $"FITS BITPIX={hdu.BitPix} is not a supported image format. " +
                    "Supported: 8, 16, 32, 64 (integer), -32, -64 (IEEE float).");
        }
    }

    /// <summary>
    /// Min/max linear-stretches <paramref name="pixels"/> to 8-bit grayscale and
    /// PNG-encodes the result. NaN values become 0 (visually black); when
    /// every finite pixel has the same value, the whole frame becomes mid-gray.
    /// </summary>
    private static DataValue EncodeGrayscalePreview(
        ReadOnlySpan<float> pixels,
        int width,
        int height,
        IValueStore arena)
    {
        // First pass: find finite min/max.
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < pixels.Length; i++)
        {
            float v = pixels[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        bool degenerate = float.IsPositiveInfinity(min) || min == max;
        float range = degenerate ? 1f : max - min;

        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        IntPtr pixelPointer = bitmap.GetPixels();

        unsafe
        {
            byte* destination = (byte*)pixelPointer;
            for (int i = 0; i < pixels.Length; i++)
            {
                float v = pixels[i];
                byte gray;
                if (degenerate)
                {
                    gray = 128;
                }
                else if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    gray = 0;
                }
                else
                {
                    float normalized = (v - min) / range;
                    int clamped = (int)Math.Clamp(normalized * 255f, 0f, 255f);
                    gray = (byte)clamped;
                }
                destination[i * 4]     = gray;
                destination[i * 4 + 1] = gray;
                destination[i * 4 + 2] = gray;
                destination[i * 4 + 3] = 255;
            }
        }

        byte[] pngBytes = ImageEncoder.Encode(bitmap, SKEncodedImageFormat.Png);
        return DataValue.FromImage(pngBytes, arena);
    }
}
