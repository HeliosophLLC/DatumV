using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Per-pixel multiply blend of a source image by a single-channel mask:
/// <c>image_multiply(image, mask) → Image</c>. Each output RGB channel is
/// <c>src * mask.R / 255</c>; source alpha is preserved unchanged. Equivalent
/// to Photoshop's "Multiply" blend mode where the upper layer happens to be
/// grayscale. Returns a null Image when either argument is null.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is for.</strong> Dim or shade a photo by a continuous
/// weight map — depth maps from MiDaS / DPT, saliency masks from U²-Net,
/// attention maps, etc. Where the mask is white the source passes through
/// unchanged; where it is black the output is black; intermediate values
/// proportionally darken. Unlike <see cref="ImageCutoutFunction"/> (which
/// writes the mask into the alpha channel and leaves RGB alone), this
/// function modulates RGB and leaves alpha alone — the result is a normal
/// opaque image suitable for downstream JPEG/PNG encoding without any
/// transparency machinery.
/// </para>
/// <para>
/// <strong>Math.</strong> Per channel, <c>out = (src * mask.R) / 255</c>
/// with integer truncation — matches Pillow's <c>ImageChops.multiply</c> and
/// is what every "multiply blend" reference implementation produces. The
/// mask is read from its red channel only; grayscale masks in this codebase
/// (U²-Net, <see cref="DepthMapToImageFunction"/>, segmentation outputs)
/// all set R=G=B, so reading R is canonical. Mask alpha is ignored.
/// </para>
/// <para>
/// <strong>Sizing.</strong> The output has the source image's dimensions.
/// If the mask was emitted at a different size, it is resized to match the
/// source via <see cref="SKBitmap.Resize(SKImageInfo, SKSamplingOptions)"/>.
/// </para>
/// </remarks>
public sealed class ImageMultiplyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_multiply";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Per-pixel multiply blend of an image by a grayscale mask: each RGB "
        + "channel becomes src * mask.R / 255; source alpha is preserved. "
        + "Equivalent to Photoshop's Multiply blend mode with a grayscale "
        + "upper layer. Use to depth-shade or attention-dim a photo with "
        + "outputs from MiDaS / DPT / U²-Net / segmentation models.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("mask",  DataKindMatcher.Exact(DataKind.Image)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageMultiplyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        ValueRef maskArg = args[1];

        if (imgArg.IsNull || maskArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        SKBitmap srcBitmap = imgArg.AsImage();
        SKBitmap maskBitmap = maskArg.AsImage();

        int width = srcBitmap.Width;
        int height = srcBitmap.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"image_multiply: source image has non-positive dimensions ({width}×{height}).");
        }

        // Source → RGBA8888 with unpremultiplied alpha. AsImage may hand back
        // a platform-native colour type (BGRA on Windows), so we normalise
        // before the per-pixel multiply.
        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap srcRgba = new(rgbaInfo);
        if (!srcBitmap.CopyTo(srcRgba, SKColorType.Rgba8888))
        {
            throw new InvalidOperationException(
                $"image_multiply: failed to convert source image to RGBA8888 "
                + $"(source colour type: {srcBitmap.ColorType}).");
        }

        // Mask → RGBA8888 sized to the source. Resize doubles as a colour-
        // type conversion.
        using SKBitmap maskRgba = maskBitmap.Resize(rgbaInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"image_multiply: failed to resize/convert the mask to {width}×{height} RGBA8888.");

        // Output bitmap. Ownership transfers to the ValueRef — no `using`.
        SKBitmap output = new(rgbaInfo);
        nint srcPtr = srcRgba.GetPixels();
        nint maskPtr = maskRgba.GetPixels();
        nint outPtr = output.GetPixels();

        int planeSize = width * height;
        unsafe
        {
            byte* s = (byte*)srcPtr;
            byte* m = (byte*)maskPtr;
            byte* d = (byte*)outPtr;
            for (int i = 0; i < planeSize; i++)
            {
                int o = i * 4;
                byte w = m[o + 0];                   // mask is grayscale, R=G=B
                d[o + 0] = (byte)((s[o + 0] * w) / 255);
                d[o + 1] = (byte)((s[o + 1] * w) / 255);
                d[o + 2] = (byte)((s[o + 2] * w) / 255);
                d[o + 3] = s[o + 3];                 // alpha preserved
            }
        }

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }
}
