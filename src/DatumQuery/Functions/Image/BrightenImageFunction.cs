namespace Axon.QueryEngine.Functions.Image;

using Axon.QueryEngine.Model;

using SkiaSharp;

/// <summary>
/// Increases image brightness by adding a fixed intensity to each RGB channel.
/// <c>brighten(img, intensity)</c> or <c>brighten(img, intensity, format)</c>.
/// The <c>intensity</c> value is added to every pixel's R, G, and B channels (clamped 0–255).
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class BrightenImageFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "brighten";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("brighten() requires 2 or 3 arguments: image, intensity[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"brighten() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"brighten() third argument (format) must be String, got {argumentKinds[2]}.");
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

        SKBitmap original = inputHandle.GetBitmap("brighten");

        // Use color matrix to add intensity to RGB channels
        // Matrix layout: [R, G, B, A, translate] × 4 rows
        SKBitmap brightened = new(original.Width, original.Height);
        using SKCanvas canvas = new(brightened);

        float normalizedIntensity = intensity / 255f;

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

        return DataValue.FromImageHandle(new ImageHandle(brightened, outputFormat));
    }
}
