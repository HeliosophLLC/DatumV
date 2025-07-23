namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Detects image blurriness using the Laplacian variance method.
/// <c>detect_blur(img)</c> returns a scalar representing the variance of
/// the Laplacian-filtered image. Higher values indicate sharper images;
/// lower values indicate blurrier images.
/// </summary>
public sealed class DetectBlurFunction : IScalarFunction
{
    // ITU-R BT.601 luminance weights for grayscale conversion
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    /// <inheritdoc />
    public string Name => "detect_blur";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("detect_blur() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"detect_blur() requires Image or UInt8Array, got {argumentKinds[0]}.");
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
        SKBitmap bitmap = inputHandle.GetBitmap("detect_blur");

        using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888
            ? bitmap
            : ConvertToRgba8888(bitmap);

        int width = rgba.Width;
        int height = rgba.Height;
        nint pixelPointer = rgba.GetPixels();

        // Convert to grayscale luminance buffer
        float[] grayscale = new float[width * height];

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < width * height; i++)
            {
                int offset = i * 4;
                grayscale[i] = pixels[offset] * RedWeight
                             + pixels[offset + 1] * GreenWeight
                             + pixels[offset + 2] * BlueWeight;
            }
        }

        // Apply 3×3 Laplacian kernel: [0, 1, 0; 1, -4, 1; 0, 1, 0]
        // Only process interior pixels (skip 1-pixel border)
        int interiorCount = (width - 2) * (height - 2);

        if (interiorCount <= 0)
        {
            return DataValue.FromScalar(0f);
        }

        double laplacianSum = 0.0;
        double laplacianSumSquared = 0.0;

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                float laplacian = grayscale[(y - 1) * width + x]       // top
                                + grayscale[(y + 1) * width + x]       // bottom
                                + grayscale[y * width + (x - 1)]       // left
                                + grayscale[y * width + (x + 1)]       // right
                                - 4f * grayscale[y * width + x];       // center

                laplacianSum += laplacian;
                laplacianSumSquared += laplacian * (double)laplacian;
            }
        }

        double mean = laplacianSum / interiorCount;
        double variance = (laplacianSumSquared / interiorCount) - (mean * mean);

        return DataValue.FromScalar((float)variance);
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }
}
