using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Sam;

/// <summary>
/// <c>binary_mask_from_logits(plane Float32[], h Int32, w Int32, threshold Float32) → Image</c>.
/// Thresholds a single mask-logit plane at <c>logit &gt; threshold</c> and
/// packs the result as a binary grayscale-as-RGBA bitmap sized
/// <c>w × h</c>. Companion to <see cref="MaskNmsPlanesFunction"/> for
/// single-mask paths (prompted SAM, single-class segmentation,
/// downstream consumers that already know which plane they want).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout.</strong> <c>plane</c> is a flat Float32 buffer of
/// length <c>h * w</c> in row-major order. Threshold is exclusive
/// (<c>v &gt; threshold</c> = foreground); for SAM the canonical value
/// is <c>0.0</c> since the decoder emits signed logits.
/// </para>
/// <para>
/// <strong>Output convention.</strong> RGBA opaque, white = foreground,
/// black = background, equal channels. Same byte layout as U²-Net masks
/// + the everything-mode survivors, so a downstream <c>image_cutout(img,
/// mask)</c> consumes any of them uniformly.
/// </para>
/// </remarks>
public sealed class BinaryMaskFromLogitsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "binary_mask_from_logits";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Thresholds a Float32[h*w] mask-logit plane at logit > threshold and packs the result " +
        "as a binary grayscale-as-RGBA Image sized w*h. SAM canonical threshold is 0.0 (signed " +
        "logits). Output layout matches u2net masks for uniform downstream consumption.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("plane",     DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("h",         DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("w",         DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("threshold", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<BinaryMaskFromLogitsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }
        float[] plane = ActivationOps.ReadFloat32Array(args[0]);
        if (!args[1].TryToInt32(out int h) || !args[2].TryToInt32(out int w))
        {
            throw new FunctionArgumentException(Name, "h and w must be Int32-coercible.");
        }
        if (!args[3].TryToFloat(out float threshold))
        {
            throw new FunctionArgumentException(Name, "threshold must be Float32-coercible.");
        }
        if (h <= 0 || w <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"h and w must be positive; got [{h}, {w}].");
        }
        int expected = h * w;
        if (plane.Length != expected)
        {
            throw new FunctionArgumentException(Name,
                $"plane length {plane.Length} != h * w = {expected}.");
        }

        byte[] mask = new byte[expected];
        for (int i = 0; i < expected; i++)
        {
            mask[i] = plane[i] > threshold ? (byte)1 : (byte)0;
        }
        return new ValueTask<ValueRef>(
            ValueRef.FromImage(SamMaskOps.BuildBinaryMaskBitmap(mask, w, h)));
    }
}
