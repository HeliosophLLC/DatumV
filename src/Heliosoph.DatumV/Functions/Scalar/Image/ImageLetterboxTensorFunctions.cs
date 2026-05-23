using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models.Onnx;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// Aspect-preserving letterbox resize plus per-channel normalisation, packed
/// as a Float32 NCHW tensor. The standard preprocessing for object detectors
/// (YOLOX, SCRFD, RetinaFace) and any model whose accuracy depends on not
/// stretching the input — letterbox keeps the original aspect ratio by
/// padding the shorter side with a uniform fill value.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Call shape.</strong> <c>image_letterbox_tensor_chw(img, target_size, mean, std, pad_fill)</c>:
/// <list type="bullet">
///   <item><description><c>target_size</c> — single Int32. The output is a
///     square <c>target_size × target_size</c> canvas; the image is
///     aspect-scaled to fit inside it and padded along the shorter side.
///   </description></item>
///   <item><description><c>mean</c>, <c>std</c> — Float32[3]. Per-channel
///     normalisation: <c>output = (pixel/255 - mean[c]) / std[c]</c>.
///     Pass <c>imagenet_mean()</c>/<c>imagenet_std()</c> for ImageNet-trained
///     models; pass <c>[0,0,0]</c>/<c>[1,1,1]</c> for raw <c>pixel/255</c>.
///   </description></item>
///   <item><description><c>pad_fill</c> — Float32. The post-normalisation
///     value written into the padded region. YOLOX uses <c>114</c> (the raw
///     byte 114, paired with raw normalisation: mean=[0,0,0], std=[1/255, …]);
///     for ImageNet-normalised letterbox padding the canonical choice is
///     <c>0</c> (close to the per-channel zero-pixel value).
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Output layout.</strong> NCHW: all R values for the resized canvas,
/// then all G, then all B. Output length = <c>3 × target_size × target_size</c>.
/// Pair with <see cref="ImageLetterboxTensorHwcFunction"/> when the model
/// expects NHWC.
/// </para>
/// <para>
/// <strong>Resize filter.</strong> Bilinear (SkiaSharp's
/// <c>SKFilterMode.Linear</c>), matching the OpenCV / torchvision /
/// TensorFlow defaults used by canonical detector preprocessing.
/// </para>
/// <para>
/// <strong>What's not exposed.</strong> Per-channel <c>pad_fill</c> (each
/// channel padded with its own zero-equivalent). For raw padding (YOLOX) and
/// uniform-fill ImageNet padding the single-value form is what every shipping
/// detector uses; add a per-channel overload when a model demands it.
/// </para>
/// </remarks>
public sealed class ImageLetterboxTensorChwFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_letterbox_tensor_chw";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Aspect-preserving letterbox resize into a square canvas with per-channel normalisation, " +
        "packed as Float32 NCHW: image_letterbox_tensor_chw(img, target_size, mean, std, pad_fill). " +
        "Pad fill is in post-normalisation space (114 for YOLOX-style raw, 0 for ImageNet-norm). " +
        "Output length = 3 × target_size × target_size; layout = [R-plane, G-plane, B-plane].";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("target_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("mean",        DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("std",         DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("pad_fill",    DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageLetterboxTensorChwFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        int targetSize = LetterboxArgReader.ReadTargetSize(Name, args[1]);
        (float[] channelScale, float[] channelBias) =
            LetterboxArgReader.ReadMeanStdAsScaleBias(Name, args[2], args[3]);
        float padFill = LetterboxArgReader.ReadPadFill(Name, args[4]);

        SKBitmap source = imgArg.AsImage();
        float[] output = new float[3 * targetSize * targetSize];
        ImageTensorPrep.LetterboxAndPackNchw(
            source, output, targetSize, channelScale, channelBias, padFill, bgr: false);

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }
}

