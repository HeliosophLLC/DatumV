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
public sealed class ImageToTensorHwcFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "image_to_tensor_hwc";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

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
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Tensor);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap bitmap = inputHandle.GetBitmap("image_to_tensor_hwc");

        using SKBitmap? converted = bitmap.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(bitmap)
            : null;
        SKBitmap rgba = converted ?? bitmap;

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
                    int sourceOffset = (row * width + col) * 4; // RGBA stride
                    int targetOffset = (row * width + col) * channelCount;

                    pixels[targetOffset] = source[sourceOffset];         // R
                    pixels[targetOffset + 1] = source[sourceOffset + 1]; // G
                    pixels[targetOffset + 2] = source[sourceOffset + 2]; // B
                }
            }
        }

        return DataValue.FromTensor(pixels, [height, width, channelCount]);
    }

    private static SKBitmap ConvertToRgba8888(SKBitmap source)
    {
        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        using SKCanvas canvas = new(converted);
        canvas.DrawBitmap(source, 0, 0);

        return converted;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Tensor);
        }

        ImageHandle inputHandle = input.GetImageHandle(frame.Source, frame.SidecarRegistry);
        SKBitmap bitmap = inputHandle.GetBitmap("image_to_tensor_hwc");

        using SKBitmap? converted = bitmap.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(bitmap)
            : null;
        SKBitmap rgba = converted ?? bitmap;

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
                    int sourceOffset = (row * width + col) * 4; // RGBA stride
                    int targetOffset = (row * width + col) * channelCount;

                    pixels[targetOffset] = source[sourceOffset];         // R
                    pixels[targetOffset + 1] = source[sourceOffset + 1]; // G
                    pixels[targetOffset + 2] = source[sourceOffset + 2]; // B
                }
            }
        }

        return DataValue.FromTensor(pixels, [height, width, channelCount], frame.Target);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
