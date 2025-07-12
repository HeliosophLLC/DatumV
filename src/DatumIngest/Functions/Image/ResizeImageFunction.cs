namespace DatumQuery.Functions.Image;

using DatumQuery.Model;

using SkiaSharp;

/// <summary>
/// Resizes an image to the specified width and height using high-quality sampling.
/// <c>resize(img, width, height)</c> or <c>resize(img, width, height, format)</c>.
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// When omitted, the original format is preserved.
/// </summary>
public sealed class ResizeImageFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "resize";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (3 or 4))
        {
            throw new ArgumentException(
                "resize() requires 3 or 4 arguments: image, width, height[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"resize() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"resize() fourth argument (format) must be String, got {argumentKinds[3]}.");
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
        int targetWidth = (int)arguments[1].AsScalar();
        int targetHeight = (int)arguments[2].AsScalar();

        string? formatOverride = arguments.Length == 4 ? arguments[3].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("resize");

        SKBitmap resized = original.Resize(
            new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"resize() failed to resize the image to {targetWidth}×{targetHeight}.");

        return DataValue.FromImageHandle(new ImageHandle(resized, outputFormat));
    }
}
