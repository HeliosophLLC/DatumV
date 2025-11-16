namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Computes the mean pixel value per channel.
/// <c>image_pixel_mean(img)</c> returns the overall mean across all channels as a scalar.
/// <c>image_pixel_mean(img, channels)</c> returns per-channel means as a vector,
/// where <c>channels</c> is a vector of channel indices (0=R, 1=G, 2=B, 3=A).
/// </summary>
public sealed class ImagePixelMeanFunction : IScalarFunction, ICostAwareFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "image_pixel_mean";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    /// <remarks>
    /// Pipeline form supports only the no-aux-args overall mean (Float32). The
    /// channel-vector form is reachable only via the standalone Execute path,
    /// which can return Vector.
    /// </remarks>
    public DataKind ResultKind => DataKind.Float32;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("image_pixel_mean() requires 1 or 2 arguments: image[, channels].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_pixel_mean() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.Vector)
        {
            throw new ArgumentException(
                $"image_pixel_mean() second argument (channels) must be Vector, got {argumentKinds[1]}.");
        }

        return argumentKinds.Length == 1 ? DataKind.Float32 : DataKind.Vector;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        // Pipeline form supports only the no-channels overall-mean variant — the
        // result kind is fixed at lowering time and can't switch between Float32
        // and Vector based on the channel arg.
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                "image_pixel_mean() in pipeline form does not accept auxiliary arguments " +
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

        return ComputeOverallMean(pixelPointer, totalPixels);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "image_pixel_mean() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    private static DataValue ComputeOverallMean(nint pixelPointer, int totalPixels)
    {
        double sum = 0.0;
        int totalElements = totalPixels * 4;

        unsafe
        {
            byte* pixels = (byte*)pixelPointer;

            for (int i = 0; i < totalElements; i++)
            {
                sum += pixels[i];
            }
        }

        float mean = (float)(sum / totalElements);
        return DataValue.FromFloat32(mean);
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
