namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Computes the standard deviation of brightness (luminance) across all pixels
/// using BT.601 weights. <c>image_brightness_std(img)</c> returns a scalar.
/// </summary>
public sealed class ImageBrightnessStandardDeviationFunction : IScalarFunction
{
    // ITU-R BT.601 luminance weights (same as GrayscaleImageFunction)
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    /// <inheritdoc />
    public string Name => "image_brightness_std";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("image_brightness_std() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_brightness_std() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap bitmap = inputHandle.GetBitmap("image_brightness_std");

        using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888
            ? bitmap
            : ConvertToRgba8888(bitmap);

        int totalPixels = rgba.Width * rgba.Height;
        nint pixelPointer = rgba.GetPixels();

        // Two-pass: compute mean, then variance
        double sum = 0.0;

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < totalPixels; i++)
            {
                int offset = i * 4;
                float luminance = pixels[offset] * RedWeight
                                + pixels[offset + 1] * GreenWeight
                                + pixels[offset + 2] * BlueWeight;
                sum += luminance;
            }

            double mean = sum / totalPixels;
            double sumSquaredDifferences = 0.0;

            for (int i = 0; i < totalPixels; i++)
            {
                int offset = i * 4;
                float luminance = pixels[offset] * RedWeight
                                + pixels[offset + 1] * GreenWeight
                                + pixels[offset + 2] * BlueWeight;
                double difference = luminance - mean;
                sumSquaredDifferences += difference * difference;
            }

            double variance = sumSquaredDifferences / totalPixels;
            float standardDeviation = (float)System.Math.Sqrt(variance);
            return DataValue.FromScalar(standardDeviation);
        }
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }
}
