using Heliosoph.DatumV.DatumFile.Sidecar;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;

namespace Heliosoph.DatumV.Model;

/// <summary>
/// Per-query registry of source videos backing <see cref="DataKind.VideoFrame"/> handles.
/// Each registered video owns a warm FFmpeg decoder that materialises pixel bytes on
/// demand. Sequential access (<c>frame N → N+1 → N+2 …</c>) reuses the decoder's
/// current position for the fast path; backward access seeks to the file head and
/// decodes forward to the requested frame.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifecycle.</strong> One <see cref="VideoRegistry"/> instance lives on the
/// per-query <see cref="Execution.ExecutionContext"/>. Lazy-initialised on first use
/// so queries that touch no video columns pay nothing. Disposing the context disposes
/// the registry, which releases every entry's native FFmpeg state.
/// </para>
/// <para>
/// <strong>Sources.</strong> Videos may be registered by filesystem path
/// (<see cref="RegisterPath"/>), by a sidecar window (<see cref="RegisterSidecar"/>,
/// for <c>.datum-blob</c>-backed columns), or by an in-memory byte buffer
/// (<see cref="RegisterBytes"/>, for arena-backed <see cref="DataKind.Video"/>
/// values). All three converge on the same warm-decoder shape.
/// </para>
/// <para>
/// <strong>Decoded format.</strong> Frames are emitted as packed BGRA8888 (4 bytes per
/// pixel, B/G/R/A byte order, top-to-bottom, no row padding) — the native pixel layout
/// SkiaSharp uses on Windows. swscale converts from the codec's native pixel format
/// (typically <see cref="AVPixelFormat.Yuv420p"/>) inside <see cref="Materialize"/>,
/// so downstream consumers can wrap the bytes in an <c>SKBitmap</c> with no further
/// conversion.
/// </para>
/// <para>
/// <strong>Thread-safety.</strong> The registry itself is safe for concurrent reads;
/// per-entry decoding is serialised on a single FFmpeg context per video. Two queries
/// running in parallel against the same video must each register their own entry.
/// </para>
/// </remarks>
public sealed class VideoRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, VideoRegistryEntry> _entries = [];
    private uint _nextId = 1;
    private int _disposed;

    /// <summary>
    /// Registers a video by file path and returns the id that subsequent
    /// <see cref="DataKind.VideoFrame"/> handles embed at <c>_p0</c>.
    /// </summary>
    /// <param name="path">Absolute or relative path to a video container readable by FFmpeg.</param>
    /// <returns>Stable id valid for the registry's lifetime. Never zero.</returns>
    public uint RegisterPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            ThrowIfDisposed();
            uint id = _nextId++;
            _entries[id] = new VideoRegistryEntry(path);
            return id;
        }
    }

    /// <summary>
    /// Registers a video whose bytes live in a window of a sidecar-backed
    /// <see cref="IBlobSource"/> (typically a <c>.datum-blob</c> file). The
    /// registry constructs its own <see cref="BlobSourceStream"/> view so two
    /// queries reading the same underlying source via different registries each
    /// get independent stream positions.
    /// </summary>
    /// <param name="source">
    /// Underlying blob source. Must outlive the registry; the catalog typically
    /// owns it and refcounts shared use across queries.
    /// </param>
    /// <param name="offset">Absolute byte offset where the video container begins.</param>
    /// <param name="length">Video container length in bytes.</param>
    public uint RegisterSidecar(IBlobSource source, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "must be non-negative.");
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), length, "must be non-negative.");
        lock (_gate)
        {
            ThrowIfDisposed();
            uint id = _nextId++;
            string label = $"sidecar@{offset}+{length}";
            _entries[id] = new VideoRegistryEntry(label, () => new BlobSourceStream(source, offset, length));
            return id;
        }
    }

    /// <summary>
    /// Registers a video whose container bytes live in a managed byte buffer
    /// (typically an arena-backed <see cref="DataKind.Video"/> DataValue read
    /// out via <c>AsByteSpan</c>). The bytes are wrapped in a
    /// <see cref="MemoryStream"/> and fed to FFmpeg via custom IO.
    /// </summary>
    /// <param name="bytes">Encoded video bytes. The buffer is read but not modified.</param>
    public uint RegisterBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            ThrowIfDisposed();
            uint id = _nextId++;
            string label = $"memory[{bytes.Length}]";
            _entries[id] = new VideoRegistryEntry(label, () => new MemoryStream(bytes, writable: false));
            return id;
        }
    }

    /// <summary>
    /// Materialises a frame from a registered video into packed BGRA8888 bytes,
    /// optionally resizing via swscale at decode time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sequential access (<paramref name="frameIndex"/> = previous + 1) hits the warm
    /// decoder; backward access seeks to the file head and decodes forward.
    /// </para>
    /// <para>
    /// Resize semantics: both dimensions <see langword="null"/> → source resolution.
    /// Exactly one dimension set → preserve source aspect ratio by computing the
    /// other. Both set → exact dimensions. swscale contexts are cached per
    /// <c>(target_width, target_height)</c> tuple, so queries that materialise
    /// many frames at one target size share a single converter.
    /// </para>
    /// </remarks>
    /// <param name="videoId">Id returned by a <c>Register*</c> call.</param>
    /// <param name="frameIndex">Zero-based frame index. Negative values are not yet supported.</param>
    /// <param name="targetWidth">Target width in pixels, or <see langword="null"/> to use source width.</param>
    /// <param name="targetHeight">Target height in pixels, or <see langword="null"/> to compute from source aspect ratio.</param>
    public MaterializedFrame Materialize(uint videoId, int frameIndex, int? targetWidth = null, int? targetHeight = null)
    {
        VideoRegistryEntry entry = ResolveEntry(videoId);
        return entry.GetFrame(frameIndex, targetWidth, targetHeight);
    }

    /// <summary>
    /// Returns container/codec metadata for a registered video. Opens the FFmpeg
    /// context if not already open.
    /// </summary>
    public VideoMetadata GetMetadata(uint videoId)
    {
        VideoRegistryEntry entry = ResolveEntry(videoId);
        return entry.GetMetadata();
    }

    private VideoRegistryEntry ResolveEntry(uint videoId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(videoId, out VideoRegistryEntry? entry))
            {
                throw new InvalidOperationException(
                    $"VideoRegistry has no entry for videoId={videoId}. " +
                    "Ids are assigned by Register* and remain valid for the registry's lifetime.");
            }
            return entry;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            foreach (VideoRegistryEntry entry in _entries.Values)
            {
                entry.Dispose();
            }
            _entries.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VideoRegistry));
        }
    }
}

