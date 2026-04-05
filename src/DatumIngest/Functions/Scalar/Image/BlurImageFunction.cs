using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>blur(img Image, radius) → Image</c>. Gaussian blur with the given
/// sigma radius applied symmetrically in X and Y. Negative radius is rejected.
/// </summary>
public sealed class BlurImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "blur";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Gaussian blur with sigma radius applied symmetrically in X and Y.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",  DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("radius", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<BlurImageFunction>(argumentKinds);

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

        float radius = args[1].ToFloat();
        if (radius < 0)
        {
            throw new FunctionArgumentException(Name,
                $"radius must be non-negative; got {radius}.");
        }

        SKBitmap source = args[0].AsImage();
        SKBitmap output = new(source.Width, source.Height);
        using (SKCanvas canvas = new(output))
        using (SKImageFilter blurFilter = SKImageFilter.CreateBlur(radius, radius))
        using (SKPaint paint = new() { ImageFilter = blurFilter })
        {
            canvas.DrawBitmap(source, 0, 0, paint);
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }
}
