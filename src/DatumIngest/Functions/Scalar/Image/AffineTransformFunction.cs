using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>affine_transform(img Image, angle, scale_x, scale_y, shear_x, shear_y)
/// → Image</c>. Decomposed affine transform built from a rotation, an
/// anisotropic scale, and an X/Y shear pair, anchored at the image centre.
/// <c>angle</c> is in degrees. The output canvas is the same size as the
/// input — content that falls outside is clipped, freed pixels become
/// transparent.
/// </summary>
public sealed class AffineTransformFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "affine_transform";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Affine transform with rotation (degrees), per-axis scale, and X/Y shear, anchored at the image centre. "
        + "Output canvas is the same size as the input; freed pixels are transparent.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("angle",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("scale_x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("scale_y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("shear_x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("shear_y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AffineTransformFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
            }
        }

        SKBitmap source = args[0].AsImage();
        float angleDeg = args[1].ToFloat();
        float scaleX = args[2].ToFloat();
        float scaleY = args[3].ToFloat();
        float shearX = args[4].ToFloat();
        float shearY = args[5].ToFloat();

        float centerX = source.Width / 2f;
        float centerY = source.Height / 2f;
        float rad = angleDeg * (float)System.Math.PI / 180f;
        float cos = (float)System.Math.Cos(rad);
        float sin = (float)System.Math.Sin(rad);

        // Decomposed Mforward = R * S * H, where
        //   R = rotation, S = diag(scale_x, scale_y), H = [[1, shear_x],[shear_y, 1]].
        float m00 = cos * scaleX + (-sin) * scaleY * shearY;
        float m01 = cos * scaleX * shearX + (-sin) * scaleY;
        float m10 = sin * scaleX + cos * scaleY * shearY;
        float m11 = sin * scaleX * shearX + cos * scaleY;

        float translateX = centerX - (m00 * centerX + m01 * centerY);
        float translateY = centerY - (m10 * centerX + m11 * centerY);

        SKMatrix matrix = new(
            m00, m01, translateX,
            m10, m11, translateY,
            0, 0, 1);

        SKBitmap output = new(source.Width, source.Height);
        using (SKCanvas canvas = new(output))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(source, 0, 0);
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }
}