/// <summary>
/// Container/codec facts for a registered video. <see cref="FrameCount"/> is
/// <see langword="null"/> when the source container does not store it (some
/// streaming containers and partial reads).
/// </summary>
public readonly record struct VideoMetadata(
    int Width,
    int Height,
    double AvgFps,
    TimeSpan Duration,
    long? FrameCount,
    string CodecName,
    AVPixelFormat NativePixelFormat);

/// <summary>
/// Pixel payload of a decoded frame. <see cref="Bgra8888Pixels"/> is laid out as
/// <c>Height × Width × 4</c> bytes in B/G/R/A order with no row padding — the
/// native pixel layout SkiaSharp uses on Windows, so consumers can wrap the buffer
/// directly in an <c>SKBitmap</c>.
/// </summary>
public readonly struct MaterializedFrame
{
    /// <summary>Constructs a frame with the given dimensions and pixel buffer.</summary>
    public MaterializedFrame(int width, int height, byte[] bgra8888Pixels)
    {
        Width = width;
        Height = height;
        Bgra8888Pixels = bgra8888Pixels;
    }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Packed BGRA8888 pixel bytes laid out as <c>Height × Width × 4</c>, top-to-bottom,
    /// with no row padding. Buffer length is exactly <c>Width * Height * 4</c>.
    /// </summary>
    public byte[] Bgra8888Pixels { get; }
}

/// <summary>
/// Holds the warm FFmpeg decoder state for a single registered video. Internal to
/// the registry; one instance per <c>VideoRegistry.Register*</c> call.
/// </summary>
internal sealed class VideoRegistryEntry : IDisposable
{
    private readonly string _label;
    // Exactly one of these is non-null. Path-based sources let FFmpeg's stock
    // IO open the URL; stream-based sources go through IOContext.ReadStream.
    private readonly string? _path;
    private readonly Func<Stream>? _streamFactory;

    private readonly object _decoderLock = new();

    // Lazy-init on first GetMetadata / GetFrame call.
    private FormatContext? _fc;
    private CodecContext? _codecCtx;
    private MediaStream _videoStream;
    // swscale contexts are keyed on (target_w, target_h). One converter per
    // target resolution; queries that materialise many frames at one target
    // size share a single converter, so the per-frame cost is just the
    // scaling pass, not context recreation.
    private readonly Dictionary<(int Width, int Height), VideoFrameConverter> _converters = [];
    // Stream / IOContext are owned by this entry when the source is non-path-based.
    private Stream? _ownedStream;
    private IOContext? _ioContext;

    // Tracks the index of the most-recently emitted decoded frame.
    // -1 means "no frame decoded yet on the current decoder position".
    private int _lastEmittedFrameIndex = -1;

    private int _disposed;

    public VideoRegistryEntry(string path)
    {
        _path = path;
        _label = path;
    }

