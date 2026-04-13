using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="VideoFrameToImageFunction"/>. Direct unit tests
/// drive the function with a hand-built <see cref="EvaluationFrame"/> carrying
/// a <see cref="VideoRegistry"/>; the end-to-end tests plan
/// <c>SELECT video_frame_to_image(frame) FROM video_unnest_frames(...)</c>
/// through the real query engine to confirm the registry flows from execution
/// context through evaluator → frame → function.
/// </summary>
public sealed class VideoFrameToImageFunctionTests : ServiceTestBase
{
    private static string SpikeVideoPath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "spike.mp4");

    // ─────────────────────── Direct unit tests ───────────────────────

    [Fact]
    public async Task Materializes_FullResolution_SKBitmap()
    {
        using VideoRegistry registry = new();
        uint videoId = registry.RegisterPath(SpikeVideoPath());

        ValueRef result = await new VideoFrameToImageFunction().ExecuteAsync(
            new[] { ValueRef.FromInline(DataValue.FromVideoFrame(videoId, frameIndex: 0)) },
            MakeFrameWith(registry),
            default);

        Assert.Equal(DataKind.Image, result.Kind);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(1920, bmp.Width);
        Assert.Equal(1080, bmp.Height);
        Assert.Equal(SKColorType.Bgra8888, bmp.ColorType);
    }

    [Fact]
    public async Task SequentialFrames_DecodeWithoutSeek()
    {
        using VideoRegistry registry = new();
        uint videoId = registry.RegisterPath(SpikeVideoPath());

        VideoFrameToImageFunction fn = new();
        SKBitmap frame0 = (await fn.ExecuteAsync(
            new[] { ValueRef.FromInline(DataValue.FromVideoFrame(videoId, 0)) },
            MakeFrameWith(registry), default)).AsImage();
        SKBitmap frame1 = (await fn.ExecuteAsync(
            new[] { ValueRef.FromInline(DataValue.FromVideoFrame(videoId, 1)) },
            MakeFrameWith(registry), default)).AsImage();

        Assert.Equal(frame0.Width, frame1.Width);
        Assert.Equal(frame0.Height, frame1.Height);
        // Real-video frames must differ.
        Assert.False(BitmapsEqual(frame0, frame1),
            "Sequential frames decoded identical bitmaps via to_image — sequential warm-decoder path may be broken.");
    }

    [Fact]
    public async Task NullInput_ReturnsNullImage()
    {
        using VideoRegistry registry = new();
        ValueRef result = await new VideoFrameToImageFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.VideoFrame) },
            MakeFrameWith(registry),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    // ─────────────────────── Target-dimension overloads ───────────────────────

    [Fact]
    public async Task TargetWidth_Only_PreservesSourceAspectRatio()
    {
        using VideoRegistry registry = new();
        uint videoId = registry.RegisterPath(SpikeVideoPath());

        ValueRef result = await new VideoFrameToImageFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromInline(DataValue.FromVideoFrame(videoId, 0)),
                ValueRef.FromInt32(640),
            },
            MakeFrameWith(registry),
            default);

        SKBitmap bmp = result.AsImage();
        Assert.Equal(640, bmp.Width);
        // 1920×1080 → at width=640, height should be 360 (preserve 16:9 aspect).
        Assert.Equal(360, bmp.Height);
    }

    [Fact]
    public async Task TargetWidthAndHeight_UseExactDimensions()
    {
        using VideoRegistry registry = new();
        uint videoId = registry.RegisterPath(SpikeVideoPath());

        // 384×384 — typical depth-model input. Source aspect is not preserved.
        ValueRef result = await new VideoFrameToImageFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromInline(DataValue.FromVideoFrame(videoId, 0)),
                ValueRef.FromInt32(384),
                ValueRef.FromInt32(384),
            },
            MakeFrameWith(registry),
            default);

        SKBitmap bmp = result.AsImage();
        Assert.Equal(384, bmp.Width);
        Assert.Equal(384, bmp.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveTargetDimension_Throws(int badWidth)
    {
        using VideoRegistry registry = new();
        uint videoId = registry.RegisterPath(SpikeVideoPath());

        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new VideoFrameToImageFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromInline(DataValue.FromVideoFrame(videoId, 0)),
                    ValueRef.FromInt32(badWidth),
                },
                MakeFrameWith(registry),
                default));
    }

    // ─────────────────────── End-to-end SQL ───────────────────────

    [Fact]
    public async Task EndToEnd_SelectVideoFrameToImageFromVideoUnnestFrames_DecodesImagesViaQueryEngine()
    {
        // Use forward slashes so the SQL string literal survives without escaping.
        string videoPath = SpikeVideoPath().Replace('\\', '/');
        string sql = $"SELECT video_frame_to_image(frame) AS img FROM video_unnest_frames('{videoPath}', 0, 1, 3)";

        DatumIngest.Catalog.TableCatalog catalog = CreateCatalog();
        IQueryPlan plan = catalog.Plan(sql);

        int rowsSeen = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i]["img"];
                Assert.Equal(DataKind.Image, cell.Kind);
                Assert.False(cell.IsNull);
                byte[] encoded = cell.AsImage(batch.Arena);
                Assert.NotEmpty(encoded);
                using SKBitmap decoded = SKBitmap.Decode(encoded);
                Assert.NotNull(decoded);
                Assert.Equal(1920, decoded.Width);
                Assert.Equal(1080, decoded.Height);
                rowsSeen++;
            }
        }

        Assert.Equal(3, rowsSeen);
    }

    [Fact]
    public async Task EndToEnd_WithTargetWidth_PreservesAspectRatioInResultStream()
    {
        string videoPath = SpikeVideoPath().Replace('\\', '/');
        string sql = $"SELECT video_frame_to_image(frame, 384) AS img FROM video_unnest_frames('{videoPath}', 0, 1, 2)";
        DatumIngest.Catalog.TableCatalog catalog = CreateCatalog();
        IQueryPlan plan = catalog.Plan(sql);

        int rowsSeen = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                byte[] encoded = batch[i]["img"].AsImage(batch.Arena);
                using SKBitmap decoded = SKBitmap.Decode(encoded);
                Assert.Equal(384, decoded.Width);
                Assert.Equal(216, decoded.Height); // 384 / (1920/1080) = 216
                rowsSeen++;
            }
        }
        Assert.Equal(2, rowsSeen);
    }

    [Fact]
    public async Task EndToEnd_CrossApplyOverVideoColumn_DecodesImages()
    {
        // Exercises the full pipeline a user runs against an ingested table:
        // a Video column joined to video_unnest_frames via CROSS APPLY, with
        // each emitted frame fed through video_frame_to_image.
        byte[] videoBytes = System.IO.File.ReadAllBytes(SpikeVideoPath());
        DatumIngest.Catalog.TableCatalog catalog = CreateCatalog();
        catalog.Add(new DatumIngest.Catalog.Providers.InMemoryTableProvider(
            CreatePool(), "videos", ["video"], [DataKind.Video],
            [new object?[] { videoBytes }]));

        const string sql =
            "SELECT video_frame_to_image(f.frame, 384) AS img " +
            "FROM videos AS v " +
            "CROSS APPLY video_unnest_frames(v.video, 0, 1, 3) AS f";
        IQueryPlan plan = catalog.Plan(sql);

        int rowsSeen = 0;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                byte[] encoded = batch[i]["img"].AsImage(batch.Arena);
                using SKBitmap decoded = SKBitmap.Decode(encoded);
                Assert.Equal(384, decoded.Width);
                Assert.Equal(216, decoded.Height);
                rowsSeen++;
            }
        }
        Assert.Equal(3, rowsSeen);
    }

    // ─────────────────────── Helpers ───────────────────────

    private EvaluationFrame MakeFrameWith(VideoRegistry registry)
    {
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(
            videoRegistry: registry
        );
        return CreateEvaluationFrame(context: context);
    }

    private static bool BitmapsEqual(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        ReadOnlySpan<byte> pa = a.GetPixelSpan();
        ReadOnlySpan<byte> pb = b.GetPixelSpan();
        return pa.SequenceEqual(pb);
    }
}
