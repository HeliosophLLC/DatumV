namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Rotates an image by the specified number of degrees clockwise.
/// <c>rotate(img, degrees)</c> or <c>rotate(img, degrees, format)</c>.
/// For non-90°-multiple rotations the canvas expands to avoid clipping.
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class RotateImageFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
{
    /// <inheritdoc />
    public string Name => "rotate";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("rotate() requires 2 or 3 arguments: image, degrees[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"rotate() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"rotate() second argument (degrees) must be numeric, got {argumentKinds[1]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"rotate() third argument (format) must be String, got {argumentKinds[2]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("rotate() requires 1 or 2 auxiliary arguments: degrees[, format].");
        }

        if (auxiliaryKinds[0] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[0]))
        {
            throw new ArgumentException(
                $"rotate() degrees must be numeric, got {auxiliaryKinds[0]}.");
        }

        if (auxiliaryKinds.Length == 2
            && auxiliaryKinds[1] != DataKind.Unknown
            && auxiliaryKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"rotate() format must be String, got {auxiliaryKinds[1]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        float degrees = auxiliaryArgs[0].ToFloat();

        double radians = degrees * System.Math.PI / 180.0;
        double sinAngle = System.Math.Abs(System.Math.Sin(radians));
        double cosAngle = System.Math.Abs(System.Math.Cos(radians));

        int newWidth = (int)System.Math.Round(input.Width * cosAngle + input.Height * sinAngle);
        int newHeight = (int)System.Math.Round(input.Width * sinAngle + input.Height * cosAngle);

        SKBitmap rotated = new(newWidth, newHeight);
        using SKCanvas canvas = new(rotated);

        canvas.Clear(SKColors.Transparent);
        canvas.Translate(newWidth / 2f, newHeight / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-input.Width / 2f, -input.Height / 2f);
        canvas.DrawBitmap(input, 0, 0);

        return rotated;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        if (auxiliaryArgs.Length < 2 || auxiliaryArgs[1].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[1].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "rotate() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
