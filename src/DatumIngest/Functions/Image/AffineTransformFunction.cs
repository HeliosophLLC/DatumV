namespace DatumIngest.Functions.Image;

using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Applies an affine transformation to an image using decomposed parameters.
/// <c>affine_transform(img, angle, scale_x, scale_y, shear_x, shear_y)</c> or with optional format.
/// The <c>angle</c> is in degrees, <c>scale_x</c>/<c>scale_y</c> are scale factors,
/// and <c>shear_x</c>/<c>shear_y</c> are shear coefficients.
/// </summary>
public sealed class AffineTransformFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "affine_transform";

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

        if (argumentKinds.Length == 7 && argumentKinds[6] != DataKind.String)
        {
            throw new ArgumentException(
                $"affine_transform() seventh argument (format) must be String, got {argumentKinds[6]}.");
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
        float angleDegrees = arguments[1].AsScalar();
        float scaleX = arguments[2].AsScalar();
        float scaleY = arguments[3].AsScalar();
        float shearX = arguments[4].AsScalar();
        float shearY = arguments[5].AsScalar();

        string? formatOverride = arguments.Length == 7 ? arguments[6].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("affine_transform");

        float centerX = original.Width / 2f;
        float centerY = original.Height / 2f;

        // Build the affine matrix: translate to origin → shear → scale → rotate → translate back
        float angleRadians = angleDegrees * (float)System.Math.PI / 180f;
        float cosAngle = (float)System.Math.Cos(angleRadians);
        float sinAngle = (float)System.Math.Sin(angleRadians);

        // Combined affine matrix: Rotation × Scale × Shear
        // Shear matrix: [1, shear_x; shear_y, 1]
        // Scale matrix: [scale_x, 0; 0, scale_y]
        // Rotation matrix: [cos, -sin; sin, cos]
        // Combined = R × S × Sh

        float m00 = cosAngle * scaleX + (-sinAngle) * scaleY * shearY;
        float m01 = cosAngle * scaleX * shearX + (-sinAngle) * scaleY;
        float m10 = sinAngle * scaleX + cosAngle * scaleY * shearY;
        float m11 = sinAngle * scaleX * shearX + cosAngle * scaleY;

        // Translation: keep image centered
        float translateX = centerX - (m00 * centerX + m01 * centerY);
        float translateY = centerY - (m10 * centerX + m11 * centerY);

        SKMatrix matrix = new(
            m00, m01, translateX,
            m10, m11, translateY,
            0, 0, 1);

        SKBitmap transformed = new(original.Width, original.Height);
        using SKCanvas canvas = new(transformed);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(original, 0, 0);

        return DataValue.FromImageHandle(new ImageHandle(transformed, outputFormat));
    }
}
