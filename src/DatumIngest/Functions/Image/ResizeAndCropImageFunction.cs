namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Resizes an image to fill the target dimensions (preserving aspect ratio) and then
/// crops to the exact target size using a gravity anchor.
/// <c>resize_and_crop(img, w, h, gravity)</c> or <c>resize_and_crop(img, w, h, gravity, format)</c>.
/// Supported gravity values: <c>'center'</c>, <c>'top'</c>, <c>'bottom'</c>, <c>'left'</c>, <c>'right'</c>.
/// </summary>
public sealed class ResizeAndCropImageFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
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

        if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"resize_and_crop() second argument (width) must be numeric, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"resize_and_crop() third argument (height) must be numeric, got {argumentKinds[2]}.");
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
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length is not (3 or 4))
        {
            throw new ArgumentException(
                "resize_and_crop() requires 3 or 4 auxiliary arguments: width, height, gravity[, format].");
        }

        if (auxiliaryKinds[0] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[0]))
        {
            throw new ArgumentException(
                $"resize_and_crop() width must be numeric, got {auxiliaryKinds[0]}.");
        }

        if (auxiliaryKinds[1] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[1]))
        {
            throw new ArgumentException(
                $"resize_and_crop() height must be numeric, got {auxiliaryKinds[1]}.");
        }

        if (auxiliaryKinds[2] != DataKind.Unknown && auxiliaryKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"resize_and_crop() gravity must be String, got {auxiliaryKinds[2]}.");
        }

        if (auxiliaryKinds.Length == 4
            && auxiliaryKinds[3] != DataKind.Unknown
            && auxiliaryKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"resize_and_crop() format must be String, got {auxiliaryKinds[3]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        int targetWidth = auxiliaryArgs[0].ToInt32();
        int targetHeight = auxiliaryArgs[1].ToInt32();
        string gravity = auxiliaryArgs[2].AsString().ToUpperInvariant();

        float scaleX = (float)targetWidth / input.Width;
        float scaleY = (float)targetHeight / input.Height;
        float scale = System.Math.Max(scaleX, scaleY);

        int resizedWidth = (int)System.Math.Ceiling(input.Width * scale);
        int resizedHeight = (int)System.Math.Ceiling(input.Height * scale);

        using SKBitmap resized = input.Resize(
            new SKImageInfo(resizedWidth, resizedHeight), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"resize_and_crop() failed to resize to {resizedWidth}×{resizedHeight}.");

        (int cropX, int cropY) = ComputeCropOffset(resizedWidth, resizedHeight, targetWidth, targetHeight, gravity);

        SKBitmap cropped = new(targetWidth, targetHeight);
        using SKCanvas canvas = new(cropped);
        canvas.DrawBitmap(resized, new SKRect(cropX, cropY, cropX + targetWidth, cropY + targetHeight),
            new SKRect(0, 0, targetWidth, targetHeight));

        return cropped;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        if (auxiliaryArgs.Length < 4 || auxiliaryArgs[3].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[3].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "resize_and_crop() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

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
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
