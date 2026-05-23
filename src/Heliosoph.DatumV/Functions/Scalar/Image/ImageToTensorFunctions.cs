using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// Resizes an image to <c>target_size</c> and flattens its RGB pixel data
/// into a normalized Float32 NCHW tensor — the canonical preprocessing
/// shape for most ONNX vision models (ResNet, MobileNet, ViT, YOLO
/// backbones, CLIP image encoders, etc.).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stretch resize, not letterbox.</strong> The output is the
/// requested <c>target_size = [height, width]</c> regardless of the input's
/// aspect ratio — the image gets squashed/stretched to fit. For aspect-
/// preserving preprocessing (detectors), use
/// <see cref="ImageLetterboxTensorChwFunction"/>.
/// </para>
/// <para>
/// <strong>Call shapes.</strong>
/// <list type="bullet">
///   <item>
///     <c>image_to_tensor_chw(img, target_size)</c> — 2-arg shortcut. Mean
///     defaults to <c>[0, 0, 0]</c> and std to <c>[1, 1, 1]</c>, so the
///     output is just pixel/255 with no further normalization.
///   </item>
///   <item>
///     <c>image_to_tensor_chw(img, target_size, mean, std)</c> — full form.
///     Pair with <c>imagenet_mean()</c>/<c>imagenet_std()</c> or
///     <c>clip_mean()</c>/<c>clip_std()</c> for the canonical presets.
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>target_size order.</strong> <c>[height, width]</c>, matching the
/// NCHW tensor convention <c>[batch, channels, height, width]</c>. For
/// square inputs the order doesn't matter; for non-square the user must
/// supply the height first.
/// </para>
/// <para>
/// <strong>Output layout.</strong> NCHW: all R values for the resized
/// image, then all G, then all B. Output length is
/// <c>3 × height × width</c>. Per-element formula:
/// <c>(pixel_byte / 255.0 - mean[channel]) / std[channel]</c>. Pair with
/// <see cref="ImageToTensorHwcFunction"/> when the model expects NHWC.
/// </para>
/// <para>
/// <strong>Resize filter.</strong> Bilinear (SkiaSharp's
/// <c>SKFilterMode.Linear</c>), matching the OpenCV, torchvision, and
/// TensorFlow defaults used by virtually all shipping ImageNet eval
/// pipelines. (Modern PIL defaults to bicubic since 9.1; CLIP/SigLIP
/// preprocessing is also bicubic — pair those with a future bicubic mode.)
/// </para>
/// </remarks>
public sealed class ImageToTensorChwFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_to_tensor_chw";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Stretch-resizes an image and flattens its RGB pixels into a normalized NCHW Float32 tensor: " +
        "image_to_tensor_chw(img, target_size [, mean, std]). target_size is [height, width]. " +
        "Output length = 3 × height × width; per-element formula = (pixel/255 - mean[c]) / std[c]. " +
        "Omit mean/std to get raw pixel/255. Pair with image_to_tensor_hwc for NHWC graphs, or " +
        "image_letterbox_tensor_chw when aspect ratio matters (detectors).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ImageToTensorShared.BuildSignatures();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageToTensorChwFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => ImageToTensorShared.ExecuteAsync(Name, arguments, layout: TensorLayout.Chw);
}

/// <summary>
/// NHWC sibling of <see cref="ImageToTensorChwFunction"/>: same stretch
/// resize and per-channel normalization, but the output is interleaved
/// per pixel — <c>dest[(y*W + x)*3 + c]</c> instead of channel-major
/// planes. ONNX models with input shape <c>[B, H, W, C]</c> (typically
/// TF-exported graphs) consume this layout directly.
/// </summary>
/// <remarks>
/// <para>
/// All other semantics — <c>target_size = [height, width]</c>, mean/std
/// per-channel, bilinear resize, 2-arg shortcut for raw pixel/255 — match
/// the CHW variant exactly. The only difference is the output index layout.
/// </para>
/// </remarks>
public sealed class ImageToTensorHwcFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_to_tensor_hwc";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Stretch-resizes an image and flattens its RGB pixels into a normalized NHWC Float32 tensor: " +
        "image_to_tensor_hwc(img, target_size [, mean, std]). Same args as image_to_tensor_chw; " +
        "differs only in output layout — pixels are interleaved as [R, G, B] triples instead of " +
        "channel-major planes. Pair with NHWC ONNX graphs.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ImageToTensorShared.BuildSignatures();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageToTensorHwcFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => ImageToTensorShared.ExecuteAsync(Name, arguments, layout: TensorLayout.Hwc);
}

