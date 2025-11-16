namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Computes the standard deviation of pixel values per channel.
/// <c>image_pixel_std(img)</c> returns the overall standard deviation across all channels as a scalar.
/// <c>image_pixel_std(img, channels)</c> returns per-channel standard deviations as a vector,
/// where <c>channels</c> is a vector of channel indices (0=R, 1=G, 2=B, 3=A).
/// </summary>
public sealed class ImagePixelStandardDeviationFunction : IScalarFunction, ICostAwareFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "image_pixel_std";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    /// <remarks>
    /// Pipeline form supports only the no-aux-args overall std (Float32). The
    /// channel-vector form is reachable only via the standalone Execute path,
    /// which can return Vector.
    /// </remarks>
    public DataKind ResultKind => DataKind.Float32;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("image_pixel_std() requires 1 or 2 arguments: image[, channels].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_pixel_std() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.Vector)
        {
            throw new ArgumentException(
                $"image_pixel_std() second argument (channels) must be Vector, got {argumentKinds[1]}.");
        }

        return argumentKinds.Length == 1 ? DataKind.Float32 : DataKind.Vector;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                "image_pixel_std() in pipeline form does not accept auxiliary arguments " +
                "(channel-index variant returns Vector and is only available in standalone form).");
        }
    }

    /// <inheritdoc cref="IImagePipelineSink.Reduce" />
    public DataValue Reduce(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs, IValueStore targetStore)
    {
        using SKBitmap? converted = input.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(input)
            : null;
        SKBitmap rgba = converted ?? input;

        int totalPixels = rgba.Width * rgba.Height;
        nint pixelPointer = rgba.GetPixels();

        return ComputeOverallStandardDeviation(pixelPointer, totalPixels);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "image_pixel_std() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    private static DataValue ComputeOverallStandardDeviation(nint pixelPointer, int totalPixels)
    {
        int totalElements = totalPixels * 4;
        double sum = 0.0;

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < totalElements; i++)
            {
                sum += pixels[i];
            }

            double mean = sum / totalElements;
            double sumSquaredDifferences = 0.0;

            for (int i = 0; i < totalElements; i++)
            {
                double difference = pixels[i] - mean;
                sumSquaredDifferences += difference * difference;
            }

            double variance = sumSquaredDifferences / totalElements;
            float standardDeviation = (float)System.Math.Sqrt(variance);
            return DataValue.FromFloat32(standardDeviation);
        }
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

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
