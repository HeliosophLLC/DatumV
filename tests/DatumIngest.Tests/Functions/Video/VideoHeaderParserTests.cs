using DatumIngest.Functions.Video;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Video;

/// <summary>
/// Tests for the Sdcb.FFmpeg-based video header parser and the inline-metadata
/// stamping path. Runs against <c>Fixtures/spike.mp4</c> — a 72-frame 1920×1080
/// H.264 clip already in the repo for video-frame tests.
/// </summary>
public sealed class VideoHeaderParserTests : ServiceTestBase
{
    private static string SpikeVideoPath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "spike.mp4");

    [Fact]
    public void TryParseHeader_Spike_ExtractsWidthHeightCodec()
    {
        byte[] bytes = File.ReadAllBytes(SpikeVideoPath());

        VideoHeaderMetadata? meta = VideoHeaderParser.TryParseHeader(bytes);

        Assert.NotNull(meta);
        Assert.Equal((ushort)1920, meta!.Width);
        Assert.Equal((ushort)1080, meta.Height);
        // spike.mp4 is H.264 — codec discriminator 1 in our enum mapping.
        Assert.Equal((byte)1, meta.Codec);
        // FPS should be non-zero; precise value depends on encoder but spike is ~30 fps.
        Assert.True(meta.FpsX256 > 0, $"expected non-zero fps_x256, got {meta.FpsX256}");
    }

    [Fact]
    public void TryParseHeader_NonVideoBytes_ReturnsNull()
    {
        byte[] notVideo = new byte[256];
        notVideo[0] = 0xFF;
        notVideo[1] = 0xD8;
        notVideo[2] = 0xFF;
        notVideo[3] = 0xE0;
        Assert.Null(VideoHeaderParser.TryParseHeader(notVideo));
    }

    [Fact]
    public void TryParseHeader_TooShort_ReturnsNull()
    {
        Assert.Null(VideoHeaderParser.TryParseHeader([0x00, 0x00, 0x00]));
    }

    [Fact]
    public void VideoDataValueFactory_FromEncodedBytes_StampsMetadataOntoDataValue()
    {
        byte[] bytes = File.ReadAllBytes(SpikeVideoPath());
        using Arena store = CreateArena();

        DataValue dv = VideoDataValueFactory.FromEncodedBytes(bytes, store);

        Assert.Equal(DataKind.Video, dv.Kind);
        Assert.Equal((ushort)1920, dv.VideoWidth);
        Assert.Equal((ushort)1080, dv.VideoHeight);
        Assert.Equal((byte)1, dv.VideoCodec); // H.264
        Assert.True(dv.VideoFpsX256 > 0, $"expected non-zero fps_x256, got {dv.VideoFpsX256}");
    }

    [Fact]
    public void VideoDataValueFactory_UnknownFormat_FallsBackToNoMetadata()
    {
        // Random bytes — FFmpeg should fail to open. Factory should produce a valid
        // Video value with zero-sentinel metadata so accessors return 0 and SQL
        // functions return NULL.
        byte[] mystery = new byte[1024];
        for (int i = 0; i < mystery.Length; i++) mystery[i] = (byte)(i & 0xFF);
        using Arena store = CreateArena();

        DataValue dv = VideoDataValueFactory.FromEncodedBytes(mystery, store);

        Assert.Equal(DataKind.Video, dv.Kind);
        Assert.Equal((ushort)0, dv.VideoWidth);
        Assert.Equal((ushort)0, dv.VideoHeight);
        Assert.Equal((byte)0, dv.VideoCodec);
    }
}
