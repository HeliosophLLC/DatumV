namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Crops a rectangular region from an image.
/// <c>crop(img, x, y, width, height)</c> or <c>crop(img, x, y, width, height, format)</c>.
/// Coordinates are in pixels with the origin at the top-left corner.
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class CropImageFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "crop";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (5 or 6))
        {
            throw new ArgumentException(
                "crop() requires 5 or 6 arguments: image, x, y, width, height[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"crop() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 6 && argumentKinds[5] != DataKind.String)
        {
            throw new ArgumentException(
                $"crop() sixth argument (format) must be String, got {argumentKinds[5]}.");
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
        int x = (int)arguments[1].AsFloat32();
        int y = (int)arguments[2].AsFloat32();
        int cropWidth = (int)arguments[3].AsFloat32();
        int cropHeight = (int)arguments[4].AsFloat32();

        string? formatOverride = arguments.Length == 6 ? arguments[5].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("crop");
        SKRectI cropRect = new(x, y, x + cropWidth, y + cropHeight);

        SKBitmap cropped = new();

        if (!original.ExtractSubset(cropped, cropRect))
        {
            cropped.Dispose();
            throw new InvalidOperationException(
                $"crop() failed to extract region ({x}, {y}, {cropWidth}×{cropHeight}).");
        }

        return DataValue.FromImageHandle(new ImageHandle(cropped, outputFormat));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle(store);
        int x = (int)arguments[1].AsFloat32();
        int y = (int)arguments[2].AsFloat32();
        int cropWidth = (int)arguments[3].AsFloat32();
        int cropHeight = (int)arguments[4].AsFloat32();

        string? formatOverride = arguments.Length == 6 ? arguments[5].AsString(store) : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("crop");
        SKRectI cropRect = new(x, y, x + cropWidth, y + cropHeight);

        SKBitmap cropped = new();

        if (!original.ExtractSubset(cropped, cropRect))
        {
            cropped.Dispose();
            throw new InvalidOperationException(
                $"crop() failed to extract region ({x}, {y}, {cropWidth}×{cropHeight}).");
        }

        return DataValue.FromImageHandle(new ImageHandle(cropped, outputFormat), store);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
