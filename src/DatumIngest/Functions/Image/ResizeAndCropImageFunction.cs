namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Resizes an image to fill the target dimensions (preserving aspect ratio) and then
/// crops to the exact target size using a gravity anchor.
/// <c>resize_and_crop(img, w, h, gravity)</c> or <c>resize_and_crop(img, w, h, gravity, format)</c>.
/// Supported gravity values: <c>'center'</c>, <c>'top'</c>, <c>'bottom'</c>, <c>'left'</c>, <c>'right'</c>.
/// </summary>
public sealed class ResizeAndCropImageFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "resize_and_crop";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (4 or 5))
        {
            throw new ArgumentException(
                "resize_and_crop() requires 4 or 5 arguments: image, width, height, gravity[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"resize_and_crop() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"resize_and_crop() fourth argument (gravity) must be String, got {argumentKinds[3]}.");
        }

        if (argumentKinds.Length == 5 && argumentKinds[4] != DataKind.String)
        {
            throw new ArgumentException(
                $"resize_and_crop() fifth argument (format) must be String, got {argumentKinds[4]}.");
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
        int targetWidth = (int)arguments[1].AsFloat32();
        int targetHeight = (int)arguments[2].AsFloat32();
        string gravity = arguments[3].AsString().ToUpperInvariant();

        string? formatOverride = arguments.Length == 5 ? arguments[4].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("resize_and_crop");

        // Step 1: Resize to fill — scale so both dimensions meet or exceed target
        float scaleX = (float)targetWidth / original.Width;
        float scaleY = (float)targetHeight / original.Height;
        float scale = System.Math.Max(scaleX, scaleY);

        int resizedWidth = (int)System.Math.Ceiling(original.Width * scale);
        int resizedHeight = (int)System.Math.Ceiling(original.Height * scale);

        using SKBitmap resized = original.Resize(
            new SKImageInfo(resizedWidth, resizedHeight), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"resize_and_crop() failed to resize to {resizedWidth}×{resizedHeight}.");

        // Step 2: Crop to exact target dimensions using gravity
        (int cropX, int cropY) = ComputeCropOffset(resizedWidth, resizedHeight, targetWidth, targetHeight, gravity);

        SKBitmap cropped = new(targetWidth, targetHeight);
        using SKCanvas canvas = new(cropped);
        canvas.DrawBitmap(resized, new SKRect(cropX, cropY, cropX + targetWidth, cropY + targetHeight),
            new SKRect(0, 0, targetWidth, targetHeight));

        return DataValue.FromImageHandle(new ImageHandle(cropped, outputFormat));
    }

    private static (int X, int Y) ComputeCropOffset(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, string gravity)
    {
        int excessX = sourceWidth - targetWidth;
        int excessY = sourceHeight - targetHeight;

        return gravity switch
        {
            "CENTER" => (excessX / 2, excessY / 2),
            "TOP" => (excessX / 2, 0),
            "BOTTOM" => (excessX / 2, excessY),
            "LEFT" => (0, excessY / 2),
            "RIGHT" => (excessX, excessY / 2),
            _ => throw new ArgumentException(
                $"resize_and_crop() unknown gravity '{gravity}'. Supported: center, top, bottom, left, right.")
        };
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
        int targetWidth = (int)arguments[1].AsFloat32();
        int targetHeight = (int)arguments[2].AsFloat32();
        string gravity = arguments[3].AsString(store).ToUpperInvariant();

        string? formatOverride = arguments.Length == 5 ? arguments[4].AsString(store) : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("resize_and_crop");

        // Step 1: Resize to fill — scale so both dimensions meet or exceed target
        float scaleX = (float)targetWidth / original.Width;
        float scaleY = (float)targetHeight / original.Height;
        float scale = System.Math.Max(scaleX, scaleY);

        int resizedWidth = (int)System.Math.Ceiling(original.Width * scale);
        int resizedHeight = (int)System.Math.Ceiling(original.Height * scale);

        using SKBitmap resized = original.Resize(
            new SKImageInfo(resizedWidth, resizedHeight), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"resize_and_crop() failed to resize to {resizedWidth}×{resizedHeight}.");

        // Step 2: Crop to exact target dimensions using gravity
        (int cropX, int cropY) = ComputeCropOffset(resizedWidth, resizedHeight, targetWidth, targetHeight, gravity);

        SKBitmap cropped = new(targetWidth, targetHeight);
        using SKCanvas canvas = new(cropped);
        canvas.DrawBitmap(resized, new SKRect(cropX, cropY, cropX + targetWidth, cropY + targetHeight),
            new SKRect(0, 0, targetWidth, targetHeight));

        return DataValue.FromImageHandle(new ImageHandle(cropped, outputFormat), store);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
