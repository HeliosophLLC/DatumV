using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Image;

/// <summary>
/// Helpers for constructing <see cref="DataValue"/>s of <see cref="DataKind.Image"/>
/// with inline dimensions metadata populated (width, height, channels). Centralises
/// the "parse the header / extract from SKBitmap" step so every <c>FromImage</c> call
/// site stamps the metadata uniformly instead of dropping it.
/// </summary>
/// <remarks>
/// <para>
/// Two ingest shapes, two helpers:
/// </para>
/// <list type="bullet">
///   <item><see cref="FromEncodedBytes"/> — caller has the encoded image bytes
///     (PNG/JPEG/WebP/etc.) but no parsed dimensions. Runs <see cref="ImageHeaderParser"/>
///     once and forwards W/H/channels to the metadata-bearing
///     <see cref="DataValue.FromImage(byte[], IValueStore, ushort, ushort, byte)"/>
///     factory. Falls back to the no-metadata factory when the header is unparseable
///     (corrupt input, exotic codec).</item>
///   <item><see cref="FromBitmap"/> — caller has an <see cref="SKBitmap"/> in hand
///     (typical for image-producing scalar functions). Encodes to PNG, reads W/H
///     directly off the bitmap, and forwards. No header re-parse needed.</item>
/// </list>
/// </remarks>
internal static class ImageDataValueFactory
{
    /// <summary>
    /// Constructs a <see cref="DataKind.Image"/> <see cref="DataValue"/> from encoded
    /// image bytes, parsing the header to stamp inline dimensions metadata.
    /// </summary>
    public static DataValue FromEncodedBytes(byte[] bytes, IValueStore store)
    {
        ImageDimensions? dims = ImageHeaderParser.TryParseHeader(bytes);
        if (dims is { Width: > 0 and <= ushort.MaxValue, Height: > 0 and <= ushort.MaxValue })
        {
            return DataValue.FromImage(bytes, store,
                (ushort)dims.Width, (ushort)dims.Height, ClampChannels(dims.Channels));
        }
        // Header unparseable (corrupt bytes, unknown format, or codec we don't
        // recognise). Fall through to the no-metadata factory so accessors return
        // 0 sentinels and image_width()/image_height() fall back to SKBitmap decode.
        return DataValue.FromImage(bytes, store);
    }

    /// <summary>
    /// Constructs a <see cref="DataKind.Image"/> <see cref="DataValue"/> by PNG-encoding
    /// an <see cref="SKBitmap"/> and forwarding its dimensions inline. The bitmap's
    /// <see cref="SKBitmap.BytesPerPixel"/> approximates the channel count for the
    /// 8-bit-per-channel formats that dominate (1=gray, 3=RGB, 4=RGBA); higher-precision
    /// formats may report something less intuitive (e.g. 8 for RGBA-F16). Callers that
    /// care about exact channel semantics should pass the value explicitly via the
    /// <see cref="DataValue.FromImage(byte[], IValueStore, ushort, ushort, byte)"/>
    /// factory directly.
    /// </summary>
    public static DataValue FromBitmap(SKBitmap bitmap, IValueStore store)
    {
        byte[] bytes = ImageEncoder.Encode(bitmap, SKEncodedImageFormat.Png, 100);
        ushort width = bitmap.Width is > 0 and <= ushort.MaxValue ? (ushort)bitmap.Width : (ushort)0;
        ushort height = bitmap.Height is > 0 and <= ushort.MaxValue ? (ushort)bitmap.Height : (ushort)0;
        byte channels = (byte)Math.Clamp(bitmap.BytesPerPixel, 0, 255);
        return DataValue.FromImage(bytes, store, width, height, channels);
    }

    private static byte ClampChannels(int c) =>
        c is >= 0 and <= 255 ? (byte)c : (byte)0;
}
