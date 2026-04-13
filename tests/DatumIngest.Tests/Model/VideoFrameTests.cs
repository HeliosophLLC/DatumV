using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Covers the new <see cref="DataKind.VideoFrame"/> data type and the
/// <see cref="VideoRegistry"/> that materialises its handles into pixel bytes.
/// Decode-path tests run against <c>Fixtures/spike.mp4</c> — a 72-frame
/// 1920×1080 H.264 clip checked into the repo.
/// </summary>
public sealed class VideoFrameTests : ServiceTestBase
{
    private static string SpikeVideoPath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "spike.mp4");

    // ─────────────────────── DataValue API ───────────────────────

    [Fact]
    public void VideoFrame_FactoryRoundTripsVideoIdAndFrameIndex()
    {
        DataValue value = DataValue.FromVideoFrame(videoId: 7, frameIndex: 42);

        Assert.Equal(DataKind.VideoFrame, value.Kind);
        Assert.True(value.IsInline);
        Assert.False(value.IsNull);
        Assert.False(value.IsArray);

        (uint videoId, int frameIndex) = value.AsVideoFrame();
        Assert.Equal(7u, videoId);
        Assert.Equal(42, frameIndex);
    }

    [Fact]
    public void VideoFrame_HandlesLargeVideoIdAndNegativeFrameSentinel()
    {
        // Full uint range for videoId; negative frameIndex slot reserved for
        // relative-from-end semantics is still representable on the DataValue side.
        DataValue value = DataValue.FromVideoFrame(uint.MaxValue, frameIndex: -1);
        (uint videoId, int frameIndex) = value.AsVideoFrame();
        Assert.Equal(uint.MaxValue, videoId);
        Assert.Equal(-1, frameIndex);
    }

    [Fact]
    public void VideoFrame_EqualityAndHashMatchPayload()
    {
        DataValue a = DataValue.FromVideoFrame(3, 10);
        DataValue b = DataValue.FromVideoFrame(3, 10);
        DataValue differentFrame = DataValue.FromVideoFrame(3, 11);
        DataValue differentVideo = DataValue.FromVideoFrame(4, 10);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, differentFrame);
        Assert.NotEqual(a, differentVideo);
    }

    [Fact]
    public void VideoFrame_AccessorThrowsOnWrongKind()
    {
        DataValue notAVideoFrame = DataValue.FromInt32(0);
        Assert.Throws<InvalidOperationException>(() => notAVideoFrame.AsVideoFrame());
    }

    [Fact]
    public void VideoFrame_TypedNullHasVideoFrameKind()
    {
        DataValue typedNull = DataValue.Null(DataKind.VideoFrame);
        Assert.Equal(DataKind.VideoFrame, typedNull.Kind);
        Assert.True(typedNull.IsNull);
    }

    // ─────────────────────── VideoRegistry decode-path ───────────────────────

    [Fact]
    public void Registry_AssignsDistinctNonZeroIds()
    {
        using VideoRegistry registry = new();
        uint a = registry.RegisterPath(SpikeVideoPath());
        uint b = registry.RegisterPath(SpikeVideoPath());
        Assert.NotEqual(0u, a);
        Assert.NotEqual(0u, b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Registry_GetMetadataReturnsContainerFacts()
    {
        Assert.True(File.Exists(SpikeVideoPath()),
            $"Fixture missing at {SpikeVideoPath()}. Check tests/DatumIngest.Tests/Fixtures/spike.mp4 is present and copied to bin output.");

        using VideoRegistry registry = new();
        uint id = registry.RegisterPath(SpikeVideoPath());
        VideoMetadata md = registry.GetMetadata(id);

        Assert.Equal(1920, md.Width);
        Assert.Equal(1080, md.Height);
        Assert.Equal("h264", md.CodecName);
        Assert.InRange(md.AvgFps, 29.0, 30.5);
        // 72 frames at ~30fps ≈ 2.4 seconds
        Assert.InRange(md.Duration.TotalSeconds, 2.0, 3.0);
        Assert.Equal(72L, md.FrameCount);
    }

    [Fact]
    public void Registry_MaterializesFrameZeroAtSourceResolution()
    {
        using VideoRegistry registry = new();
        uint id = registry.RegisterPath(SpikeVideoPath());

        MaterializedFrame frame = registry.Materialize(id, frameIndex: 0);

        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(1920 * 1080 * 4, frame.Bgra8888Pixels.Length);
    }

    [Fact]
    public void Registry_SequentialMaterializeReturnsDistinctFrames()
    {
        using VideoRegistry registry = new();
        uint id = registry.RegisterPath(SpikeVideoPath());

        MaterializedFrame f0 = registry.Materialize(id, 0);
        MaterializedFrame f1 = registry.Materialize(id, 1);
        MaterializedFrame f5 = registry.Materialize(id, 5);

        Assert.Equal(f0.Width, f1.Width);
        Assert.Equal(f0.Width, f5.Width);
        // Real video — adjacent frames must differ on at least one pixel byte.
        Assert.False(f0.Bgra8888Pixels.AsSpan().SequenceEqual(f1.Bgra8888Pixels.AsSpan()),
            "Sequential frames decoded identical bytes — warm-decoder advance is not working.");
        Assert.False(f1.Bgra8888Pixels.AsSpan().SequenceEqual(f5.Bgra8888Pixels.AsSpan()),
            "Frames 1 and 5 decoded identical bytes.");
    }

    [Fact]
    public void Registry_BackwardAccessReseekToStartAndDecodesForward()
    {
        using VideoRegistry registry = new();
        uint id = registry.RegisterPath(SpikeVideoPath());

        // Advance forward, then ask for an earlier frame — registry must seek back
        // to the file head and replay decode forward.
        MaterializedFrame f0a = registry.Materialize(id, 0);
        _ = registry.Materialize(id, 10);
        MaterializedFrame f0b = registry.Materialize(id, 0);

        Assert.True(f0a.Bgra8888Pixels.AsSpan().SequenceEqual(f0b.Bgra8888Pixels.AsSpan()),
            "Re-fetching frame 0 after a forward seek produced different bytes — seek-and-replay isn't deterministic.");
    }

    [Fact]
    public void Registry_RejectsUnknownVideoId()
    {
        using VideoRegistry registry = new();
        Assert.Throws<InvalidOperationException>(() => registry.Materialize(videoId: 999, frameIndex: 0));
    }

    [Fact]
    public void Registry_RejectsNegativeFrameIndex()
    {
        using VideoRegistry registry = new();
        uint id = registry.RegisterPath(SpikeVideoPath());
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Materialize(id, frameIndex: -1));
    }

    [Fact]
    public void Registry_DisposeReleasesNativeStateAndBlocksFurtherUse()
    {
        VideoRegistry registry = new();
        uint id = registry.RegisterPath(SpikeVideoPath());
        _ = registry.Materialize(id, 0);

        registry.Dispose();
        Assert.Throws<ObjectDisposedException>(() => registry.RegisterPath(SpikeVideoPath()));
        Assert.Throws<ObjectDisposedException>(() => registry.Materialize(id, 1));
    }

    // ─────────────────────── ExecutionContext wiring ───────────────────────

    // ─────────────────────── RegisterSidecar + RegisterBytes ───────────────────────

    [Fact]
    public void Registry_RegisterBytes_MaterializesFromInMemoryContainer()
    {
        byte[] containerBytes = File.ReadAllBytes(SpikeVideoPath());
        using VideoRegistry registry = new();
        uint id = registry.RegisterBytes(containerBytes);

        VideoMetadata md = registry.GetMetadata(id);
        Assert.Equal(1920, md.Width);
        Assert.Equal(1080, md.Height);

        MaterializedFrame frame = registry.Materialize(id, 0);
        Assert.Equal(1920 * 1080 * 4, frame.Bgra8888Pixels.Length);
    }

    [Fact]
    public void Registry_RegisterSidecar_MaterializesThroughBlobSourceStream()
    {
        // Wrap the spike video bytes in a fake IBlobSource and register a
        // sidecar window covering the whole payload. This exercises the
        // BlobSourceStream → IOContext.ReadStream → FFmpeg path end-to-end
        // without needing a real .datum-blob file.
        byte[] containerBytes = File.ReadAllBytes(SpikeVideoPath());
        using InMemoryBlobSource source = new(containerBytes);
        using VideoRegistry registry = new();
        uint id = registry.RegisterSidecar(source, offset: 0, length: containerBytes.LongLength);

        VideoMetadata md = registry.GetMetadata(id);
        Assert.Equal(1920, md.Width);
        Assert.Equal(1080, md.Height);
        Assert.Equal("h264", md.CodecName);

        MaterializedFrame f0 = registry.Materialize(id, 0);
        MaterializedFrame f1 = registry.Materialize(id, 1);
        Assert.Equal(1920 * 1080 * 4, f0.Bgra8888Pixels.Length);
        Assert.False(f0.Bgra8888Pixels.AsSpan().SequenceEqual(f1.Bgra8888Pixels.AsSpan()),
            "Sequential frames from sidecar source decoded identical bytes — the BlobSourceStream may not be advancing.");
    }

    [Fact]
    public void Registry_RegisterSidecar_OffsetWindow_DecodesPayloadAtNonZeroBase()
    {
        // Embed the video bytes inside a larger buffer at a non-zero offset.
        // Verifies BlobSourceStream's baseOffset semantics: FFmpeg sees position 0
        // mapping to the prefix-skipped payload, not the start of the buffer.
        byte[] videoBytes = File.ReadAllBytes(SpikeVideoPath());
        const int Prefix = 512;
        byte[] padded = new byte[Prefix + videoBytes.Length + 256];
        // Fill prefix and suffix with sentinel so any drift into them would corrupt decode.
        for (int i = 0; i < padded.Length; i++) padded[i] = 0xAB;
        Buffer.BlockCopy(videoBytes, 0, padded, Prefix, videoBytes.Length);

        using InMemoryBlobSource source = new(padded);
        using VideoRegistry registry = new();
        uint id = registry.RegisterSidecar(source, offset: Prefix, length: videoBytes.LongLength);

        VideoMetadata md = registry.GetMetadata(id);
        Assert.Equal(1920, md.Width);
        MaterializedFrame f0 = registry.Materialize(id, 0);
        Assert.Equal(1920 * 1080 * 4, f0.Bgra8888Pixels.Length);
    }

    // ─────────────────────── ExecutionContext wiring ───────────────────────

    [Fact]
    public void ExecutionContext_AllocatesAndOwnsVideoRegistry()
    {
        Pool pool = CreatePool();
        ExecutionContext ctx = new DatumIngest.Execution.ExecutionContext(CreateCatalog());

        Assert.NotNull(ctx.VideoRegistry);

        // Owned registry — using it after Dispose should fail with ObjectDisposedException.
        VideoRegistry registry = ctx.VideoRegistry;
        ctx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => registry.RegisterPath(SpikeVideoPath()));
    }

    [Fact]
    public void ExecutionContext_BorrowedRegistrySurvivesChildDispose()
    {
        Pool pool = CreatePool();
        VideoRegistry shared = new();
        try
        {
            ExecutionContext child = new DatumIngest.Execution.ExecutionContext(CreateCatalog(),
                videoRegistry: shared);
            Assert.Same(shared, child.VideoRegistry);
            child.Dispose();

            // Shared registry must remain usable after child disposes.
            uint id = shared.RegisterPath(SpikeVideoPath());
            Assert.NotEqual(0u, id);
        }
        finally
        {
            shared.Dispose();
        }
    }

    // ─────────────────────── Test double ───────────────────────

    private sealed class InMemoryBlobSource(byte[] payload) : IBlobSource
    {
        private readonly byte[] _payload = payload;
        public ReadOnlySpan<byte> Read(long offset, long length) =>
            _payload.AsSpan((int)offset, (int)length);
        public void Dispose() { }
    }
}
