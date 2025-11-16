namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Resizes an image to the specified width and height using high-quality sampling.
/// <c>resize(img, width, height)</c> or <c>resize(img, width, height, format)</c>.
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// When omitted, the original format is preserved.
/// </summary>
public sealed class ResizeImageFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
{
    /// <inheritdoc />
    public string Name => "resize";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

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

        if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"resize() second argument (width) must be numeric, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"resize() third argument (height) must be numeric, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 4 && argumentKinds[3] != DataKind.String)
        {
            throw new ArgumentException(
                $"resize() fourth argument (format) must be String, got {argumentKinds[3]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "resize() requires 2 or 3 auxiliary arguments: width, height[, format].");
        }

        if (auxiliaryKinds[0] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[0]))
        {
            throw new ArgumentException(
                $"resize() width must be numeric, got {auxiliaryKinds[0]}.");
        }

        if (auxiliaryKinds[1] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[1]))
        {
            throw new ArgumentException(
                $"resize() height must be numeric, got {auxiliaryKinds[1]}.");
        }

        if (auxiliaryKinds.Length == 3
            && auxiliaryKinds[2] != DataKind.Unknown
            && auxiliaryKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"resize() format must be String, got {auxiliaryKinds[2]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        int targetWidth = auxiliaryArgs[0].ToInt32();
        int targetHeight = auxiliaryArgs[1].ToInt32();

        SKBitmap resized = input.Resize(
            new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"resize() failed to resize the image to {targetWidth}×{targetHeight}.");

        return resized;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        if (auxiliaryArgs.Length < 3 || auxiliaryArgs[2].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[2].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "resize() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
