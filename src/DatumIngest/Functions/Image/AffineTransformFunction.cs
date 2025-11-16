namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Applies an affine transformation to an image using decomposed parameters.
/// <c>affine_transform(img, angle, scale_x, scale_y, shear_x, shear_y)</c> or with optional format.
/// The <c>angle</c> is in degrees, <c>scale_x</c>/<c>scale_y</c> are scale factors,
/// and <c>shear_x</c>/<c>shear_y</c> are shear coefficients.
/// </summary>
public sealed class AffineTransformFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
{
    /// <inheritdoc />
    public string Name => "affine_transform";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (6 or 7))
        {
            throw new ArgumentException(
                "affine_transform() requires 6 or 7 arguments: image, angle, scale_x, scale_y, shear_x, shear_y[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"affine_transform() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"affine_transform() second argument (angle) must be numeric, got {argumentKinds[1]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"affine_transform() third argument (scale_x) must be numeric, got {argumentKinds[2]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[3]))
        {
            throw new ArgumentException(
                $"affine_transform() fourth argument (scale_y) must be numeric, got {argumentKinds[3]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[4]))
        {
            throw new ArgumentException(
                $"affine_transform() fifth argument (shear_x) must be numeric, got {argumentKinds[4]}.");
        }

        if (!DataValue.IsNumericScalarKind(argumentKinds[5]))
        {
            throw new ArgumentException(
                $"affine_transform() sixth argument (shear_y) must be numeric, got {argumentKinds[5]}.");
        }

        if (argumentKinds.Length == 7 && argumentKinds[6] != DataKind.String)
        {
            throw new ArgumentException(
                $"affine_transform() seventh argument (format) must be String, got {argumentKinds[6]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        if (auxiliaryKinds.Length is not (5 or 6))
        {
            throw new ArgumentException(
                "affine_transform() requires 5 or 6 auxiliary arguments: angle, scale_x, scale_y, shear_x, shear_y[, format].");
        }

        string[] names = ["angle", "scale_x", "scale_y", "shear_x", "shear_y"];
        for (int i = 0; i < 5; i++)
        {
            if (auxiliaryKinds[i] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[i]))
            {
                throw new ArgumentException(
                    $"affine_transform() {names[i]} must be numeric, got {auxiliaryKinds[i]}.");
            }
        }

        if (auxiliaryKinds.Length == 6
            && auxiliaryKinds[5] != DataKind.Unknown
            && auxiliaryKinds[5] != DataKind.String)
        {
            throw new ArgumentException(
                $"affine_transform() format must be String, got {auxiliaryKinds[5]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        float angleDegrees = auxiliaryArgs[0].ToFloat();
        float scaleX = auxiliaryArgs[1].ToFloat();
        float scaleY = auxiliaryArgs[2].ToFloat();
        float shearX = auxiliaryArgs[3].ToFloat();
        float shearY = auxiliaryArgs[4].ToFloat();

        float centerX = input.Width / 2f;
        float centerY = input.Height / 2f;

        float angleRadians = angleDegrees * (float)System.Math.PI / 180f;
        float cosAngle = (float)System.Math.Cos(angleRadians);
        float sinAngle = (float)System.Math.Sin(angleRadians);

        float m00 = cosAngle * scaleX + (-sinAngle) * scaleY * shearY;
        float m01 = cosAngle * scaleX * shearX + (-sinAngle) * scaleY;
        float m10 = sinAngle * scaleX + cosAngle * scaleY * shearY;
        float m11 = sinAngle * scaleX * shearX + cosAngle * scaleY;

        float translateX = centerX - (m00 * centerX + m01 * centerY);
        float translateY = centerY - (m10 * centerX + m11 * centerY);

        SKMatrix matrix = new(
            m00, m01, translateX,
            m10, m11, translateY,
            0, 0, 1);

        SKBitmap transformed = new(input.Width, input.Height);
        using SKCanvas canvas = new(transformed);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(input, 0, 0);

        return transformed;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        if (auxiliaryArgs.Length < 6 || auxiliaryArgs[5].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[5].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "affine_transform() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
