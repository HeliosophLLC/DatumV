namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Converts an image to grayscale using the luminance color matrix.
/// <c>grayscale(img)</c> or <c>grayscale(img, format)</c>.
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class GrayscaleImageFunction : IScalarFunction, ICostAwareFunction
{
    // ITU-R BT.601 luminance weights
    private const float RedWeight = 0.2126f;
    private const float GreenWeight = 0.7152f;
    private const float BlueWeight = 0.0722f;

    /// <inheritdoc />
    public string Name => "grayscale";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("grayscale() requires 1 or 2 arguments: image[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"grayscale() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"grayscale() second argument (format) must be String, got {argumentKinds[1]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle();

        string? formatOverride = arguments.Length == 2 ? arguments[1].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("grayscale");

        SKBitmap grayscaled = new(original.Width, original.Height);
        using SKCanvas canvas = new(grayscaled);

        float[] matrix =
        [
            RedWeight, GreenWeight, BlueWeight, 0, 0,
            RedWeight, GreenWeight, BlueWeight, 0, 0,
            RedWeight, GreenWeight, BlueWeight, 0, 0,
            0, 0, 0, 1, 0
        ];

        using SKColorFilter filter = SKColorFilter.CreateColorMatrix(matrix);
        using SKPaint paint = new() { ColorFilter = filter };

        canvas.DrawBitmap(original, 0, 0, paint);

        return DataValue.FromImageHandle(new ImageHandle(grayscaled, outputFormat));
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
