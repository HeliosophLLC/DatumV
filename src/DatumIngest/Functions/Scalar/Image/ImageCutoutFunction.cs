using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Composites a source image with a single-channel saliency mask, returning
/// an <c>Image</c> whose alpha channel is set from the mask. <c>image_cutout(
/// image, mask)</c> — RGB is taken from the source image unchanged; the
/// mask's red-channel intensity becomes the per-pixel alpha. Returns a
/// null Image when either argument is null.
/// </summary>
/// <remarks>
/// <para>
/// Designed for the <c>models.u2net(...)</c> / <c>models.u2netp(...)</c>
/// background-removal pipeline: the model emits a grayscale-as-RGBA mask
/// the same size as the input photo, and this function turns "image + mask"
/// into a transparent-background PNG ready to layer over another scene.
/// Works with any single-channel-as-RGBA mask, not just U²-Net's — anything
/// that writes the mask value into the red channel composes the same way.
/// </para>
/// <para>
/// <strong>Sizing.</strong> The output has the source image's dimensions.
/// If the mask was emitted at a different size (e.g. the caller hand-built
/// a 320×320 mask without resizing), it is resized to match the source via
/// <see cref="SKBitmap.Resize(SKImageInfo, SKSamplingOptions)"/>.
/// </para>
/// <para>
/// <strong>Alpha policy.</strong> The mask's red-channel value <em>replaces</em>
/// any alpha already on the source — there is no multiply step. For an opaque
/// JPEG/PNG photo (the dominant input shape) replacement is what the caller
/// wants; for an already-cut-out PNG, the existing alpha is discarded. A
/// future <c>image_cutout(image, mask, mode)</c> overload with a "multiply"
/// mode is the right answer if that case shows up.
/// </para>
/// <para>
/// <strong>Output is unpremultiplied.</strong> RGB is preserved verbatim
/// even where alpha is 0, so saving the bitmap to PNG and reopening it in
/// an editor recovers the original colours under the transparency.
/// </para>
/// </remarks>
public sealed class ImageCutoutFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_cutout";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Composites a source image with a single-channel mask, returning an "
        + "image whose alpha channel is set from the mask's red-channel intensity. "
        + "RGB is preserved from the source; alpha is replaced by the mask. "
        + "Pairs naturally with U²-Net (models.u2net / models.u2netp) for "
        + "background removal.";

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
        FunctionMetadata.Validate<ImageCutoutFunction>(argumentKinds);

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
                $"image_cutout: source image has non-positive dimensions ({width}×{height}).");
        }

        // Source → RGBA8888 with unpremultiplied alpha. The arena-owned bitmap
        // from AsImage is platform-native (BGRA on Windows, RGBA elsewhere); an
        // explicit conversion gives us a stable byte order for the per-pixel
        // copy below regardless of host OS.
        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap srcRgba = new(rgbaInfo);
        if (!srcBitmap.CopyTo(srcRgba, SKColorType.Rgba8888))
        {
            throw new InvalidOperationException(
                $"image_cutout: failed to convert source image to RGBA8888 "
                + $"(source colour type: {srcBitmap.ColorType}).");
        }

        // Mask → RGBA8888 sized to the source. Resize doubles as a colour-
        // type conversion. When dimensions already match, this still walks
        // the mask once — cheap relative to the source's own conversion and
        // avoids a branch in the per-pixel loop below.
        using SKBitmap maskRgba = maskBitmap.Resize(rgbaInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"image_cutout: failed to resize/convert the mask to {width}×{height} RGBA8888.");

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
                d[o + 0] = s[o + 0];     // R
                d[o + 1] = s[o + 1];     // G
                d[o + 2] = s[o + 2];     // B
                d[o + 3] = m[o + 0];     // A ← mask R (mask is grayscale, R=G=B)
            }
        }

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }
}
