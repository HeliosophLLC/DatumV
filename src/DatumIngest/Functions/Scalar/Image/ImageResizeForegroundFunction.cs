using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>image_resize_foreground(image Image, ratio Float32) → Image</c>. Crops
/// an RGBA image to the tight bounding box of its non-transparent pixels,
/// then pads to a square with the subject occupying <c>ratio</c> of each
/// side. The padded area is fully transparent (alpha = 0). This is the
/// canonical "centred-subject-with-margin" preprocessing step that
/// TripoSR-family models expect — without it, a photo where the subject
/// occupies 30% of the frame produces garbled reconstructions because the
/// model sees mostly empty space inside its 512×512 input.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Algorithm.</strong> Mirrors TripoSR's
/// <c>tsr/utils.py:resize_foreground</c>:
/// <list type="number">
///   <item>Scan the alpha channel; locate the tight bbox of pixels with
///         <c>alpha &gt; 0</c>.</item>
///   <item>Crop the source to that bbox (h × w).</item>
///   <item>Compute <c>final = round(max(h, w) / ratio)</c>; allocate a
///         <c>final × final</c> RGBA image of zeros (transparent).</item>
///   <item>Place the cropped bbox centred in the final image — offset
///         <c>((final - h) / 2, (final - w) / 2)</c>.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Pair with the typical TripoSR pipeline.</strong>
/// <code>
/// img → image_cutout(img, models.u2netp(img))
///      → image_resize_foreground(_, 0.85)        ← this function
///      → image_composite_over(_, [0.5,0.5,0.5])
///      → models.triposr(_)
/// </code>
/// </para>
/// <para>
/// <strong>Inputs without a real alpha channel.</strong> Fully-opaque
/// inputs (alpha = 255 everywhere, e.g. raw JPEGs) get the full image as
/// the "subject" bbox — the function still pads to <c>1 / ratio</c> times
/// the image's longest side, giving a centred letterbox with transparent
/// margin. That's still useful (better than stretching the photo to 512×512
/// directly) but not as effective as feeding a properly-masked cutout.
/// </para>
/// <para>
/// <strong>Degenerate input (alpha = 0 everywhere).</strong> Returns the
/// source unchanged rather than crashing on an empty bbox. Pipelines that
/// pass through a black mask still produce some output.
/// </para>
/// </remarks>
public sealed class ImageResizeForegroundFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_resize_foreground";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Crops an RGBA image to the bbox of its non-transparent pixels, then pads to "
        + "a square with the subject occupying `ratio` of each side. Canonical "
        + "preprocessing for image-to-3D models (TripoSR / SF3D / InstantMesh) that "
        + "expect a centred subject with margin. ratio=0.85 matches TripoSR's "
        + "reference. Padded area is transparent (alpha=0).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("ratio", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageResizeForegroundFunction>(argumentKinds);

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

        float ratio = args[1].AsFloat32();
        if (!float.IsFinite(ratio) || ratio <= 0f || ratio > 1f)
        {
            throw new FunctionArgumentException(Name,
                $"ratio must be in (0, 1] (e.g. 0.85 for TripoSR-style margin); got {ratio}.");
        }

        SKBitmap srcBitmap = args[0].AsImage();
        int w = srcBitmap.Width;
        int h = srcBitmap.Height;
        if (w <= 0 || h <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"source image has non-positive dimensions ({w}×{h}).");
        }

        // Normalize source to RGBA8888 unpremultiplied. AsImage() may hand
        // back a platform-native colour type (BGRA) or premultiplied
        // alpha; explicit CopyTo through RGBA8888 Unpremul keeps the
        // alpha-bbox scan straightforward.
        SKImageInfo rgbaInfo = new(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap srcRgba = new(rgbaInfo);
        if (!srcBitmap.CopyTo(srcRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(Name,
                $"failed to convert source image to RGBA8888 "
                + $"(source colour type: {srcBitmap.ColorType}).");
        }

        nint srcPtr = srcRgba.GetPixels();
        (int y1, int y2, int x1, int x2, bool anyOpaque) = FindAlphaBbox(srcPtr, w, h);
        if (!anyOpaque)
        {
            // Fully transparent input — nothing to centre. Hand the source
            // back unchanged so pipelines stay alive on weird masks.
            return new ValueTask<ValueRef>(args[0]);
        }

        // Tight bbox dimensions, inclusive on both ends.
        int bboxW = x2 - x1 + 1;
        int bboxH = y2 - y1 + 1;
        int side = System.Math.Max(bboxW, bboxH);
        int final = (int)MathF.Round(side / ratio);
        if (final <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"computed final size <= 0 (side={side}, ratio={ratio}); pass a larger ratio.");
        }

        // Centre offsets in the final canvas.
        int offY = (final - bboxH) / 2;
        int offX = (final - bboxW) / 2;

        SKImageInfo outInfo = new(final, final, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        SKBitmap output = new(outInfo);
        // SKBitmap allocates pre-zeroed in managed mode, but be explicit
        // since we're writing through a raw byte* and the unfilled region
        // must read as fully-transparent black.
        output.Erase(new SKColor(0, 0, 0, 0));

        nint outPtr = output.GetPixels();
        unsafe
        {
            byte* s = (byte*)srcPtr;
            byte* d = (byte*)outPtr;
            for (int yy = 0; yy < bboxH; yy++)
            {
                int srcRow = (y1 + yy) * w + x1;
                int dstRow = (offY + yy) * final + offX;
                for (int xx = 0; xx < bboxW; xx++)
                {
                    int srcOff = (srcRow + xx) * 4;
                    int dstOff = (dstRow + xx) * 4;
                    d[dstOff + 0] = s[srcOff + 0];
                    d[dstOff + 1] = s[srcOff + 1];
                    d[dstOff + 2] = s[srcOff + 2];
                    d[dstOff + 3] = s[srcOff + 3];
                }
            }
        }
        GC.KeepAlive(srcRgba);
        GC.KeepAlive(output);

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    /// <summary>
    /// Walks the RGBA pixel buffer, returning the inclusive bbox of pixels
    /// with non-zero alpha. The trailing <c>anyOpaque</c> flag is false
    /// when the entire image is fully transparent — caller falls back to
    /// the unchanged source in that case.
    /// </summary>
    private static (int y1, int y2, int x1, int x2, bool anyOpaque) FindAlphaBbox(
        nint pixelPtr, int width, int height)
    {
        int y1 = int.MaxValue, y2 = int.MinValue;
        int x1 = int.MaxValue, x2 = int.MinValue;
        bool any = false;
        unsafe
        {
            byte* p = (byte*)pixelPtr;
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    byte alpha = p[(rowStart + x) * 4 + 3];
                    if (alpha > 0)
                    {
                        any = true;
                        if (y < y1) y1 = y;
                        if (y > y2) y2 = y;
                        if (x < x1) x1 = x;
                        if (x > x2) x2 = x;
                    }
                }
            }
        }
        return (y1, y2, x1, x2, any);
    }
}
