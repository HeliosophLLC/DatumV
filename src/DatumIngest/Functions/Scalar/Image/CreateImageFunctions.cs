using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>create_image_rgb(width Int32, height Int32, r Int32, g Int32, b Int32) → Image</c>.
/// Constructs a solid-color RGBA8888 <see cref="DataKind.Image"/> at the
/// requested pixel dimensions, with alpha forced to 255. Useful as a
/// placeholder canvas — e.g. backgrounds for compositing, blank frames in
/// a SCAN fold while a downstream model is still wiring up, test images.
/// </summary>
/// <remarks>
/// Color components accept any numeric scalar (Int32/UInt8/Float32/...);
/// each is range-checked to <c>[0, 255]</c>. Negative dimensions or any
/// out-of-range component throws <see cref="FunctionArgumentException"/>.
/// </remarks>
public sealed class CreateImageRgbFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "create_image_rgb";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a solid-color RGBA8888 Image of the requested width × height with "
        + "the given (r, g, b) bytes and alpha=255. Useful as a blank canvas for "
        + "compositing, a placeholder frame in a SCAN fold, or test inputs.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("height", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("r",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("g",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("b",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CreateImageRgbFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull || args[4].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        int width  = ReadDimension(args[0], "width");
        int height = ReadDimension(args[1], "height");
        byte r     = ReadColorByte(args[2], "r");
        byte g     = ReadColorByte(args[3], "g");
        byte b     = ReadColorByte(args[4], "b");

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap output = new(info);
        using (SKCanvas canvas = new(output))
        {
            // SKColor in Skia takes (red, green, blue, alpha) — alpha pinned
            // to 255 since this is the RGB variant. Use the RGBA variant when
            // a transparency channel matters.
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    private static int ReadDimension(ValueRef arg, string paramName)
    {
        if (!arg.TryToInt32(out int value))
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} of kind {arg.Kind} could not be widened to Int32.");
        }
        if (value <= 0)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} must be positive; got {value}.");
        }
        return value;
    }

    private static byte ReadColorByte(ValueRef arg, string paramName)
    {
        if (!arg.TryToInt32(out int value))
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} of kind {arg.Kind} could not be widened to Int32.");
        }
        if (value < 0 || value > 255)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} must be in [0, 255]; got {value}.");
        }
        return (byte)value;
    }
}
