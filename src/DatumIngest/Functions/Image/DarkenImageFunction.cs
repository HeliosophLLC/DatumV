namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Decreases image brightness by subtracting a fixed intensity from each RGB channel.
/// <c>darken(img, intensity)</c> or <c>darken(img, intensity, format)</c>.
/// The <c>intensity</c> value is subtracted from every pixel's R, G, and B channels (clamped 0–255).
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class DarkenImageFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "darken";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("darken() requires 2 or 3 arguments: image, intensity[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"darken() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"darken() third argument (format) must be String, got {argumentKinds[2]}.");
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
        float intensity = arguments[1].AsScalar();

        string? formatOverride = arguments.Length == 3 ? arguments[2].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("darken");

        // Use color matrix to subtract intensity from RGB channels
        SKBitmap darkened = new(original.Width, original.Height);
        using SKCanvas canvas = new(darkened);

        float normalizedIntensity = -(intensity / 255f);

        float[] matrix =
        [
            1, 0, 0, 0, normalizedIntensity,
            0, 1, 0, 0, normalizedIntensity,
            0, 0, 1, 0, normalizedIntensity,
            0, 0, 0, 1, 0
        ];

        using SKColorFilter filter = SKColorFilter.CreateColorMatrix(matrix);
        using SKPaint paint = new() { ColorFilter = filter };

        canvas.DrawBitmap(original, 0, 0, paint);

        return DataValue.FromImageHandle(new ImageHandle(darkened, outputFormat));
    }
}
