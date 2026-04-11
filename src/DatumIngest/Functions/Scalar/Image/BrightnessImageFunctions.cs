using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>brighten(img Image, intensity) → Image</c>. Increases brightness by
/// adding the intensity argument (in the 0–255 byte range) to each pixel's
/// R, G, B channels; the SkiaSharp color-matrix path clamps at the channel
/// boundary. Alpha is preserved.
/// </summary>
public sealed class BrightenImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "brighten";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Increases brightness by adding intensity (0–255 byte units) to each RGB channel. Alpha is preserved.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",     DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("intensity", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<BrightenImageFunction>(argumentKinds);

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

        float intensity = args[1].ToFloat();
        SKBitmap source = args[0].AsImage();
        return new ValueTask<ValueRef>(ValueRef.FromImage(
            BrightnessImageOps.ApplyBias(source, intensity / 255f)));
    }
}

/// <summary>
/// <c>darken(img Image, intensity) → Image</c>. Decreases brightness by
/// subtracting the intensity argument (0–255 byte range) from each pixel's
/// R, G, B channels with channel-boundary clamping. Alpha is preserved.
/// </summary>
public sealed class DarkenImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "darken";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Decreases brightness by subtracting intensity (0–255 byte units) from each RGB channel. Alpha is preserved.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",     DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("intensity", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DarkenImageFunction>(argumentKinds);

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

        float intensity = args[1].ToFloat();
        SKBitmap source = args[0].AsImage();
        return new ValueTask<ValueRef>(ValueRef.FromImage(
            BrightnessImageOps.ApplyBias(source, -intensity / 255f)));
    }
}

internal static class BrightnessImageOps
{
    /// <summary>
    /// Renders <paramref name="source"/> through a color matrix that adds a
    /// constant bias to each RGB channel. <paramref name="bias"/> is in the
    /// normalised 0–1 channel-space the SkiaSharp matrix expects (i.e.
    /// <c>intensity/255</c>), positive to brighten and negative to darken.
    /// </summary>
    public static SKBitmap ApplyBias(SKBitmap source, float bias)
    {
        SKBitmap output = new(source.Width, source.Height);
        using SKCanvas canvas = new(output);
        float[] matrix =
        [
            1, 0, 0, 0, bias,
            0, 1, 0, 0, bias,
            0, 0, 1, 0, bias,
            0, 0, 0, 1, 0,
        ];
        using SKColorFilter filter = SKColorFilter.CreateColorMatrix(matrix);
        using SKPaint paint = new() { ColorFilter = filter };
        canvas.DrawBitmap(source, 0, 0, paint);
        return output;
    }
}
