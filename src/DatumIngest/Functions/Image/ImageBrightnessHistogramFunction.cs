namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Computes a 256-bin brightness histogram of an image using BT.601 luminance weights.
/// <c>image_brightness_histogram(img)</c> returns a 256-element vector where each element
/// is the count of pixels whose luminance falls into that bin (0–255).
/// </summary>
public sealed class ImageBrightnessHistogramFunction : IScalarFunction
{
    // ITU-R BT.601 luminance weights (same as GrayscaleImageFunction)
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    private const int BinCount = 256;

    /// <inheritdoc />
    public string Name => "image_brightness_histogram";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("image_brightness_histogram() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_brightness_histogram() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap bitmap = inputHandle.GetBitmap("image_brightness_histogram");

        using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888
            ? bitmap
            : ConvertToRgba8888(bitmap);

        int totalPixels = rgba.Width * rgba.Height;
        nint pixelPointer = rgba.GetPixels();

        float[] histogram = new float[BinCount];

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < totalPixels; i++)
            {
                int offset = i * 4;
                float luminance = pixels[offset] * RedWeight
                                + pixels[offset + 1] * GreenWeight
                                + pixels[offset + 2] * BlueWeight;

                // Clamp to [0, 255] and quantize to bin index
                int bin = (int)luminance;
                if (bin < 0) bin = 0;
                else if (bin >= BinCount) bin = BinCount - 1;

                histogram[bin]++;
            }
        }

        return DataValue.FromVector(histogram);
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }
}
