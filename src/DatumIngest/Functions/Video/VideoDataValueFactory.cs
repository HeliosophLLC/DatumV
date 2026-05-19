using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Video;

/// <summary>
/// Helpers for constructing <see cref="DataValue"/>s of <see cref="DataKind.Video"/>
/// with inline width / height / fps / codec / frame-count metadata populated.
/// Mirrors <c>ImageDataValueFactory</c> and <c>AudioDataValueFactory</c>; every new
/// Video production site routes through here so accessors like <c>video_width()</c>
/// work without a full decode fallback.
/// </summary>
/// <remarks>
/// Uses <see cref="VideoHeaderParser"/> (Sdcb.FFmpeg-based) to extract width, height,
/// FPS (as 8.8 fixed-point), codec discriminator, and frame count from any container
/// FFmpeg can demux. Falls back to the no-metadata factory when FFmpeg can't open
/// the bytes (corrupt input, no video stream, unknown container).
/// </remarks>
public static class VideoDataValueFactory
{
    /// <summary>
    /// Constructs a <see cref="DataKind.Video"/> <see cref="DataValue"/> from encoded
    /// video bytes, parsing the container header to stamp inline metadata. Unrecognised
    /// inputs fall through to the no-metadata factory; accessors return zero sentinels
    /// and the SQL <c>video_*</c> functions return NULL.
    /// </summary>
    public static DataValue FromEncodedBytes(byte[] bytes, IValueStore store)
    {
        VideoHeaderMetadata? meta = VideoHeaderParser.TryParseHeader(bytes);
        if (meta is { Width: > 0, Height: > 0 })
        {
            return DataValue.FromVideo(bytes, store, meta.Width, meta.Height, meta.FpsX256, meta.Codec, meta.FrameCount);
        }
        return DataValue.FromVideo(bytes, store);
    }
}
