using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>image_composite_over(image Image, background_rgb Float32[]) → Image</c>.
/// Composites an RGBA image over a solid RGB background, producing an opaque
/// image. The output's RGB is the standard alpha-over blend
/// <c>in_rgb · in_alpha + bg_rgb · (1 - in_alpha)</c>; the output's alpha is
/// 255 (fully opaque).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is for.</strong> Models trained on subjects against a
/// known uniform background (TripoSR's 0.5 gray, white-background ImageNet
/// crops, etc.) want the input pre-composited — feeding them raw transparency
/// or arbitrary background pixels at inference time produces silhouette
/// artifacts and ghost geometry. Pairs naturally with
/// <c>image_cutout(img, mask)</c>: cut out the subject, then flatten over
/// the background colour the consumer model was trained on.
/// </para>
/// <para>
/// <strong>Background colour units.</strong> Float32 in <c>[0, 1]</c> per
/// channel (the standard ML normalised range). <c>[0.5, 0.5, 0.5]</c> is mid
/// gray (TripoSR's training-data background); <c>[1, 1, 1]</c> is white;
/// <c>[0, 0, 0]</c> is black. Values outside <c>[0, 1]</c> are clamped before
/// quantisation to 0–255.
/// </para>
/// <para>
/// <strong>Inputs without alpha.</strong> A fully-opaque RGB image
/// (alpha = 255 everywhere) makes this a no-op for the RGB channels, so the
/// function is safe to apply unconditionally when you're not sure whether
/// the upstream produced an RGBA cutout or a flattened RGB image. The
/// output is always opaque RGBA8.
/// </para>
/// </remarks>
public sealed class ImageCompositeOverFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_composite_over";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Composites an RGBA image over a solid RGB background, producing an opaque "
        + "image. Output RGB = in_rgb·in_alpha + bg_rgb·(1-in_alpha); output alpha = 255. "
        + "background_rgb is a Float32[3] in [0, 1] (e.g. [0.5, 0.5, 0.5] for mid gray, "
        + "TripoSR's training background). Pairs with image_cutout for "
        + "background-removed inputs that downstream models expect over a flat colour.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("background_rgb", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageCompositeOverFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        float[] bg = ActivationOps.ReadFloat32Array(args[1]);
        if (bg.Length != 3)
        {
            throw new FunctionArgumentException(Name,
                $"background_rgb must be a Float32[3] (R, G, B in [0, 1]); got length {bg.Length}.");
        }

        byte bgR = QuantizeChannel(bg[0]);
        byte bgG = QuantizeChannel(bg[1]);
        byte bgB = QuantizeChannel(bg[2]);

        SKBitmap srcBitmap = args[0].AsImage();
        int width = srcBitmap.Width;
        int height = srcBitmap.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"source image has non-positive dimensions ({width}×{height}).");
        }

        // Normalise to RGBA8888 unpremultiplied. AsImage() may hand back a
        // platform-native layout (BGRA on Windows) or a premultiplied bitmap
        // (the standard PNG decode path); the explicit CopyTo + targetInfo
        // forces straight RGBA with unpremultiplied channels so the blend
        // math below reads what we expect. Without this step the RGB
        // beneath alpha=0 pixels can be zero (the premultiplied form), which
        // gives the "black background" artifact for transparent cutouts.
        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap srcRgba = new(rgbaInfo);
        if (!srcBitmap.CopyTo(srcRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(Name,
                $"failed to convert source image to RGBA8888 "
                + $"(source colour type: {srcBitmap.ColorType}).");
        }

        SKBitmap output = new(rgbaInfo);
        nint srcPtr = srcRgba.GetPixels();
        nint outPtr = output.GetPixels();
        int pixelCount = width * height;
        unsafe
        {
            byte* s = (byte*)srcPtr;
            byte* d = (byte*)outPtr;
            for (int i = 0; i < pixelCount; i++)
            {
                int o = i * 4;
                byte r = s[o + 0];
                byte g = s[o + 1];
                byte b = s[o + 2];
                byte a = s[o + 3];
                int inv = 255 - a;
                // (r * a + bgR * inv + 127) / 255 -- the +127 is round-to-nearest
                // (vs. truncation), which keeps the no-op case (alpha = 255)
                // exact regardless of bgR.
                d[o + 0] = (byte)((r * a + bgR * inv + 127) / 255);
                d[o + 1] = (byte)((g * a + bgG * inv + 127) / 255);
                d[o + 2] = (byte)((b * a + bgB * inv + 127) / 255);
                d[o + 3] = 255;
            }
        }
        GC.KeepAlive(srcRgba);
        GC.KeepAlive(output);

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    private static byte QuantizeChannel(float c)
    {
        if (float.IsNaN(c)) return 0;
        if (c <= 0f) return 0;
        if (c >= 1f) return 255;
        return (byte)MathF.Round(c * 255f);
    }
}
