namespace DatumQuery.Functions.Image;

/// <summary>
/// Parses JPEG, PNG, and WebP image headers to extract dimensions and channel count
/// without fully decoding the image data. Also provides format detection for re-encoding.
/// </summary>
public static class ImageHeaderParser
{
    /// <summary>
    /// Attempts to parse image header bytes to extract dimensions without full decoding.
    /// Supports JPEG (SOF markers), PNG (IHDR chunk), and WebP (VP8/VP8L/VP8X).
    /// </summary>
    /// <param name="data">The raw encoded image bytes.</param>
    /// <returns>Parsed dimensions, or <c>null</c> if the format is unrecognized or the header is malformed.</returns>
    public static ImageDimensions? TryParseHeader(byte[] data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        // JPEG: starts with FF D8
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryParseJpegHeader(data);
        }

        // PNG: starts with 89 50 4E 47 0D 0A 1A 0A
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return TryParsePngHeader(data);
        }

        // WebP: starts with RIFF....WEBP
        if (data.Length >= 16 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return TryParseWebPHeader(data);
        }

        return null;
    }

    /// <summary>
    /// Detects the image format from the leading magic bytes.
    /// </summary>
    /// <param name="data">The raw encoded image bytes.</param>
    /// <returns>The detected format, or <see cref="ImageFormat.Unknown"/> if unrecognized.</returns>
    public static ImageFormat DetectFormat(byte[] data)
    {
        if (data.Length < 4)
        {
            return ImageFormat.Unknown;
        }

        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return ImageFormat.Jpeg;
        }

        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return ImageFormat.Png;
        }

        if (data.Length >= 16 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return ImageFormat.WebP;
        }

        return ImageFormat.Unknown;
    }

    private static ImageDimensions? TryParseJpegHeader(byte[] data)
    {
        // Scan for SOF markers: FF C0..C3, C5..C7, C9..CB, CD..CF
        int offset = 2; // skip SOI marker

        while (offset + 2 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = data[offset + 1];

            // Skip padding bytes (0xFF)
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            // SOF markers
            bool isSof = marker is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7)
                         or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF);

            if (isSof)
            {
                // Need at least 8 more bytes after the marker
                if (offset + 9 >= data.Length)
                {
                    return null;
                }

                // Skip marker (2) + length (2) + precision (1)
                int height = (data[offset + 5] << 8) | data[offset + 6];
                int width = (data[offset + 7] << 8) | data[offset + 8];
                int channels = data[offset + 9];

                return new ImageDimensions(width, height, channels);
            }

            // Skip to next marker: read segment length and advance
            if (offset + 3 >= data.Length)
            {
                return null;
            }

            int segmentLength = (data[offset + 2] << 8) | data[offset + 3];

            if (segmentLength < 2)
            {
                return null;
            }

            offset += 2 + segmentLength;
        }

        return null;
    }

    private static ImageDimensions? TryParsePngHeader(byte[] data)
    {
        // IHDR is always the first chunk after the 8-byte signature
        // Chunk: length(4) + type(4) + data + CRC(4)
        // IHDR data: width(4) + height(4) + bitDepth(1) + colorType(1) + ...
        if (data.Length < 24 + 2) // 8 sig + 4 len + 4 type + 4 width + 4 height + 1 depth + 1 color
        {
            return null;
        }

        // Verify IHDR type at offset 12
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
        {
            return null;
        }

        int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
        byte colorType = data[25];

        int channels = colorType switch
        {
            0 => 1,  // Grayscale
            2 => 3,  // RGB
            3 => 1,  // Indexed (palette)
            4 => 2,  // Grayscale + Alpha
            6 => 4,  // RGBA
            _ => 0
        };

        return new ImageDimensions(width, height, channels);
    }

    private static ImageDimensions? TryParseWebPHeader(byte[] data)
    {
        if (data.Length < 20)
        {
            return null;
        }

        // Check chunk type at offset 12: VP8 (lossy), VP8L (lossless), VP8X (extended)
        bool isVp8 = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)' ';
        bool isVp8L = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'L';
        bool isVp8X = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'X';

        if (isVp8X && data.Length >= 30)
        {
            // VP8X: flags at byte 20, then 3 bytes reserved
            // Canvas width - 1 at bytes 24-26 (24-bit LE)
            // Canvas height - 1 at bytes 27-29 (24-bit LE)
            int width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            int height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            bool hasAlpha = (data[20] & 0x10) != 0;

            return new ImageDimensions(width, height, hasAlpha ? 4 : 3);
        }

        if (isVp8 && data.Length >= 30)
        {
            // VP8 lossy: skip chunk header (4 type + 4 size = offset 20)
            // Then 3-byte frame tag, then look for sync code 9D 01 2A
            int searchEnd = System.Math.Min(data.Length - 4, 40);

            for (int i = 20; i < searchEnd; i++)
            {
                if (data[i] == 0x9D && data[i + 1] == 0x01 && data[i + 2] == 0x2A)
                {
                    if (i + 6 >= data.Length)
                    {
                        return null;
                    }

                    int width = (data[i + 3] | (data[i + 4] << 8)) & 0x3FFF;
                    int height = (data[i + 5] | (data[i + 6] << 8)) & 0x3FFF;

                    return new ImageDimensions(width, height, 3); // VP8 lossy is always YUV → 3 channels
                }
            }
        }

        if (isVp8L && data.Length >= 25)
        {
            // VP8L lossless: offset 20 = chunk size (4 bytes), offset 24 = signature (0x2F)
            if (data[24] != 0x2F)
            {
                return null;
            }

            // Next 4 bytes: width-1 (14 bits) + height-1 (14 bits) + alpha (1 bit) + version (3 bits)
            if (data.Length < 29)
            {
                return null;
            }

            uint bits = (uint)(data[25] | (data[26] << 8) | (data[27] << 16) | (data[28] << 24));
            int width = (int)(bits & 0x3FFF) + 1;
            int height = (int)((bits >> 14) & 0x3FFF) + 1;
            bool hasAlpha = ((bits >> 28) & 1) == 1;

            return new ImageDimensions(width, height, hasAlpha ? 4 : 3);
        }

        return null;
    }
}

/// <summary>
/// Parsed image dimensions.
/// </summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Channels">Number of color channels (e.g. 1=grayscale, 3=RGB, 4=RGBA).</param>
public sealed record ImageDimensions(int Width, int Height, int Channels);

/// <summary>
/// Recognized image encoding formats.
/// </summary>
public enum ImageFormat
{
    /// <summary>Unrecognized or unsupported format.</summary>
    Unknown = 0,

    /// <summary>JPEG (JFIF/Exif).</summary>
    Jpeg,

    /// <summary>Portable Network Graphics.</summary>
    Png,

    /// <summary>WebP (lossy or lossless).</summary>
    WebP
}
