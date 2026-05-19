using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>rotate(img Image, degrees) → Image</c>. Rotates an image clockwise by
/// the requested angle. The output canvas expands to the rotated bounding
/// box so non-90°-multiple rotations don't clip; the freed corners are
/// transparent.
/// </summary>
public sealed class RotateImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "rotate";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Rotates an image clockwise by an arbitrary angle (degrees). "
        + "Canvas expands for non-90° rotations; new corners are transparent.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("degrees", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RotateImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        SKBitmap source = args[0].AsImage();
        float degrees = args[1].ToFloat();

        double radians = degrees * System.Math.PI / 180.0;
        double sinAbs = System.Math.Abs(System.Math.Sin(radians));
        double cosAbs = System.Math.Abs(System.Math.Cos(radians));
        int newWidth = (int)System.Math.Round(source.Width * cosAbs + source.Height * sinAbs);
        int newHeight = (int)System.Math.Round(source.Width * sinAbs + source.Height * cosAbs);
        if (newWidth <= 0 || newHeight <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"produced a degenerate {newWidth}×{newHeight} canvas for {degrees}°.");
        }

        SKBitmap rotated = new(newWidth, newHeight);
        using (SKCanvas canvas = new(rotated))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(newWidth / 2f, newHeight / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(rotated));
    }
}
