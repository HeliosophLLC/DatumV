using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>image_transpose(img Image) → Image</c>. Reflects an image across its
/// main diagonal: <c>output[x, y] = input[y, x]</c>. Width and height swap, so
/// a <c>W×H</c> image becomes <c>H×W</c>. Equivalent to a 90° rotation
/// composed with a mirror — the canonical fix for source corpora (notably
/// EMNIST) whose glyphs are stored transposed relative to display orientation.
/// </summary>
/// <remarks>
/// Unlike <see cref="RotateImageFunction"/>, this is a pure reflection: it
/// touches no interpolation and invents no pixels, so it round-trips exactly
/// (transposing twice returns the original). Returns a null Image when the
/// argument is null.
/// </remarks>
public sealed class ImageTransposeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_transpose";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Transposes an image across its main diagonal (output[x,y] = input[y,x]); "
        + "width and height swap. A pure reflection — no interpolation, exact "
        + "round-trip. The standard fix for transposed source glyphs (e.g. EMNIST).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageTransposeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        SKBitmap source = args[0].AsImage();
        int srcWidth = source.Width;
        int srcHeight = source.Height;

        // Transposed canvas: dst is srcHeight wide × srcWidth tall, preserving
        // the source colour + alpha encoding so a grayscale glyph stays
        // grayscale.
        SKBitmap transposed = new(
            new SKImageInfo(srcHeight, srcWidth, source.ColorType, source.AlphaType));
        for (int dstY = 0; dstY < srcWidth; dstY++)
        {
            for (int dstX = 0; dstX < srcHeight; dstX++)
            {
                // Reflect across the diagonal: dst (x, y) ← src (y, x).
                transposed.SetPixel(dstX, dstY, source.GetPixel(dstY, dstX));
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(transposed));
    }
}
