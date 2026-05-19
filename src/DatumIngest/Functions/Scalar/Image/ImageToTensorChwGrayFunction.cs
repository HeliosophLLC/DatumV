using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// Grayscale sibling of <see cref="ImageToTensorChwFunction"/>. Converts an
/// RGB image to a single-channel luma tensor, stretch-resized to
/// <c>target_size = [height, width]</c> and flattened into NCHW Float32
/// layout. Output length is <c>height × width</c> (one channel), not
/// <c>3 × height × width</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Use cases.</strong> SCUNet-gray, single-channel denoising,
/// document / medical / scientific imaging models that take grayscale
/// input. Roughly 3× faster than running the RGB variant on a
/// channel-replicated tensor since the network's first conv has 3× less
/// FLOPs at the input.
/// </para>
/// <para>
/// <strong>RGB → luma conversion.</strong> ITU-R BT.601 coefficients:
/// <c>Y = 0.299·R + 0.587·G + 0.114·B</c>. The standard for
/// JPEG / OpenCV grayscale conversion. Models trained against
/// <c>cv2.imread(path, cv2.IMREAD_GRAYSCALE)</c> or PIL's
/// <c>Image.convert('L')</c> see the same coefficients; matched-condition
/// inference works out of the box.
/// </para>
/// <para>
/// <strong>Call shapes.</strong>
/// <list type="bullet">
///   <item>
///     <c>image_to_tensor_chw_gray(img, target_size)</c> — 2-arg shortcut.
///     Mean=0, std=1; output is raw <c>luma/255</c> in <c>[0, 1]</c>. Pair
///     with SCUNet-style models that consume <c>[0, 1]</c> input directly.
///   </item>
///   <item>
///     <c>image_to_tensor_chw_gray(img, target_size, mean, std)</c> —
///     scalar mean/std (not arrays) since there's only one channel.
///     Per-element formula <c>(luma/255 - mean) / std</c>.
///   </item>
///   <item>
///     Per-element formula: <c>luma = 0.299·R + 0.587·G + 0.114·B</c>,
///     <c>output = (luma/255 - mean) / std</c>.
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>Resize filter.</strong> Bilinear (SkiaSharp's
/// <c>SKSamplingOptions.Default</c>), matching PIL/Pillow's default.
/// </para>
/// </remarks>
public sealed class ImageToTensorChwGrayFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_to_tensor_chw_gray";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Grayscale variant of image_to_tensor_chw. Converts RGB to luma via BT.601 " +
        "(Y = 0.299·R + 0.587·G + 0.114·B), stretch-resizes to target_size, and emits a " +
        "single-channel NCHW Float32 tensor of length height × width. " +
        "Call shapes: image_to_tensor_chw_gray(img, target_size [, mean, std]); mean/std are " +
        "scalars (not arrays) since there's only one channel. Pair with SCUNet-gray and other " +
        "single-channel denoising / restoration models.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Full form: (image, target_size INT[], mean FLOAT32, std FLOAT32)
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img",         DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("target_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("mean",        DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("std",         DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
        // Shortcut form: (image, target_size INT[]) — defaults mean=0, std=1
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
        FunctionMetadata.Validate<ImageToTensorChwGrayFunction>(argumentKinds);

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

        (int height, int width) = ImageToTensorShared.ReadTargetSize(Name, args[1]);

        float mean = 0f;
        float std = 1f;
        if (args.Length == 4)
        {
            mean = args[2].AsFloat32();
            std = args[3].AsFloat32();
            if (std == 0f)
            {
                throw new FunctionArgumentException(Name,
                    $"std must not be zero (got {std}); division by zero would corrupt the output.");
            }
        }

        SKBitmap source = imgArg.AsImage();

        // Resize into RGBA8888 first; convert to luma in the per-pixel loop
        // below. Doing the resize-then-luma in two passes mirrors how PIL
        // (.convert('L') after .resize()) and OpenCV (cvtColor after
        // resize) handle this, so models trained against those reference
        // pipelines see matched preprocessing.
        SKImageInfo targetInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = source.Resize(targetInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"{Name}: SkiaSharp failed to resize the source " +
                $"({source.Width}×{source.Height} {source.ColorType}) to {width}×{height} RGBA8888.");

        int plane = height * width;
        float[] output = new float[plane];

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
                    // BT.601 luma. Fixed-point integer-coefficient ports
                    // (cv2's `RGB2GRAY`) round the same as this; results
                    // match within 1 ULP across the dynamic range.
                    float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                    output[rowBase + x] = (luma / 255f - mean) / std;
                }
            }
        }

        // Keep both bitmaps rooted through the unsafe pointer read — see
        // ImageToTensorShared for the why (JIT-eliding-locals + GC racing
        // SKBitmap finalizers under per-row dispatch pressure).
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
                $"[img2tensor:{Name}] {source.Width}x{source.Height}->{width}x{height} " +
                $"len={output.Length} sample=[{output[0]:F3},{output[1]:F3},{output[2]:F3}] " +
                $"min={(min == float.PositiveInfinity ? double.NaN : min):F3} max={(max == float.NegativeInfinity ? double.NaN : max):F3} " +
                $"nan={nanCount} inf={infCount}");
        }

        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }
}