/// <summary>
/// Aspect-preserving letterbox resize plus per-channel normalisation, packed
/// as a Float32 NHWC tensor. The HWC sibling of
/// <see cref="ImageLetterboxTensorChwFunction"/> for models with input shape
/// <c>[B, H, W, C]</c> (typically TF-exported graphs). Layout is the only
/// difference — the resize, padding, and normalisation are identical.
/// </summary>
/// <remarks>
/// Output layout: pixels interleaved as <c>[R, G, B]</c> triples in row-major
/// order. Output length = <c>3 × target_size × target_size</c>, same as the
/// CHW variant, but the index is <c>(y * target_size + x) * 3 + c</c> instead
/// of <c>c * target_size² + y * target_size + x</c>.
/// </remarks>
public sealed class ImageLetterboxTensorHwcFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_letterbox_tensor_hwc";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Aspect-preserving letterbox resize into a square canvas with per-channel normalisation, " +
        "packed as Float32 NHWC: image_letterbox_tensor_hwc(img, target_size, mean, std, pad_fill). " +
        "Same args as the _chw variant; differs only in output layout — pixels are interleaved as " +
        "[R, G, B] triples instead of channel-major planes. Pair with NHWC ONNX graphs.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("target_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("mean",        DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("std",         DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("pad_fill",    DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageLetterboxTensorHwcFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        int targetSize = LetterboxArgReader.ReadTargetSize(Name, args[1]);
        (float[] channelScale, float[] channelBias) =
            LetterboxArgReader.ReadMeanStdAsScaleBias(Name, args[2], args[3]);
        float padFill = LetterboxArgReader.ReadPadFill(Name, args[4]);

        SKBitmap source = imgArg.AsImage();
        float[] output = new float[3 * targetSize * targetSize];
        ImageTensorPrep.LetterboxAndPackNhwc(
            source, output, targetSize, channelScale, channelBias, padFill, bgr: false);

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }
}

/// <summary>
/// Argument-reader helpers shared by the CHW and HWC letterbox functions.
/// Both functions take the same five arguments and validate them the same
/// way — extracting the readers keeps each function body focused on its
/// layout choice (which <c>ImageTensorPrep</c> overload to call).
/// </summary>
internal static class LetterboxArgReader
{
    /// <summary>
    /// Reads a positive Int32 target_size from the second argument.
    /// Accepts any integer kind via <c>ToInt32</c>; throws on null or
    /// non-positive.
    /// </summary>
    public static int ReadTargetSize(string fnName, ValueRef arg)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(fnName, "target_size must not be null.");
        }
        int targetSize = arg.ToInt32();
        if (targetSize <= 0)
        {
            throw new FunctionArgumentException(fnName,
                $"target_size must be positive, got {targetSize}.");
        }
        return targetSize;
    }

    /// <summary>
    /// Reads mean[3] and std[3] arrays, then converts them to per-channel
    /// scale and bias arrays in the on-the-wire form the
    /// <c>LetterboxAndPack*</c> kernels consume:
    /// <c>scale[c] = 1 / (255 * std[c])</c> and
    /// <c>bias[c] = -mean[c] / std[c]</c>. Matches the canonical
    /// <c>output = (pixel/255 - mean[c]) / std[c]</c> formula every
    /// detector preprocessing pipeline uses.
    /// </summary>
    public static (float[] Scale, float[] Bias) ReadMeanStdAsScaleBias(
        string fnName, ValueRef meanArg, ValueRef stdArg)
    {
        (float meanR, float meanG, float meanB) = ReadFloat3(fnName, meanArg, "mean");
        (float stdR, float stdG, float stdB) = ReadFloat3(fnName, stdArg, "std");
        if (stdR == 0f || stdG == 0f || stdB == 0f)
        {
            throw new FunctionArgumentException(fnName,
                $"std must not contain zero (got [{stdR}, {stdG}, {stdB}]); division by zero would corrupt the output.");
        }

        float[] scale =
        [
            1f / (255f * stdR),
            1f / (255f * stdG),
            1f / (255f * stdB),
        ];
        float[] bias =
        [
            -meanR / stdR,
            -meanG / stdG,
            -meanB / stdB,
        ];
        return (scale, bias);
    }

    /// <summary>
    /// Reads the pad_fill scalar. Already typed Float32 by the planner; this
    /// just rejects null with a function-specific error.
    /// </summary>
    public static float ReadPadFill(string fnName, ValueRef arg)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(fnName, "pad_fill must not be null.");
        }
        return arg.ToFloat();
    }

    /// <summary>
    /// Coerces a 3-element Float32 array into (r, g, b). Accepts both the
    /// <c>float[]</c> primitive-array payload and the inline <c>ValueRef[]</c>
    /// payload that comes from SQL array literals.
    /// </summary>
    private static (float R, float G, float B) ReadFloat3(string fnName, ValueRef arg, string paramName)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(fnName, $"{paramName} must not be null.");
        }

        if (arg.Materialized is float[] floatDirect)
        {
            if (floatDirect.Length != 3)
            {
                throw new FunctionArgumentException(fnName,
                    $"{paramName} must have exactly 3 elements (R, G, B), got {floatDirect.Length}.");
            }
            return (floatDirect[0], floatDirect[1], floatDirect[2]);
        }

        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        if (elements.Length != 3)
        {
            throw new FunctionArgumentException(fnName,
                $"{paramName} must have exactly 3 elements (R, G, B), got {elements.Length}.");
        }
        if (!elements[0].TryToFloat(out float r) ||
            !elements[1].TryToFloat(out float g) ||
            !elements[2].TryToFloat(out float b))
        {
            throw new FunctionArgumentException(fnName,
                $"{paramName} elements must be coercible to Float32.");
        }
        return (r, g, b);
    }
}
