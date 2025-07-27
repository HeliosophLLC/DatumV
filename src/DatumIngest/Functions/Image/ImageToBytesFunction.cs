namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Extracts raw RGBA pixel bytes from an encoded image.
/// <c>image_to_bytes(img)</c> accepts Image or UInt8Array and returns a flat
/// UInt8Array of length <c>height × width × 4</c> in RGBA byte order.
/// </summary>
public sealed class ImageToBytesFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "image_to_bytes";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("image_to_bytes() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"image_to_bytes() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.UInt8Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.UInt8Array);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap bitmap = inputHandle.GetBitmap("image_to_bytes");

        using SKBitmap rgba = bitmap.ColorType == SKColorType.Rgba8888
            ? bitmap
            : ConvertToRgba8888(bitmap);

        int byteCount = rgba.Height * rgba.Width * 4;
        byte[] pixels = new byte[byteCount];
        nint pixelPtr = rgba.GetPixels();

        unsafe
        {
            byte* source = (byte*)pixelPtr;

            for (int i = 0; i < byteCount; i++)
            {
                pixels[i] = source[i];
            }
        }

        return DataValue.FromUInt8Array(pixels);
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
