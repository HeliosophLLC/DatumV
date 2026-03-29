using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Video;

/// <summary>
/// <c>video_width(v Video) → Int32</c>. Returns the pixel width parsed from the
/// video's inline metadata. Every Video production site routes through
/// <see cref="DatumIngest.Functions.Video.VideoDataValueFactory"/>, which uses
/// FFmpeg (via <see cref="DatumIngest.Functions.Video.VideoHeaderParser"/>) to
/// extract dimensions at construction time. Returns NULL only when FFmpeg fails
/// to open the bytes (corrupt input, missing demuxer, no video stream).
/// </summary>
public sealed class VideoWidthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "video_width";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the pixel width of a Video value as Int32, or NULL when the dimensions "
        + "were not stamped at ingest. Pair with video_height() for aspect-preserving work.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("v", DataKindMatcher.Exact(DataKind.Video))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VideoWidthFunction>(argumentKinds);

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
        ushort width = arg.InlineDataValue.VideoWidth;
        return new ValueTask<ValueRef>(
            width != 0
                ? ValueRef.FromInt32(width)
                : ValueRef.Null(DataKind.Int32));
    }
}

/// <summary>
/// <c>video_height(v Video) → Int32</c>. Sibling to <see cref="VideoWidthFunction"/>.
/// Returns NULL when inline metadata wasn't stamped at ingest.
/// </summary>
public sealed class VideoHeightFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "video_height";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the pixel height of a Video value as Int32, or NULL when the dimensions "
        + "were not stamped at ingest.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("v", DataKindMatcher.Exact(DataKind.Video))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VideoHeightFunction>(argumentKinds);

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
        ushort height = arg.InlineDataValue.VideoHeight;
        return new ValueTask<ValueRef>(
            height != 0
                ? ValueRef.FromInt32(height)
                : ValueRef.Null(DataKind.Int32));
    }
}
