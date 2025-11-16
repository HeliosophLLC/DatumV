namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Computes the mean brightness (luminance) of an image using BT.601 weights.
/// <c>image_brightness_mean(img)</c> returns a scalar in the range 0–255.
/// </summary>
public sealed class ImageBrightnessMeanFunction : IScalarFunction, ICostAwareFunction, IImagePipelineSink
{
    // ITU-R BT.601 luminance weights (same as GrayscaleImageFunction)
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    /// <inheritdoc />
    public string Name => "image_brightness_mean";

    /// <inheritdoc />
    public int QueryUnitCost => 10;

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Float32;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("image_brightness_mean() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_brightness_mean() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"image_brightness_mean() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
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
        }

        float mean = (float)(sum / totalPixels);
        return DataValue.FromFloat32(mean);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "image_brightness_mean() must be lowered to a FusedImagePipelineExpression at plan time " +
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
