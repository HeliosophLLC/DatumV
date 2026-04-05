using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>resize(img Image, width, height) → Image</c>. Resizes an image to the
/// requested pixel dimensions using SkiaSharp's default sampling
/// (bilinear-equivalent). Width and height must be positive integers; float
/// arguments truncate.
/// </summary>
public sealed class ResizeImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "resize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Resizes an image to the requested pixel dimensions using default sampling. "
        + "Width and height must be positive; float arguments truncate to integers.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",  DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("height", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ResizeImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        if (imgArg.IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        int targetWidth = args[1].ToInt32();
        int targetHeight = args[2].ToInt32();
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"width and height must be positive; got {targetWidth}×{targetHeight}.");
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap? resized = source.Resize(
            new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
        if (resized is null)
        {
            throw new FunctionArgumentException(Name,
                $"failed to produce a {targetWidth}×{targetHeight} bitmap.");
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(resized));
    }
}
