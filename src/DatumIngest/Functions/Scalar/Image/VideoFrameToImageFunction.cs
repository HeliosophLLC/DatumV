using System.Runtime.InteropServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>video_frame_to_image(VideoFrame [, target_width [, target_height]]) → Image</c>.
/// Materialises a lazy <see cref="DataKind.VideoFrame"/> handle by routing it
/// through the per-query <see cref="VideoRegistry"/> on
/// <see cref="EvaluationFrame.VideoRegistry"/> and wrapping the decoded pixel
/// buffer in an <see cref="SKBitmap"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Resize overloads.</strong> The single-argument form decodes at the
/// source video's native resolution. Supplying <c>target_width</c> resizes
/// while preserving the source aspect ratio (height auto-computed). Supplying
/// both <c>target_width</c> and <c>target_height</c> resizes to those exact
/// dimensions. Resize happens inside swscale, fused with the YUV→BGRA pixel
/// conversion — no extra per-frame copy.
/// </para>
/// <para>
/// <strong>Output layout.</strong> The registry emits BGRA8888 — the native
/// pixel layout SkiaSharp uses on Windows — so the bytes copy straight into an
/// <see cref="SKBitmap"/> with no per-pixel format conversion.
/// </para>
/// <para>
/// <strong>Sequential access matters.</strong> The warm decoder behind the
/// registry is fast on in-order frame access (~11 ms/frame for the reference
/// 1080p H.264 clip; lower at smaller target resolutions) and slow on backward
/// seeks. Pipelines that iterate frames in <c>frame_index</c> order hit the
/// fast path; consumers that join on arbitrary indices pay seek-to-head
/// latency per non-sequential read.
/// </para>
/// </remarks>
public sealed class VideoFrameToImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "video_frame_to_image";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Materialises a VideoFrame handle into an Image by routing it through the per-query " +
        "VideoRegistry. Output is a BGRA8888 SKBitmap. Single-argument form decodes at the " +
        "source resolution; supplying target_width resizes while preserving aspect ratio; " +
        "supplying both target_width and target_height resizes to those exact dimensions.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Source resolution.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("frame", DataKindMatcher.Exact(DataKind.VideoFrame), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),

        // Aspect-preserving — width specified, height computed from source aspect.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("frame", DataKindMatcher.Exact(DataKind.VideoFrame), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("target_width", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),

        // Explicit dimensions.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("frame", DataKindMatcher.Exact(DataKind.VideoFrame), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("target_width", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("target_height", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VideoFrameToImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef frameArg = args[0];
        if (frameArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        if (frame.VideoRegistry is null)
        {
            throw new FunctionArgumentException(
                Name,
                "no VideoRegistry is bound to the current evaluation frame. " +
                $"{Name}() must be called from within a query pipeline that has " +
                "an ExecutionContext (the registry is allocated per-query).");
        }

        int? targetWidth = ReadOptionalDimension(args, index: 1, paramName: "target_width");
        int? targetHeight = ReadOptionalDimension(args, index: 2, paramName: "target_height");

        DataValue handle = frameArg.ToDataValue(frame.Target);
        (uint videoId, int frameIndex) = handle.AsVideoFrame();

        MaterializedFrame materialised = frame.VideoRegistry.Materialize(
            videoId, frameIndex, targetWidth, targetHeight);

        SKImageInfo info = new(
            materialised.Width,
            materialised.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque);

        SKBitmap bitmap = new(info);
        // Copy the decoded BGRA8888 buffer into the bitmap's owned pixel
        // memory. The bytes are in the exact layout SkiaSharp expects on
        // Windows — no per-pixel conversion needed.
        Marshal.Copy(
            materialised.Bgra8888Pixels,
            startIndex: 0,
            destination: bitmap.GetPixels(),
            length: materialised.Bgra8888Pixels.Length);

        return new ValueTask<ValueRef>(ValueRef.FromImage(bitmap));
    }

    private int? ReadOptionalDimension(ReadOnlySpan<ValueRef> args, int index, string paramName)
    {
        if (args.Length <= index) return null;
        if (args[index].IsNull)
        {
            throw new FunctionArgumentException(
                Name, $"{paramName} must not be NULL.");
        }
        int value = (int)args[index].ToInt64();
        if (value <= 0)
        {
            throw new FunctionArgumentException(
                Name, $"{paramName} must be positive (got {value}).");
        }
        return value;
    }
}
