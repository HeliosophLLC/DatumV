using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Shared image preprocessing helpers for ONNX model pipelines.
/// Handles aspect-preserving or stretch resize, per-pixel normalization, and NCHW tensor packing.
/// Output is channel-major float32: plane 0 = channel 0 (R for RGB, B for BGR),
/// plane 1 = G, plane 2 = channel 2 (B for RGB, R for BGR).
/// Normalization contract: <c>output = rawByte * channelScale[c] + channelBias[c]</c>.
/// </summary>
internal static class ImageTensorPrep
{
    // ImageNet normalisation presets.
    // Derived from mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]:
    //   scale[c] = 1 / (255 * std[c])
    //   bias[c]  = -mean[c] / std[c]
    // Pass these to StretchAndPackNchw for models trained on ImageNet statistics.
    public static readonly float[] ImageNetScale =
    [
        1f / (255f * 0.229f),
        1f / (255f * 0.224f),
        1f / (255f * 0.225f),
    ];
    public static readonly float[] ImageNetBias =
    [
        -0.485f / 0.229f,
        -0.456f / 0.224f,
        -0.406f / 0.225f,
    ];

    /// <summary>
    /// Stretch-resize <paramref name="source"/> to <paramref name="width"/>×<paramref name="height"/>,
    /// normalise per-channel as <c>rawByte * channelScale[c] + channelBias[c]</c>, and write NCHW
    /// float32 into <paramref name="dest"/>.
    /// </summary>
    public static void StretchAndPackNchw(
        SKBitmap source,
        Span<float> dest,
        int width,
        int height,
        ReadOnlySpan<float> channelScale,
        ReadOnlySpan<float> channelBias,
        bool bgr = false)
    {
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = source.Resize(info, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {width}×{height}.");

        int planeSize = height * width;
        nint pixelPtr = resized.GetPixels();

        unsafe
        {
            byte* src = (byte*)pixelPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                int b = yx * 4; // Skia Rgba8888: byte 0=R, 1=G, 2=B, 3=A
                float c0 = bgr ? src[b + 2] : src[b];   // R (RGB) or B (BGR)
                float c1 = src[b + 1];                    // G
                float c2 = bgr ? src[b] : src[b + 2];    // B (RGB) or R (BGR)

                dest[yx]                 = c0 * channelScale[0] + channelBias[0];
                dest[planeSize + yx]     = c1 * channelScale[1] + channelBias[1];
                dest[2 * planeSize + yx] = c2 * channelScale[2] + channelBias[2];
            }
        }
    }

    /// <summary>
    /// Stretch-resize variant with a single normalisation applied uniformly to all channels:
    /// <c>output = rawByte * scale + bias</c>.
    /// </summary>
    public static void StretchAndPackNchw(
        SKBitmap source,
        Span<float> dest,
        int width,
        int height,
        float scale,
        float bias,
        bool bgr = false)
    {
        Span<float> s = stackalloc float[3] { scale, scale, scale };
        Span<float> b = stackalloc float[3] { bias, bias, bias };
        StretchAndPackNchw(source, dest, width, height, s, b, bgr);
    }

    /// <summary>
    /// Letterbox-resize <paramref name="source"/> into a <paramref name="targetSize"/>×<paramref name="targetSize"/>
    /// canvas (aspect-preserving, image placed at top-left), pre-fill the padding region with
    /// <paramref name="padFill"/>, normalise uniformly as <c>rawByte * scale + bias</c>, and write
    /// NCHW float32 into <paramref name="dest"/>.
    /// </summary>
    /// <param name="source">Source bitmap to resize and pack.</param>
    /// <param name="dest">Destination span of length <c>3 * targetSize * targetSize</c>.</param>
    /// <param name="targetSize">Square canvas side length in pixels.</param>
    /// <param name="scale">Per-pixel multiplier applied to each raw byte.</param>
    /// <param name="bias">Per-pixel additive offset applied after <paramref name="scale"/>.</param>
    /// <param name="padFill">
    /// Post-normalisation fill value for the padded region. For zero-pixel padding pass
    /// <c>(0 - rawMean) * rawScale</c>; for raw-byte padding (e.g. YOLOX's 114 gray) pass the
    /// byte value directly when <paramref name="bias"/> is 0.
    /// </param>
    /// <param name="bgr">When <see langword="true"/> output channel order is BGR; otherwise RGB.</param>
    /// <returns>
    /// The letterbox scale factor <c>min(target/origW, target/origH)</c>. Divide bbox coordinates
    /// by this value to map detections back to original-image pixel space.
    /// </returns>
    public static float LetterboxAndPackNchw(
        SKBitmap source,
        Span<float> dest,
        int targetSize,
        float scale,
        float bias,
        float padFill,
        bool bgr = false)
    {
        int origW = source.Width;
        int origH = source.Height;
        float ratio = MathF.Min((float)targetSize / origW, (float)targetSize / origH);
        int newW = Math.Max(1, Math.Min(targetSize, (int)MathF.Round(origW * ratio)));
        int newH = Math.Max(1, Math.Min(targetSize, (int)MathF.Round(origH * ratio)));

        SKImageInfo info = new(newW, newH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = source.Resize(info, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize image to {newW}×{newH} for letterbox.");

        int planeSize = targetSize * targetSize;
        dest.Fill(padFill);

        nint pixelPtr = resized.GetPixels();
        unsafe
        {
            byte* src = (byte*)pixelPtr;
            for (int y = 0; y < newH; y++)
            {
                int srcRow = y * newW * 4;
                int dstRow = y * targetSize;
                for (int x = 0; x < newW; x++)
                {
                    int sb = srcRow + x * 4;
                    int di = dstRow + x;

                    float c0 = bgr ? src[sb + 2] : src[sb];
                    float c1 = src[sb + 1];
                    float c2 = bgr ? src[sb] : src[sb + 2];

                    dest[di]                 = c0 * scale + bias;
                    dest[planeSize + di]     = c1 * scale + bias;
                    dest[2 * planeSize + di] = c2 * scale + bias;
                }
            }
        }

        return ratio;
    }
}
