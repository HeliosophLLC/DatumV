using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>grayscale(img Image) → Image</c>. Converts an image to grayscale using
/// BT.601 luminance weights. The output is RGBA8888 with all three color
/// channels set to the per-pixel luminance and alpha preserved.
/// </summary>
public sealed class GrayscaleImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "grayscale";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Converts an image to grayscale using BT.601 luminance weights. Alpha is preserved.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<GrayscaleImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef imgArg = arguments.Span[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap output = new(source.Width, source.Height);
        using (SKCanvas canvas = new(output))
        {
            float r = ImagePixelAccess.Bt601RedWeight;
            float g = ImagePixelAccess.Bt601GreenWeight;
            float b = ImagePixelAccess.Bt601BlueWeight;
            float[] matrix =
            [
                r, g, b, 0, 0,
                r, g, b, 0, 0,
                r, g, b, 0, 0,
                0, 0, 0, 1, 0,
            ];
            using SKColorFilter filter = SKColorFilter.CreateColorMatrix(matrix);
            using SKPaint paint = new() { ColorFilter = filter };
            canvas.DrawBitmap(source, 0, 0, paint);
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }
}
