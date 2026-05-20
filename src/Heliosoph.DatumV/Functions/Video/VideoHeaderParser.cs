using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;

namespace Heliosoph.DatumV.Functions.Video;

/// <summary>
/// Lightweight video container header parser. Opens an in-memory video blob with
/// FFmpeg, reads stream-level metadata (width / height / fps / codec / frame
/// count) without spinning up a full decoder, and disposes the FFmpeg resources.
/// Used at ingest time to stamp inline metadata onto <c>Video</c>
/// <see cref="Heliosoph.DatumV.Model.DataValue"/>s so accessors like <c>video_width()</c>
/// skip a full decode.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="MediaStream.Codecpar"/> for width/height/codec — no
/// <see cref="CodecContext"/> open required, which is both faster and avoids
/// pulling in decoder libraries we don't need at parse time. Frame count comes from
/// <see cref="MediaStream.NbFrames"/> when the container records it; container
/// formats that don't (some MKV variants, fragmented MP4 without an index) return
/// 0 for frame count but width/height/fps still parse correctly.
/// </para>
/// <para>
/// Mirrors the stream-open path in <see cref="Heliosoph.DatumV.Model.VideoRegistry"/> —
/// wrap bytes in a <see cref="MemoryStream"/>, hand to FFmpeg via
/// <see cref="IOContext.ReadStream"/>, open the format context, load stream info,
/// find the best video stream. Disposes everything on every code path.
/// </para>
/// <para>
/// Returns <c>null</c> on any FFmpeg error (corrupt input, unrecognised container,
/// no video stream) — callers fall through to the no-metadata factory.
/// </para>
/// </remarks>
public static class VideoHeaderParser
{
    private const int StreamBufferSize = 8 * 1024;

    /// <summary>
    /// Attempts to parse video metadata from an encoded video blob.
    /// Returns <c>null</c> when the format isn't recognised or FFmpeg fails to
    /// open it.
    /// </summary>
    public static VideoHeaderMetadata? TryParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return null;

        // FFmpeg's IOContext expects a Stream — copy into a MemoryStream once.
        // The cost is a single byte[] allocation, dwarfed by FFmpeg's own parse
        // work.
        byte[] copy = data.ToArray();
        using MemoryStream ms = new(copy, writable: false);
        IOContext? io = null;
        FormatContext? fc = null;
        try
        {
            io = IOContext.ReadStream(ms, StreamBufferSize);
            fc = FormatContext.OpenInputIO(io);
            fc.LoadStreamInfo();

            MediaStream? videoStream = fc.FindBestStreamOrNull(AVMediaType.Video);
            if (videoStream is null) return null;

            MediaStream s = videoStream.Value;
            var codecpar = s.Codecpar;
            if (codecpar is null) return null;

            int width = codecpar.Width;
            int height = codecpar.Height;
            if (width <= 0 || height <= 0) return null;

            AVRational fps = s.AvgFrameRate;
            double fpsValue = fps.Den == 0 ? 0.0 : (double)fps.Num / fps.Den;
            // 8.8 fixed-point cap at uint16 max → 255.99 fps. Anything higher
            // saturates to the cap; callers wanting exact extreme rates can read
            // the frame count + duration off the container directly.
            ushort fpsX256 = fpsValue switch
            {
                <= 0.0 => (ushort)0,
                >= 255.99 => ushort.MaxValue,
                _ => (ushort)Math.Round(fpsValue * 256.0),
            };

            byte codecByte = MapCodec(codecpar.CodecId);
            uint frameCount = s.NbFrames > 0 && s.NbFrames <= uint.MaxValue ? (uint)s.NbFrames : 0u;

            return new VideoHeaderMetadata(
                Width: (ushort)Math.Min(width, ushort.MaxValue),
                Height: (ushort)Math.Min(height, ushort.MaxValue),
                FpsX256: fpsX256,
                Codec: codecByte,
                FrameCount: frameCount);
        }
        catch
        {
            // Any FFmpeg failure (unknown container, corrupt header, missing
            // demuxer) → caller falls back to the no-metadata factory.
            return null;
        }
        finally
        {
            fc?.Free();
            io?.Dispose();
        }
    }

    /// <summary>
    /// Maps an FFmpeg codec id to a single byte for inline storage. The byte is a
    /// stable engine-side discriminator (not the raw <see cref="AVCodecID"/> integer
    /// since those can shift across FFmpeg versions). 0 = unknown.
    /// </summary>
    private static byte MapCodec(AVCodecID id) => id switch
    {
        AVCodecID.H264 => 1,
        AVCodecID.Hevc => 2,
        AVCodecID.Av1 => 3,
        AVCodecID.Vp9 => 4,
        AVCodecID.Vp8 => 5,
        AVCodecID.Mpeg4 => 6,
        AVCodecID.Mpeg2video => 7,
        AVCodecID.Theora => 8,
        _ => 0,
    };
}

/// <summary>
/// Video metadata extracted from a container header. All fields fit the inline
/// storage budget (uint16 W/H, uint16 fps as 8.8 fixed-point, byte codec
/// discriminator, uint32 frame count).
/// </summary>
public sealed record VideoHeaderMetadata(ushort Width, ushort Height, ushort FpsX256, byte Codec, uint FrameCount);
