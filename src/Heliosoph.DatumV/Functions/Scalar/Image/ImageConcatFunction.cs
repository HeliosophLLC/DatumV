using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// Concatenates two images into one. Two call shapes:
/// <list type="bullet">
///   <item><c>image_concat(a, b)</c> — joins them side-by-side (horizontal).</item>
///   <item><c>image_concat(a, b, direction)</c> — <c>direction</c> selects the
///   join axis: <c>'horizontal'</c>/<c>'h'</c> place <c>b</c> to the right of
///   <c>a</c>; <c>'vertical'</c>/<c>'v'</c> place <c>b</c> below <c>a</c>
///   (case-insensitive).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The two images need not share dimensions. For a horizontal join the output
/// is <c>(w_a + w_b) × max(h_a, h_b)</c>; for a vertical join it is
/// <c>max(w_a, w_b) × (h_a + h_b)</c>. Each image is centred along the
/// perpendicular (cross) axis and any leftover margin is filled with
/// transparent pixels, so the output is always RGBA — a shorter image sitting
/// beside a taller one gains transparent bands above and below rather than
/// being stretched.
/// </para>
/// <para>
/// Returns a null Image when either image argument is null.
/// </para>
/// </remarks>
public sealed class ImageConcatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_concat";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Joins two images into one. image_concat(a, b) places them side-by-side; "
        + "pass a third 'horizontal'/'h' or 'vertical'/'v' argument to choose the "
        + "axis (vertical stacks b below a). Images need not match in size — the "
        + "shorter one is centred on the cross axis with transparent margins. "
        + "Returns null if either image is null.";

    private static readonly string[] DirectionValues =
        ["horizontal", "h", "vertical", "v"];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("b", DataKindMatcher.Exact(DataKind.Image)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("b",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("direction", DataKindMatcher.StringEnum(DirectionValues)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageConcatFunction>(argumentKinds);

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

        bool horizontal = args.Length < 3 || args[2].IsNull || ParseHorizontal(args[2].AsString());

        SKBitmap a = args[0].AsImage();
        SKBitmap b = args[1].AsImage();

        int outWidth = horizontal ? a.Width + b.Width : System.Math.Max(a.Width, b.Width);
        int outHeight = horizontal ? System.Math.Max(a.Height, b.Height) : a.Height + b.Height;

        // RGBA/unpremultiplied so the transparent cross-axis margins read as
        // true transparency rather than the black premultiplied form.
        SKImageInfo info = new(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        SKBitmap output = new(info);
        using (SKCanvas canvas = new(output))
        {
            canvas.Clear(SKColors.Transparent);
            if (horizontal)
            {
                // Lay a then b left-to-right; centre each vertically.
                canvas.DrawBitmap(a, 0, (outHeight - a.Height) / 2f);
                canvas.DrawBitmap(b, a.Width, (outHeight - b.Height) / 2f);
            }
            else
            {
                // Lay a then b top-to-bottom; centre each horizontally.
                canvas.DrawBitmap(a, (outWidth - a.Width) / 2f, 0);
                canvas.DrawBitmap(b, (outWidth - b.Width) / 2f, a.Height);
            }
        }

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    private static bool ParseHorizontal(string direction) =>
        direction.ToLowerInvariant() switch
        {
            "horizontal" or "h" => true,
            "vertical" or "v" => false,
            _ => throw new FunctionArgumentException(Name,
                $"direction must be 'horizontal'/'h' or 'vertical'/'v'; got '{direction}'."),
        };
}
