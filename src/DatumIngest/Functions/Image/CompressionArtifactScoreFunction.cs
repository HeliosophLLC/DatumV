namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Detects JPEG compression artifacts by measuring discontinuities at 8×8 block
/// boundaries. <c>compression_artifact_score(img)</c> returns a scalar where higher
/// values indicate more severe compression artifacts (blockiness).
/// </summary>
public sealed class CompressionArtifactScoreFunction : IScalarFunction, ICostAwareFunction
{
    // ITU-R BT.601 luminance weights for grayscale conversion
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    private const int BlockSize = 8;

    /// <inheritdoc />
    public string Name => "compression_artifact_score";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("compression_artifact_score() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"compression_artifact_score() requires Image or UInt8Array, got {argumentKinds[0]}.");
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
        SKBitmap bitmap = inputHandle.GetBitmap("compression_artifact_score");

        using SKBitmap? converted = bitmap.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(bitmap)
            : null;
        SKBitmap rgba = converted ?? bitmap;

        int width = rgba.Width;
        int height = rgba.Height;

        if (width < BlockSize * 2 || height < BlockSize * 2)
        {
            return DataValue.FromScalar(0f);
        }

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

        // Measure average absolute gradient difference at block boundaries vs. interior
        double boundaryGradientSum = 0.0;
        int boundaryCount = 0;
        double interiorGradientSum = 0.0;
        int interiorCount = 0;

        // Horizontal block boundaries (vertical lines at x = 8, 16, 24, ...)
        for (int x = BlockSize; x < width - 1; x += BlockSize)
        {
            for (int y = 0; y < height; y++)
            {
                float gradient = System.Math.Abs(
                    grayscale[y * width + x] - grayscale[y * width + (x - 1)]);
                boundaryGradientSum += gradient;
                boundaryCount++;
            }
        }

        // Vertical block boundaries (horizontal lines at y = 8, 16, 24, ...)
        for (int y = BlockSize; y < height - 1; y += BlockSize)
        {
            for (int x = 0; x < width; x++)
            {
                float gradient = System.Math.Abs(
                    grayscale[y * width + x] - grayscale[(y - 1) * width + x]);
                boundaryGradientSum += gradient;
                boundaryCount++;
            }
        }

        // Interior gradients: sample non-boundary positions
        for (int x = 1; x < width; x++)
        {
            if (x % BlockSize == 0) continue; // skip block boundaries

            for (int y = 0; y < height; y++)
            {
                float gradient = System.Math.Abs(
                    grayscale[y * width + x] - grayscale[y * width + (x - 1)]);
                interiorGradientSum += gradient;
                interiorCount++;
            }
        }

        for (int y = 1; y < height; y++)
        {
            if (y % BlockSize == 0) continue; // skip block boundaries

            for (int x = 0; x < width; x++)
            {
                float gradient = System.Math.Abs(
                    grayscale[y * width + x] - grayscale[(y - 1) * width + x]);
                interiorGradientSum += gradient;
                interiorCount++;
            }
        }

        if (boundaryCount == 0 || interiorCount == 0)
        {
            return DataValue.FromScalar(0f);
        }

        double averageBoundaryGradient = boundaryGradientSum / boundaryCount;
        double averageInteriorGradient = interiorGradientSum / interiorCount;

        // Score: ratio of boundary excess over interior, clamped to [0, 1]
        // When boundary gradients are higher than interior, blockiness is present
        float score = averageInteriorGradient > 0.001
            ? (float)System.Math.Clamp(
                (averageBoundaryGradient - averageInteriorGradient) / averageInteriorGradient,
                0.0, 1.0)
            : 0f;

        return DataValue.FromScalar(score);
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);
        return converted;
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
