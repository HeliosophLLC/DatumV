using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// Internal helper shared by the pixel-statistics image functions
/// (<c>image_brightness_*</c>, <c>image_pixel_*</c>, <c>detect_blur</c>,
/// <c>compression_artifact_score</c>). Centralises the
/// "decode-if-needed, RGBA8888-coerce, read raw bytes" prologue so the
/// individual functions can focus on their reduction kernel.
/// </summary>
internal static class ImagePixelAccess
{
    /// <summary>
    /// BT.601 luminance weights (Y' = 0.299·R + 0.587·G + 0.114·B). Used by
    /// every function that needs a single-channel grayscale projection.
    /// </summary>
    public const float Bt601RedWeight = 0.299f;
    public const float Bt601GreenWeight = 0.587f;
    public const float Bt601BlueWeight = 0.114f;

    /// <summary>
    /// Returns an <see cref="SKBitmap"/> in <see cref="SKColorType.Rgba8888"/>
    /// layout. When the input is already RGBA8888 the input is returned directly
    /// and <paramref name="ownedConversion"/> is set to <see langword="null"/>;
    /// otherwise a freshly allocated converted bitmap is returned and the caller
    /// is responsible for disposing <paramref name="ownedConversion"/>.
    /// </summary>
    public static SKBitmap AsRgba8888(SKBitmap source, out SKBitmap? ownedConversion)
    {
        if (source.ColorType == SKColorType.Rgba8888)
        {
            ownedConversion = null;
            return source;
        }

        SKBitmap converted = new(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (SKCanvas canvas = new(converted))
        {
            canvas.DrawBitmap(source, 0, 0);
        }
        ownedConversion = converted;
        return converted;
    }
}
