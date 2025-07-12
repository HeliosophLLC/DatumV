namespace DatumIngest.Functions.Image;

using SkiaSharp;

/// <summary>
/// Shared helpers for encoding SkiaSharp bitmaps back to Image byte arrays.
/// Transform functions use this to re-encode results in the original or requested format.
/// </summary>
internal static class ImageEncoder
{
    /// <summary>
    /// Resolves the output format from an optional user-supplied format string
    /// and the detected format of the original image bytes.
    /// </summary>
    /// <param name="originalBytes">The original encoded image bytes (used to detect format if no override).</param>
    /// <param name="formatOverride">Optional user-supplied format string (e.g. <c>"jpeg"</c>, <c>"png"</c>, <c>"webp"</c>).</param>
    /// <returns>The resolved SkiaSharp encoding format.</returns>
    public static SKEncodedImageFormat ResolveFormat(byte[] originalBytes, string? formatOverride)
    {
        if (formatOverride is not null)
        {
            return ParseFormatString(formatOverride);
        }

        ImageFormat detected = ImageHeaderParser.DetectFormat(originalBytes);

        return detected switch
        {
            ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            ImageFormat.Png => SKEncodedImageFormat.Png,
            ImageFormat.WebP => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png // default to lossless if unknown
        };
    }

    /// <summary>
    /// Resolves the output format from an <see cref="ImageHandle"/> and an optional
    /// user-supplied format override. When no override is given, the handle's existing
    /// format is preserved — avoiding a header re-parse.
    /// </summary>
    /// <param name="handle">The source image handle whose format is used as a fallback.</param>
    /// <param name="formatOverride">Optional user-supplied format string.</param>
    /// <returns>The resolved SkiaSharp encoding format.</returns>
    public static SKEncodedImageFormat ResolveFormat(ImageHandle handle, string? formatOverride)
    {
        if (formatOverride is not null)
        {
            return ParseFormatString(formatOverride);
        }

        return handle.Format;
    }

    /// <summary>
    /// Encodes a bitmap to a byte array in the specified format.
    /// </summary>
    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 90)
    {
        using SKData data = bitmap.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>
    /// Encodes an SKImage to a byte array in the specified format.
    /// </summary>
    public static byte[] Encode(SKImage image, SKEncodedImageFormat format, int quality = 90)
    {
        using SKData data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>
    /// Parses a user-supplied format string into an <see cref="SKEncodedImageFormat"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The format string is not recognized.</exception>
    public static SKEncodedImageFormat ParseFormatString(string format)
    {
        return format.ToUpperInvariant() switch
        {
            "JPEG" or "JPG" => SKEncodedImageFormat.Jpeg,
            "PNG" => SKEncodedImageFormat.Png,
            "WEBP" => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentException(
                $"Unknown image format '{format}'. Supported: jpeg, png, webp.")
        };
    }
}
