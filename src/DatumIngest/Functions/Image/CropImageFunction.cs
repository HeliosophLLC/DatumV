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
public sealed class CropImageFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
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
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length is not (4 or 5))
        {
            throw new ArgumentException(
                "crop() requires 4 or 5 auxiliary arguments: x, y, width, height[, format].");
        }

        string[] names = ["x", "y", "width", "height"];
        for (int i = 0; i < 4; i++)
        {
            if (auxiliaryKinds[i] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[i]))
            {
                throw new ArgumentException(
                    $"crop() {names[i]} must be numeric, got {auxiliaryKinds[i]}.");
            }
        }

        if (auxiliaryKinds.Length == 5
            && auxiliaryKinds[4] != DataKind.Unknown
            && auxiliaryKinds[4] != DataKind.String)
        {
            throw new ArgumentException(
                $"crop() format must be String, got {auxiliaryKinds[4]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        int x = auxiliaryArgs[0].ToInt32();
        int y = auxiliaryArgs[1].ToInt32();
        int cropWidth = auxiliaryArgs[2].ToInt32();
        int cropHeight = auxiliaryArgs[3].ToInt32();

        SKRectI cropRect = new(x, y, x + cropWidth, y + cropHeight);
        SKBitmap cropped = new();

        if (!input.ExtractSubset(cropped, cropRect))
        {
            cropped.Dispose();
            throw new InvalidOperationException(
                $"crop() failed to extract region ({x}, {y}, {cropWidth}×{cropHeight}).");
        }

        return cropped;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        if (auxiliaryArgs.Length < 5 || auxiliaryArgs[4].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[4].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "crop() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
