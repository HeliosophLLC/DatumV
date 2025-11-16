namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Decodes an image into an RGB float tensor in CHW (channels, height, width) layout.
/// <c>image_to_tensor_chw(img)</c> accepts Image or UInt8Array and returns a
/// Tensor with shape <c>[3, height, width]</c>. Pixel values are floats in
/// the 0–255 range. The alpha channel is discarded.
/// </summary>
public sealed class ImageToTensorChwFunction : IScalarFunction, ICostAwareFunction, IImagePipelineSink
{
    /// <inheritdoc />
    public string Name => "image_to_tensor_chw";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc cref="IImagePipelineSink.ResultKind" />
    public DataKind ResultKind => DataKind.Tensor;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("image_to_tensor_chw() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_to_tensor_chw() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Tensor;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length != 0)
        {
            throw new ArgumentException(
                $"image_to_tensor_chw() takes no auxiliary arguments in pipeline form; got {auxiliaryKinds.Length}.");
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
        int planeSize = height * width;
        const int channelCount = 3;

        float[] pixels = new float[channelCount * planeSize];
        nint pixelPtr = rgba.GetPixels();

        unsafe
        {
            byte* source = (byte*)pixelPtr;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int sourceOffset = (row * width + col) * 4;
                    int pixelIndex = row * width + col;

                    pixels[pixelIndex] = source[sourceOffset];
                    pixels[planeSize + pixelIndex] = source[sourceOffset + 1];
                    pixels[2 * planeSize + pixelIndex] = source[sourceOffset + 2];
                }
            }
        }

        return DataValue.FromTensor(pixels, [channelCount, height, width], targetStore);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "image_to_tensor_chw() must be lowered to a FusedImagePipelineExpression at plan time " +
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
