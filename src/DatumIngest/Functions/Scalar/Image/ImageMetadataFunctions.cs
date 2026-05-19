using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>pixel_count(img Image) → Int32</c>. Returns <c>width × height</c>.
/// The planner pass <see cref="ImageMetadataLowerer"/> rewrites this into
/// <c>image_width(img) * image_height(img)</c> so the multiplication
/// composes through the elidable accessors — both sides become struct
/// reads and CSE collapses any sibling width/height calls in the same
/// query. This runtime body is the slow-path fallback for the unusual case
/// where the lowering can't fire (e.g. an unresolved
/// <see cref="DataKind.Unknown"/> argument that the planner skipped).
/// </summary>
public sealed class ImagePixelCountFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pixel_count";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the total pixel count of an Image (width × height) as Int32. "
        + "Lowered at plan time to image_width(img) * image_height(img); the runtime "
        + "body is the slow-path fallback.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("img", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImagePixelCountFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        DataValue dv = arg.InlineDataValue;
        int width = dv.ImageWidth;
        int height = dv.ImageHeight;
        if (width == 0 || height == 0)
        {
            SKBitmap bmp = arg.AsImage();
            width = bmp.Width;
            height = bmp.Height;
        }
        return new ValueTask<ValueRef>(ValueRef.FromInt32(width * height));
    }
}

/// <summary>
/// <c>dimensions(img Image, format String) → Int32[]</c>. Returns the
/// image's dimensions in the requested axis order. Supported formats
/// (case-insensitive):
/// <list type="bullet">
///   <item><c>'WH'</c>  → <c>[width, height]</c></item>
///   <item><c>'WHC'</c> → <c>[width, height, channels]</c></item>
///   <item><c>'HWC'</c> → <c>[height, width, channels]</c></item>
///   <item><c>'CHW'</c> → <c>[channels, height, width]</c></item>
/// </list>
/// The planner pass <see cref="ImageMetadataLowerer"/> rewrites the call
/// into the corresponding <c>array(...)</c> of elidable accessors when the
/// <c>format</c> argument is a string literal. This runtime body is the
/// slow-path fallback for the non-literal case.
/// </summary>
public sealed class ImageDimensionsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "dimensions";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns image dimensions as Int32[] in the requested axis order. "
        + "Formats: 'WH', 'WHC', 'HWC', 'CHW'. Lowered at plan time to array(...) "
        + "of elidable accessors when the format is a literal.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",    DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("format", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageDimensionsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Int32));
        }
        string format = args[1].AsString().ToUpperInvariant();
        DataValue dv = args[0].InlineDataValue;
        int width = dv.ImageWidth;
        int height = dv.ImageHeight;
        int channels = dv.ImageChannels;
        if (width == 0 || height == 0 || (FormatNeedsChannels(format) && channels == 0))
        {
            SKBitmap bmp = args[0].AsImage();
            if (width == 0) width = bmp.Width;
            if (height == 0) height = bmp.Height;
            if (channels == 0) channels = bmp.BytesPerPixel;
        }
        int[] dims = format switch
        {
            "WH"  => [width, height],
            "WHC" => [width, height, channels],
            "HWC" => [height, width, channels],
            "CHW" => [channels, height, width],
            _ => throw new FunctionArgumentException(Name,
                $"unknown format '{format}'. Supported: WH, WHC, HWC, CHW."),
        };
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(dims, DataKind.Int32));
    }

    /// <summary>
    /// Whether the format includes the channels axis. Used by both the
    /// runtime path (to know whether to fall through to a decode for
    /// channels) and conceptually mirrored by the planner pass.
    /// </summary>
    internal static bool FormatNeedsChannels(string upperFormat) =>
        upperFormat is "WHC" or "HWC" or "CHW";
}
