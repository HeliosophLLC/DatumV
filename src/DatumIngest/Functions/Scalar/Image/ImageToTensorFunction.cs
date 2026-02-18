using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Resizes an image to <c>target_size</c> and flattens its RGB pixel data
/// into a normalized NCHW Float32 tensor. The most common preprocessing
/// shape required by ONNX vision models (ResNet, MobileNet, ViT, YOLO
/// backbones, CLIP image encoders, etc.).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Call shapes.</strong>
/// <list type="bullet">
///   <item>
///     <c>image_to_tensor(img, target_size)</c> — 2-arg shortcut. Mean
///     defaults to <c>[0, 0, 0]</c> and std to <c>[1, 1, 1]</c>, so the
///     output is just pixel/255 with no further normalization.
///   </item>
///   <item>
///     <c>image_to_tensor(img, target_size, mean, std)</c> — full form.
///     Pair with <c>imagenet_mean()</c>/<c>imagenet_std()</c> or
///     <c>clip_mean()</c>/<c>clip_std()</c> for the canonical presets.
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>target_size order.</strong> <c>[height, width]</c>, matching the
/// ONNX-vision tensor shape convention <c>[batch, channels, height, width]</c>.
/// For square inputs the order doesn't matter; for non-square the user must
/// supply the height first.
/// </para>
/// <para>
/// <strong>Output layout.</strong> Single-batch NCHW flattened in row-major
/// order: all R values for the resized image, then all G values, then all B.
/// Output length is <c>3 × height × width</c>. Per-element formula:
/// <c>(pixel_byte / 255.0 - mean[channel]) / std[channel]</c>.
/// </para>
/// <para>
/// <strong>Resize filter.</strong> Bilinear (SkiaSharp's
/// <c>SKSamplingOptions.Default</c>), matching PIL/Pillow's default and
/// the standard for ImageNet eval pipelines. If a model was trained with a
/// different filter (BICUBIC, NEAREST), this v1 doesn't expose that knob —
/// a follow-up can add an optional <c>filter</c> argument.
/// </para>
/// </remarks>
public sealed class ImageToTensorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_to_tensor";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Resizes an image and flattens its RGB pixel data into a normalized NCHW Float32 tensor: " +
        "image_to_tensor(img, target_size [, mean, std]). target_size is [height, width]. " +
        "Output length = 3 × height × width; per-element formula = (pixel/255 - mean[c]) / std[c]. " +
        "Omit mean/std to get raw pixel/255 (default mean=[0,0,0], std=[1,1,1]). " +
        "Pair with imagenet_mean()/imagenet_std() or clip_mean()/clip_std() for the canonical presets.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
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

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageToTensorFunction>(argumentKinds);

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

        (int height, int width) = ReadTargetSize(args[1]);

        // Default mean=[0,0,0], std=[1,1,1] when caller omits the normalize args.
        float meanR = 0f, meanG = 0f, meanB = 0f;
        float stdR = 1f, stdG = 1f, stdB = 1f;
        if (args.Length == 4)
        {
            (meanR, meanG, meanB) = ReadFloat3(args[2], paramName: "mean");
            (stdR, stdG, stdB)    = ReadFloat3(args[3], paramName: "std");
            if (stdR == 0f || stdG == 0f || stdB == 0f)
            {
                throw new FunctionArgumentException(Name,
                    $"std must not contain zero (got [{stdR}, {stdG}, {stdB}]); division by zero would corrupt the output.");
            }
        }

        SKBitmap source = imgArg.AsImage();

        // Resize directly into RGBA8888 with unpremultiplied alpha. The
        // single Resize call fuses both resampling and colour-space
        // conversion — cheaper than two passes, and stable across host
        // platforms (whose native bitmap layout may be BGRA).
        SKImageInfo targetInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = source.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"image_to_tensor: SkiaSharp failed to resize the source ({source.Width}×{source.Height} {source.ColorType}) to {width}×{height} RGBA8888.");

        int plane = height * width;
        float[] output = new float[3 * plane];

        nint pixelPtr = resized.GetPixels();
        unsafe
        {
            byte* p = (byte*)pixelPtr;
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    int srcOffset = (rowBase + x) * 4;
                    byte r = p[srcOffset + 0];
                    byte g = p[srcOffset + 1];
                    byte b = p[srcOffset + 2];
                    // alpha (srcOffset + 3) intentionally ignored.

                    int dstIdx = rowBase + x;
                    output[0 * plane + dstIdx] = (r / 255f - meanR) / stdR;
                    output[1 * plane + dstIdx] = (g / 255f - meanG) / stdG;
                    output[2 * plane + dstIdx] = (b / 255f - meanB) / stdB;
                }
            }
        }

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }

    /// <summary>
    /// Coerces a 2-element integer array into <c>(height, width)</c>. Throws
    /// when the array has a different length or carries a value that can't
    /// fit in a positive Int32. Accepts both the
    /// <see cref="ValueRef.FromPrimitiveArray{T}"/> typed-buffer payload
    /// (e.g. <c>int[]</c>) and the <c>ValueRef[]</c> inline-array payload
    /// — array-literal SQL expressions produce the latter, programmatic
    /// callers commonly produce the former.
    /// </summary>
    private static (int Height, int Width) ReadTargetSize(ValueRef arg)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(Name,
                "target_size must not be null.");
        }

        int height, width, count;
        if (arg.Materialized is int[] int32Direct)
        {
            count = int32Direct.Length;
            if (count != 2) ThrowTargetSizeArity(count);
            height = int32Direct[0];
            width  = int32Direct[1];
        }
        else if (arg.Materialized is long[] int64Direct)
        {
            count = int64Direct.Length;
            if (count != 2) ThrowTargetSizeArity(count);
            height = checked((int)int64Direct[0]);
            width  = checked((int)int64Direct[1]);
        }
        else
        {
            ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
            count = elements.Length;
            if (count != 2) ThrowTargetSizeArity(count);
            height = elements[0].ToInt32();
            width  = elements[1].ToInt32();
        }

        if (height <= 0 || width <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"target_size must have positive dimensions, got [{height}, {width}].");
        }
        return (height, width);
    }

    private static void ThrowTargetSizeArity(int got)
    {
        throw new FunctionArgumentException(Name,
            $"target_size must have exactly 2 elements [height, width], got {got}.");
    }

    /// <summary>
    /// Coerces a 3-element Float32 array into an <c>(r, g, b)</c> tuple. Used
    /// for both <c>mean</c> and <c>std</c>. Accepts both
    /// <see cref="ValueRef.FromPrimitiveArray{T}"/>'s typed <c>float[]</c>
    /// payload and the <c>ValueRef[]</c> inline-array payload.
    /// </summary>
    private static (float R, float G, float B) ReadFloat3(ValueRef arg, string paramName)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(Name,
                $"{paramName} must not be null.");
        }

        if (arg.Materialized is float[] floatDirect)
        {
            if (floatDirect.Length != 3)
            {
                throw new FunctionArgumentException(Name,
                    $"{paramName} must have exactly 3 elements (R, G, B), got {floatDirect.Length}.");
            }
            return (floatDirect[0], floatDirect[1], floatDirect[2]);
        }

        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        if (elements.Length != 3)
        {
            throw new FunctionArgumentException(Name,
                $"{paramName} must have exactly 3 elements (R, G, B), got {elements.Length}.");
        }
        if (!elements[0].TryToFloat(out float r) ||
            !elements[1].TryToFloat(out float g) ||
            !elements[2].TryToFloat(out float b))
        {
            throw new FunctionArgumentException(Name,
                $"{paramName} elements must be coercible to Float32.");
        }
        return (r, g, b);
    }
}
