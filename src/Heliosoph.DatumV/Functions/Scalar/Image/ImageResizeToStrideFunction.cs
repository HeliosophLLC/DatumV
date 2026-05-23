using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// Aspect-preserving resize where the longest side is capped at
/// <c>max_side</c> and both dimensions are rounded to the nearest
/// multiple of <c>stride</c>. The canonical preprocessing policy for
/// fully-dynamic-shape segmentation models — PaddleOCR's
/// <c>DetResizeForTest</c> for PP-OCR-det, U²-Net's input policy, and
/// most DBNet-family text detectors.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate primitive.</strong> Unlike letterbox (square
/// canvas, fixed size) or stretch resize (loses aspect ratio), stride
/// rounding produces an output whose dimensions vary per input image —
/// the ONNX session has to accept dynamic spatial dims for this policy
/// to make sense. PP-OCR-det's <c>[N, 3, H, W]</c> input shape is the
/// typical consumer. Returns the resized image rather than a tensor so
/// the body can call <c>image_width</c> / <c>image_height</c> on it and
/// compute scale-back factors for post-processing.
/// </para>
/// <para>
/// <strong>Algorithm.</strong> Compute the smaller of <c>max_side / origW</c>
/// and <c>max_side / origH</c> as the scale ratio (only applied if the
/// longest side exceeds <c>max_side</c>; smaller images keep their
/// original aspect). Round each scaled dimension to the nearest multiple
/// of <c>stride</c>, with a minimum of one stride. Resize via SkiaSharp's
/// bilinear filter — matches PIL/Pillow's default and what PaddleOCR's
/// training pipeline uses for eval.
/// </para>
/// <para>
/// <strong>Pair with.</strong> <see cref="ImageToTensorChwFunction"/> /
/// <see cref="ImageToTensorHwcFunction"/> (after taking <c>image_height</c> /
/// <c>image_width</c> of the result) to produce the model input tensor.
/// Post-processing functions then need scale-back factors computed as
/// <c>image_width(orig) / image_width(resized)</c> per axis.
/// </para>
/// </remarks>
public sealed class ImageResizeToStrideFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_resize_to_stride";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Aspect-preserving resize: longest side capped at max_side, both dims rounded to multiples of stride. " +
        "image_resize_to_stride(img, max_side INT, stride INT) → Image. " +
        "Canonical preprocessing for fully-dynamic-shape segmentation models (PaddleOCR-det, DBNet family).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",      DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("max_side", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("stride",   DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageResizeToStrideFunction>(argumentKinds);

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
        int maxSide = args[1].ToInt32();
        int stride = args[2].ToInt32();
        if (maxSide <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"max_side must be positive, got {maxSide}.");
        }
        if (stride <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"stride must be positive, got {stride}.");
        }

        SKBitmap source = args[0].AsImage();
        int origW = source.Width;
        int origH = source.Height;

        // Scale ratio: smaller of (max_side / dim) for each axis, applied
        // only when the longest side exceeds max_side. Mirrors PaddleOCR's
        // DetResizeForTest behaviour exactly.
        float ratio = 1f;
        int longest = System.Math.Max(origW, origH);
        if (longest > maxSide)
        {
            ratio = (float)maxSide / longest;
        }

        int targetW = (int)MathF.Round(origW * ratio);
        int targetH = (int)MathF.Round(origH * ratio);

        // Round to nearest multiple of stride; clamp to at least one stride
        // so an input smaller than stride doesn't collapse to zero pixels.
        int snappedW = System.Math.Max(stride, (int)MathF.Round(targetW / (float)stride) * stride);
        int snappedH = System.Math.Max(stride, (int)MathF.Round(targetH / (float)stride) * stride);

        SKImageInfo targetInfo = new(snappedW, snappedH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        SKBitmap resized = source.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear))
            ?? throw new InvalidOperationException(
                $"{Name}: SkiaSharp failed to resize the source ({origW}×{origH} {source.ColorType}) to {snappedW}×{snappedH} RGBA8888.");

        return new ValueTask<ValueRef>(ValueRef.FromImage(resized));
    }
}