/// <summary>
/// BGR sibling of <see cref="ImageToTensorChwFunction"/>. Same stretch
/// resize and per-channel normalisation, but emits the tensor in BGR
/// channel order. Used by legacy detectors and depth estimators trained
/// against cv2-loaded BGR images (MiDaS-small v2.1, some YOLO variants).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mean / std ordering.</strong> The <c>mean</c> and <c>std</c>
/// arrays are indexed in <em>output channel</em> order — i.e. BGR. Pass
/// <c>[meanB, meanG, meanR]</c> if the upstream Python reference cites
/// the values in BGR; pass <c>[meanR, meanG, meanB]</c> with a manual
/// swap if it cites RGB. MiDaS-small uses symmetric-enough ImageNet
/// stats that the order rarely matters in practice; for asymmetric
/// statistics it matters a lot.
/// </para>
/// </remarks>
public sealed class ImageToTensorChwBgrFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_to_tensor_chw_bgr";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "BGR sibling of image_to_tensor_chw. Stretch-resizes an image and packs BGR "
        + "(not RGB) pixels into a normalized NCHW Float32 tensor: "
        + "image_to_tensor_chw_bgr(img, target_size [, mean, std]). Use for models trained "
        + "on cv2-loaded BGR images (MiDaS-small v2.1, some YOLO exports).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ImageToTensorShared.BuildSignatures();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageToTensorChwBgrFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
        => ImageToTensorShared.ExecuteAsync(Name, arguments, layout: TensorLayout.Chw, bgr: true);
}

/// <summary>Tensor memory layout selector for the shared image-to-tensor helper.</summary>
internal enum TensorLayout
{
    /// <summary>Channel-major: <c>dest[c*H*W + y*W + x]</c>. NCHW ONNX inputs.</summary>
    Chw,
    /// <summary>Interleaved: <c>dest[(y*W + x)*3 + c]</c>. NHWC ONNX inputs.</summary>
    Hwc,
}

