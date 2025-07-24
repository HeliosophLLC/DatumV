namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Applies a Gaussian blur to an image.
/// <c>blur(img, radius)</c> or <c>blur(img, radius, format)</c>.
/// The <c>radius</c> controls blur strength (sigma in both X and Y directions).
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class BlurImageFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "blur";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("blur() requires 2 or 3 arguments: image, radius[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"blur() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"blur() third argument (format) must be String, got {argumentKinds[2]}.");
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
        float radius = arguments[1].AsScalar();

        string? formatOverride = arguments.Length == 3 ? arguments[2].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("blur");

        SKBitmap blurred = new(original.Width, original.Height);
        using SKCanvas canvas = new(blurred);
        using SKImageFilter blurFilter = SKImageFilter.CreateBlur(radius, radius);
        using SKPaint paint = new() { ImageFilter = blurFilter };

        canvas.DrawBitmap(original, 0, 0, paint);

        return DataValue.FromImageHandle(new ImageHandle(blurred, outputFormat));
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
