namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Decodes an image into an RGB float tensor in HWC (height, width, channels) layout.
/// <c>image_to_tensor_hwc(img)</c> accepts Image or UInt8Array and returns a
/// Tensor with shape <c>[height, width, 3]</c>. Pixel values are floats in
/// the 0–255 range. The alpha channel is discarded.
/// </summary>
public sealed class ImageToTensorHwcFunction : IScalarFunction, ICostAwareFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "image_to_tensor_hwc";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Tensor;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("image_to_tensor_hwc() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_to_tensor_hwc() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Tensor;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"image_to_tensor_hwc() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
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
        const int channelCount = 3;

        float[] pixels = new float[height * width * channelCount];
        nint pixelPtr = rgba.GetPixels();

        unsafe
        {
            byte* source = (byte*)pixelPtr;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int sourceOffset = (row * width + col) * 4;
                    int targetOffset = (row * width + col) * channelCount;

                    pixels[targetOffset] = source[sourceOffset];
                    pixels[targetOffset + 1] = source[sourceOffset + 1];
                    pixels[targetOffset + 2] = source[sourceOffset + 2];
                }
            }
        }

        return DataValue.FromTensor(pixels, [height, width, channelCount], targetStore);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "image_to_tensor_hwc() must be lowered to a FusedImagePipelineExpression at plan time " +
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