/// <summary>
/// Shared signature + execution logic for the CHW and HWC stretch-resize
/// image-to-tensor functions. Both variants take identical arguments and
/// run identical math; only the output index differs. Centralizing the
/// arg parsing and resize loop here keeps both function classes thin and
/// guarantees the per-element math doesn't drift between layouts.
/// </summary>
internal static class ImageToTensorShared
{
    public static IReadOnlyList<FunctionSignatureVariant> BuildSignatures() =>
    [
        // Full form: (image, target_size INT[], mean FLOAT32[], std FLOAT32[])
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("target_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("mean",        DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("std",         DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
        // Shortcut form: (image, target_size INT[]) — defaults mean=[0,0,0], std=[1,1,1]
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("target_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    public static ValueTask<ValueRef> ExecuteAsync(
        string fnName,
        ReadOnlyMemory<ValueRef> arguments,
        TensorLayout layout,
        bool bgr = false)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];

        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        (int height, int width) = ReadTargetSize(fnName, args[1]);

        // Default mean=[0,0,0], std=[1,1,1] when caller omits the normalize args.
        float meanR = 0f, meanG = 0f, meanB = 0f;
        float stdR = 1f, stdG = 1f, stdB = 1f;
        if (args.Length == 4)
        {
            (meanR, meanG, meanB) = ReadFloat3(fnName, args[2], paramName: "mean");
            (stdR, stdG, stdB)    = ReadFloat3(fnName, args[3], paramName: "std");
            if (stdR == 0f || stdG == 0f || stdB == 0f)
            {
                throw new FunctionArgumentException(fnName,
                    $"std must not contain zero (got [{stdR}, {stdG}, {stdB}]); division by zero would corrupt the output.");
            }
        }

        SKBitmap source = imgArg.AsImage();

        // Resize directly into RGBA8888 with unpremultiplied alpha. The
        // single Resize call fuses both resampling and colour-space
        // conversion — cheaper than two passes, and stable across host
        // platforms (whose native bitmap layout may be BGRA).
        SKImageInfo targetInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = source.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear))
            ?? throw new InvalidOperationException(
                $"{fnName}: SkiaSharp failed to resize the source ({source.Width}×{source.Height} {source.ColorType}) to {width}×{height} RGBA8888.");

        int plane = height * width;
        float[] output = new float[3 * plane];

        // The unsafe block below reads through `p` (a raw byte* derived from
        // resized.GetPixels()). The JIT's reachability analysis can decide
        // `resized` and `source` aren't "used" inside the unsafe block — only
        // `p` is — and stop rooting them. Under per-row dispatch pressure
        // (many rows in close succession + concurrent GC), SKBitmap's
        // finalizer can fire mid-loop and free the native pixel buffer that
        // `p` is reading. Garbage / NaN values then flow into the model and
        // crash native ORT.Run downstream. GC.KeepAlive after the loop
        // forces the JIT to keep both alive until that point.
        nint pixelPtr = resized.GetPixels();
        unsafe
        {
            byte* p = (byte*)pixelPtr;
            // BGR swaps channel-0 and channel-2 reads from the source buffer
            // (which is always RGBA after the Resize call above). The mean/std
            // arrays are still indexed [0]=channel-0, [1]=channel-1, [2]=channel-2
            // in the *output* order — i.e. BGR callers pass mean/std in BGR
            // order. MiDaS-small v2.1's ImageNet stats happen to be symmetric
            // (~0.45 each) so the difference is small, but the contract is
            // explicit: index follows the output channel order.
            if (layout == TensorLayout.Chw)
            {
                for (int y = 0; y < height; y++)
                {
                    int rowBase = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int srcOffset = (rowBase + x) * 4;
                        byte c0 = bgr ? p[srcOffset + 2] : p[srcOffset + 0];
                        byte c1 = p[srcOffset + 1];
                        byte c2 = bgr ? p[srcOffset + 0] : p[srcOffset + 2];

                        int dstIdx = rowBase + x;
                        output[0 * plane + dstIdx] = (c0 / 255f - meanR) / stdR;
                        output[1 * plane + dstIdx] = (c1 / 255f - meanG) / stdG;
                        output[2 * plane + dstIdx] = (c2 / 255f - meanB) / stdB;
                    }
                }
            }
            else // Hwc
            {
                for (int y = 0; y < height; y++)
                {
                    int rowBase = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int srcOffset = (rowBase + x) * 4;
                        byte c0 = bgr ? p[srcOffset + 2] : p[srcOffset + 0];
                        byte c1 = p[srcOffset + 1];
                        byte c2 = bgr ? p[srcOffset + 0] : p[srcOffset + 2];

                        int dstIdx = (rowBase + x) * 3;
                        output[dstIdx]     = (c0 / 255f - meanR) / stdR;
                        output[dstIdx + 1] = (c1 / 255f - meanG) / stdG;
                        output[dstIdx + 2] = (c2 / 255f - meanB) / stdB;
                    }
                }
            }
        }

        // Force both bitmaps to stay rooted through the end of the unsafe
        // pointer read. See the comment above the unsafe block for why this
        // matters — TL;DR the JIT can release locals it considers dead
        // and SKBitmap's finalizer can fire mid-loop on freed pixels.
        GC.KeepAlive(resized);
        GC.KeepAlive(source);

        if (DatumActivity.Scalars.HasListeners())
        {
            int nanCount = 0, infCount = 0;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int k = 0; k < output.Length; k++)
            {
                float v = output[k];
                if (float.IsNaN(v)) nanCount++;
                else if (float.IsInfinity(v)) infCount++;
                else { if (v < min) min = v; if (v > max) max = v; }
            }
            DatumActivity.Scalars.Trace(
                $"[img2tensor:{fnName}] {source.Width}x{source.Height}->{width}x{height} bgr={bgr} layout={layout} " +
                $"len={output.Length} sample=[{output[0]:F3},{output[1]:F3},{output[2]:F3}] " +
                $"min={(min == float.PositiveInfinity ? double.NaN : min):F3} max={(max == float.NegativeInfinity ? double.NaN : max):F3} " +
                $"nan={nanCount} inf={infCount}");
        }

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }

    /// <summary>
    /// Coerces a 2-element integer array into <c>(height, width)</c>. Throws
    /// when the array has a different length or carries a value that can't
    /// fit in a positive Int32.
    /// </summary>
    internal static (int Height, int Width) ReadTargetSize(string fnName, ValueRef arg)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(fnName, "target_size must not be null.");
        }

        int height, width, count;
        if (arg.Materialized is int[] int32Direct)
        {
            count = int32Direct.Length;
            if (count != 2) ThrowTargetSizeArity(fnName, count);
            height = int32Direct[0];
            width  = int32Direct[1];
        }
        else if (arg.Materialized is long[] int64Direct)
        {
            count = int64Direct.Length;
            if (count != 2) ThrowTargetSizeArity(fnName, count);
            height = checked((int)int64Direct[0]);
            width  = checked((int)int64Direct[1]);
        }
        else
        {
            ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
            count = elements.Length;
            if (count != 2) ThrowTargetSizeArity(fnName, count);
            height = elements[0].ToInt32();
            width  = elements[1].ToInt32();
        }

        if (height <= 0 || width <= 0)
        {
            throw new FunctionArgumentException(fnName,
                $"target_size must have positive dimensions, got [{height}, {width}].");
        }
        return (height, width);
    }

    private static void ThrowTargetSizeArity(string fnName, int got)
    {
        throw new FunctionArgumentException(fnName,
            $"target_size must have exactly 2 elements [height, width], got {got}.");
    }

    /// <summary>
    /// Coerces a 3-element Float32 array into an <c>(r, g, b)</c> tuple. Used
    /// for both <c>mean</c> and <c>std</c>. Accepts both
    /// <see cref="ValueRef.FromPrimitiveArray{T}"/>'s typed <c>float[]</c>
    /// payload and the <c>ValueRef[]</c> inline-array payload.
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
