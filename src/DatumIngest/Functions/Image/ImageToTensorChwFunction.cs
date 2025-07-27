namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Decodes an image into an RGB float tensor in CHW (channels, height, width) layout.
/// <c>image_to_tensor_chw(img)</c> accepts Image or UInt8Array and returns a
/// Tensor with shape <c>[3, height, width]</c>. Pixel values are floats in
/// the 0–255 range. The alpha channel is discarded.
/// </summary>
public sealed class ImageToTensorChwFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "image_to_tensor_chw";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

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
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Tensor);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap bitmap = inputHandle.GetBitmap("image_to_tensor_chw");

        using SKBitmap? converted = bitmap.ColorType != SKColorType.Rgba8888
            ? ConvertToRgba8888(bitmap)
            : null;
        SKBitmap rgba = converted ?? bitmap;

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
                    int sourceOffset = (row * width + col) * 4; // RGBA stride
                    int pixelIndex = row * width + col;

                    pixels[pixelIndex] = source[sourceOffset];                     // R plane
                    pixels[planeSize + pixelIndex] = source[sourceOffset + 1];     // G plane
                    pixels[2 * planeSize + pixelIndex] = source[sourceOffset + 2]; // B plane
                }
            }
        }

        return DataValue.FromTensor(pixels, [channelCount, height, width]);
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
