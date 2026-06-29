using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Video;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Video;

/// <summary>
/// <c>video_width(v Video) → Int32</c>. Returns the pixel width parsed from the
/// video's inline metadata. Every Video production site routes through
/// <see cref="Heliosoph.DatumV.Functions.Video.VideoDataValueFactory"/>, which uses
/// FFmpeg (via <see cref="Heliosoph.DatumV.Functions.Video.VideoHeaderParser"/>) to
/// extract dimensions at construction time. Returns NULL only when FFmpeg fails
/// to open the bytes (corrupt input, missing demuxer, no video stream).
/// </summary>
public sealed class VideoWidthFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "video_width";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.VideoWidth;

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
public sealed class VideoHeightFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "video_height";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.VideoHeight;

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

/// <summary>
/// <c>video_duration(v Video) → Float64</c>. Returns the clip length in seconds.
/// On the common (stamped) path the value is derived inline as
/// <c>video_frame_count ÷ fps</c> — approximate because the inline fps is 8.8
/// fixed-point (a 23.976 rate rounds), and served by the planner-time elider
/// without dispatching here. When the frame count or fps wasn't stamped at
/// ingest (some MKV / fragmented MP4), this falls back to the container's
/// authoritative recorded duration via
/// <see cref="VideoHeaderParser.TryReadDurationSeconds"/>. Returns NULL when the
/// container records no duration or FFmpeg can't open it.
/// </summary>
public sealed class VideoDurationFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "video_duration";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.VideoDuration;

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the duration in seconds of a Video value as Float64. Derived "
        + "inline from the stamped frame count and fps when available (fps is "
        + "8.8 fixed-point, so the inline value is approximate); else read from "
        + "the container's recorded duration. NULL when no duration is recorded.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("v", DataKindMatcher.Exact(DataKind.Video))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VideoDurationFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));
        }

        // Fast path mirrors the elider: frame count ÷ fps when both are stamped.
        DataValue dv = arg.InlineDataValue;
        uint frames = dv.VideoFrameCount;
        ushort fpsX256 = dv.VideoFpsX256;
        if (frames != 0 && fpsX256 != 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromFloat64(frames / (fpsX256 / 256.0)));
        }

        // Slow path: the container's authoritative recorded duration.
        DataValue videoValue = arg.ToDataValue(frame.Source);
        byte[] videoBytes = videoValue.AsVideo(frame.Source, frame.SidecarRegistry);
        double? seconds = VideoHeaderParser.TryReadDurationSeconds(videoBytes);
        return new ValueTask<ValueRef>(
            seconds is { } s ? ValueRef.FromFloat64(s) : ValueRef.Null(DataKind.Float64));
    }
}
