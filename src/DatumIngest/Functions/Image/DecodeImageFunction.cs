namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Decodes encoded image bytes into a Tensor of pixel values with shape [height, width, 4] (RGBA).
/// <c>decode_image(img)</c> accepts Image or UInt8Array. Pixel values are floats in the 0–255 range.
/// </summary>
public sealed class DecodeImageFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "decode_image";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("decode_image() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"decode_image() requires Image or UInt8Array, got {argumentKinds[0]}.");
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
        SKBitmap bitmap = inputHandle.GetBitmap("decode_image");

        // Ensure RGBA8888 layout for consistent pixel access
        using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888
            ? bitmap
            : ConvertToRgba8888(bitmap);

        int width = rgba.Width;
        int height = rgba.Height;
        const int channelCount = 4; // RGBA

        float[] pixels = new float[height * width * channelCount];
        nint pixelPtr = rgba.GetPixels();

        unsafe
        {
            byte* source = (byte*)pixelPtr;
            int totalElements = height * width * channelCount;

            for (int i = 0; i < totalElements; i++)
            {
                pixels[i] = source[i];
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
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