    public VideoRegistryEntry(string label, Func<Stream> streamFactory)
    {
        _label = label;
        _streamFactory = streamFactory;
    }

    public VideoMetadata GetMetadata()
    {
        lock (_decoderLock)
        {
            ThrowIfDisposed();
            EnsureOpen();

            CodecContext cc = _codecCtx!;
            MediaStream s = _videoStream;
            AVRational fps = s.AvgFrameRate;
            double fpsValue = fps.Den == 0 ? 0.0 : (double)fps.Num / fps.Den;
            long durationTicks = s.Duration != ffmpeg.AV_NOPTS_VALUE && s.TimeBase.Num > 0
                ? s.Duration * TimeSpan.TicksPerSecond * s.TimeBase.Num / s.TimeBase.Den
                : 0L;
            long? frameCount = s.NbFrames > 0 ? s.NbFrames : null;
            return new VideoMetadata(
                Width: cc.Width,
                Height: cc.Height,
                AvgFps: fpsValue,
                Duration: TimeSpan.FromTicks(durationTicks),
                FrameCount: frameCount,
                CodecName: cc.Codec.Name ?? "(unknown)",
                NativePixelFormat: cc.PixelFormat);
        }
    }

    public MaterializedFrame GetFrame(int frameIndex, int? targetWidth = null, int? targetHeight = null)
    {
        if (frameIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameIndex), frameIndex,
                "Negative frame indices (relative-from-end) are not yet supported by VideoRegistry.");
        }

        lock (_decoderLock)
        {
            ThrowIfDisposed();
            EnsureOpen();

            (int finalWidth, int finalHeight) = ResolveTargetDimensions(targetWidth, targetHeight);

            // Backward / re-read: seek to file start and reset position counter.
            if (frameIndex <= _lastEmittedFrameIndex)
            {
                SeekToStart();
            }

            return DecodeForwardTo(frameIndex, finalWidth, finalHeight);
        }
    }

    /// <summary>
    /// Resolves the final <c>(width, height)</c> pair given optional caller
    /// hints and the source's native dimensions. Aspect ratio is preserved when
    /// the caller supplied exactly one dimension.
    /// </summary>
    private (int Width, int Height) ResolveTargetDimensions(int? targetWidth, int? targetHeight)
    {
        int srcWidth = _codecCtx!.Width;
        int srcHeight = _codecCtx.Height;
        if (targetWidth is null && targetHeight is null)
        {
            return (srcWidth, srcHeight);
        }
        if (targetWidth is int w && targetHeight is int h)
        {
            return (w, h);
        }
        if (targetWidth is int wOnly)
        {
            int hComputed = (int)Math.Round((double)srcHeight * wOnly / srcWidth);
            return (wOnly, Math.Max(1, hComputed));
        }
        int hOnly = targetHeight!.Value;
        int wComputed = (int)Math.Round((double)srcWidth * hOnly / srcHeight);
        return (Math.Max(1, wComputed), hOnly);
    }

    private VideoFrameConverter GetOrCreateConverter(int width, int height)
    {
        if (!_converters.TryGetValue((width, height), out VideoFrameConverter? converter))
        {
            converter = new VideoFrameConverter();
            _converters[(width, height)] = converter;
        }
        return converter;
    }

    private const int BytesPerPixel = 4; // BGRA8888

    // Internal buffer size for IOContext-wrapped streams. 32 KiB is the common
    // default for FFmpeg's stock IO and large enough to amortise the
    // managed-stream call overhead without committing meaningful memory.
    private const int StreamBufferSize = 32 * 1024;

    private void EnsureOpen()
    {
        if (_fc is not null) return;

        FormatContext fc;
        Stream? stream = null;
        IOContext? io = null;
        if (_path is not null)
        {
            fc = FormatContext.OpenInputUrl(_path);
        }
        else
        {
            stream = _streamFactory!();
            io = IOContext.ReadStream(stream, StreamBufferSize);
            fc = FormatContext.OpenInputIO(io);
        }
        try
        {
            fc.LoadStreamInfo();
            MediaStream? videoStream = fc.FindBestStreamOrNull(AVMediaType.Video);
            if (videoStream is null)
            {
                throw new InvalidOperationException(
                    $"Video source '{_label}' contains no video stream FFmpeg can decode.");
            }
            MediaStream s = videoStream.Value;
            Codec codec = Codec.FindDecoderById(s.Codecpar!.CodecId);
            CodecContext codecCtx = new(codec);
            codecCtx.FillParameters(s.Codecpar);
            codecCtx.Open(codec);

            _fc = fc;
            _videoStream = s;
            _codecCtx = codecCtx;
            _ownedStream = stream;
            _ioContext = io;
            _lastEmittedFrameIndex = -1;
        }
        catch
        {
            fc.Free();
            io?.Dispose();
            stream?.Dispose();
            throw;
        }
    }

    private void SeekToStart()
    {
        // Seek the video stream to timestamp 0 with backward flag to ensure we land
        // on a keyframe at or before the start, then flush the decoder so any
        // buffered frames don't bleed across the seek boundary. Sdcb wraps
        // av_seek_frame but not avcodec_flush_buffers, so we call the raw API for
        // the flush — same lifecycle discipline as the OnnxRuntimeSession work.
        _fc!.SeekFrame(timestamp: 0, _videoStream.Index, AVSEEK_FLAG.Backward);
        unsafe
        {
            ffmpeg.avcodec_flush_buffers(_codecCtx!);
        }
        _lastEmittedFrameIndex = -1;
    }

    private MaterializedFrame DecodeForwardTo(int targetFrameIndex, int outWidth, int outHeight)
    {
        using Packet packet = new();
        using Frame decoded = new();

        while (true)
        {
            // Pull packets until we have one for our video stream (or EOF).
            CodecResult readResult;
            do
            {
                packet.Unref();
                readResult = _fc!.ReadFrame(packet);
                if (readResult == CodecResult.EOF)
                {
                    // Signal end-of-stream to the decoder so it flushes any frames
                    // buffered after the last received packet. Sdcb's SendPacket
                    // doesn't accept null, so route through the raw API.
                    unsafe
                    {
                        ffmpeg.avcodec_send_packet(_codecCtx!, null);
                    }
                    return DrainAfterEof(decoded, targetFrameIndex, outWidth, outHeight);
                }
                // Non-EOF non-success is unexpected; treat as error.
            }
            while (packet.StreamIndex != _videoStream.Index);

            _codecCtx!.SendPacket(packet);
            packet.Unref();

            while (true)
            {
                CodecResult recv = _codecCtx.ReceiveFrame(decoded);
                if (recv == CodecResult.Again) break;          // need more packets
                if (recv == CodecResult.EOF)
                {
                    throw new InvalidOperationException(
                        $"Decoder reported EOF before reaching frame {targetFrameIndex} " +
                        $"in '{_label}' (last emitted={_lastEmittedFrameIndex}).");
                }
                // recv == Success: decoded carries the next frame.
                _lastEmittedFrameIndex++;
                if (_lastEmittedFrameIndex == targetFrameIndex)
                {
                    return ConvertToBgra8888(decoded, outWidth, outHeight);
                }
                // Skip past it — keep decoding.
            }
        }
    }

    private MaterializedFrame DrainAfterEof(Frame decoded, int targetFrameIndex, int outWidth, int outHeight)
    {
        while (true)
        {
            CodecResult recv = _codecCtx!.ReceiveFrame(decoded);
            if (recv == CodecResult.EOF || recv == CodecResult.Again)
            {
                throw new InvalidOperationException(
                    $"Reached end of '{_label}' before producing frame {targetFrameIndex} " +
                    $"(last emitted={_lastEmittedFrameIndex}).");
            }
            _lastEmittedFrameIndex++;
            if (_lastEmittedFrameIndex == targetFrameIndex)
            {
                return ConvertToBgra8888(decoded, outWidth, outHeight);
            }
        }
    }

    private MaterializedFrame ConvertToBgra8888(Frame source, int width, int height)
    {
        VideoFrameConverter converter = GetOrCreateConverter(width, height);

        using Frame target = new();
        target.Width = width;
        target.Height = height;
        target.Format = (int)AVPixelFormat.Bgra;
        target.EnsureBuffer(align: 1);

        converter.ConvertFrame(source, target, SWS.Bilinear);

        // Copy row-by-row so the output buffer is exactly width*4*height with no
        // gaps, regardless of any per-row alignment padding FFmpeg chose.
        int bytesPerRow = width * BytesPerPixel;
        byte[] bgra = new byte[bytesPerRow * height];
        int srcStride = target.Linesize[0];
        unsafe
        {
            byte* src = (byte*)target.Data[0].ToPointer();
            for (int y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>(src + y * srcStride, bytesPerRow)
                    .CopyTo(bgra.AsSpan(y * bytesPerRow, bytesPerRow));
            }
        }

        return new MaterializedFrame(width, height, bgra);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_decoderLock)
        {
            foreach (VideoFrameConverter c in _converters.Values) c.Free();
            _converters.Clear();
            _codecCtx?.Free();
            _fc?.Free();
            _ioContext?.Dispose();
            _ownedStream?.Dispose();
            _codecCtx = null;
            _fc = null;
            _ioContext = null;
            _ownedStream = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VideoRegistryEntry));
        }
    }
}
