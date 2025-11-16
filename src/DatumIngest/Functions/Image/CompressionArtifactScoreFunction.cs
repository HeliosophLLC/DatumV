namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Detects JPEG compression artifacts by measuring discontinuities at 8×8 block
/// boundaries. <c>compression_artifact_score(img)</c> returns a scalar where higher
/// values indicate more severe compression artifacts (blockiness).
/// </summary>
public sealed class CompressionArtifactScoreFunction : IScalarFunction, ICostAwareFunction, IImagePipelineSink
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

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Float32;

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

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"compression_artifact_score() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
        }
    }

    /// <inheritdoc cref="IImagePipelineSink.Reduce" />
    public DataValue Reduce(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs, IValueStore targetStore)
    {
        using SKBitmap? converted = input.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(input)
            : null;
        SKBitmap rgba = converted ?? input;

        int width = rgba.Width;
        int height = rgba.Height;

        if (width < BlockSize * 2 || height < BlockSize * 2)
        {
            return DataValue.FromFloat32(0f);
        }

        nint pixelPointer = rgba.GetPixels();

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

        double boundaryGradientSum = 0.0;
        int boundaryCount = 0;
        double interiorGradientSum = 0.0;
        int interiorCount = 0;

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

        for (int x = 1; x < width; x++)
        {
            if (x % BlockSize == 0) continue;

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
            if (y % BlockSize == 0) continue;

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
            return DataValue.FromFloat32(0f);
        }

        double averageBoundaryGradient = boundaryGradientSum / boundaryCount;
        double averageInteriorGradient = interiorGradientSum / interiorCount;

        float score = averageInteriorGradient > 0.001
            ? (float)System.Math.Clamp(
                (averageBoundaryGradient - averageInteriorGradient) / averageInteriorGradient,
                0.0, 1.0)
            : 0f;

        return DataValue.FromFloat32(score);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "compression_artifact_score() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

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

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
