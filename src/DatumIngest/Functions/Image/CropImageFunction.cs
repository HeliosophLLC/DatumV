namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
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

        if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"crop() second argument (x) must be numeric, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"crop() third argument (y) must be numeric, got {argumentKinds[2]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[3]))
        {
            throw new ArgumentException(
                $"crop() fourth argument (width) must be numeric, got {argumentKinds[3]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[4]))
        {
            throw new ArgumentException(
                $"crop() fifth argument (height) must be numeric, got {argumentKinds[4]}.");
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
        int x = arguments[1].ToInt32();
        int y = arguments[2].ToInt32();
        int cropWidth = arguments[3].ToInt32();
        int cropHeight = arguments[4].ToInt32();

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
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle(frame.Source, frame.SidecarRegistry);
        int x = arguments[1].ToInt32();
        int y = arguments[2].ToInt32();
        int cropWidth = arguments[3].ToInt32();
        int cropHeight = arguments[4].ToInt32();

        string? formatOverride = arguments.Length == 6 ? arguments[5].AsString(frame.Source) : null;
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

        return DataValue.FromImageHandle(new ImageHandle(cropped, outputFormat), frame.Target);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
