using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Sam;

/// <summary>
/// Shared mask-output helpers for the SAM-family builtins. Both
/// <see cref="MaskNmsPlanesFunction"/> (multi-survivor) and
/// <see cref="BinaryMaskFromLogitsFunction"/> (single-mask) emit binary
/// grayscale-as-RGBA bitmaps; the bitmap-build step is identical and
/// lives here so the two surfaces stay byte-for-byte consistent.
/// </summary>
internal static class SamMaskOps
{
    /// <summary>
    /// Packs a flat per-pixel <c>{0, non-zero}</c> byte mask into an
    /// <see cref="SKBitmap"/> sized <paramref name="width"/> ×
    /// <paramref name="height"/>. RGBA opaque, white = foreground, black =
    /// background — same layout as U²-Net's output so downstream
    /// <c>image_cutout(img, mask)</c> consumes both uniformly.
    /// </summary>
    internal static SKBitmap BuildBinaryMaskBitmap(byte[] mask, int width, int height)
    {
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap bmp = new(info);
        nint ptr = bmp.GetPixels();
        unsafe
        {
            byte* dst = (byte*)ptr;
            for (int i = 0; i < mask.Length; i++)
            {
                byte g = mask[i] != 0 ? (byte)255 : (byte)0;
                int o = i * 4;
                dst[o + 0] = g;
                dst[o + 1] = g;
                dst[o + 2] = g;
                dst[o + 3] = 255;
            }
        }
        return bmp;
    }
}
