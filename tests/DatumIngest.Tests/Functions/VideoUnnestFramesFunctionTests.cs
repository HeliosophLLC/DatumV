using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="VideoUnnestFramesFunction"/>. The emission path is
/// pure handle-construction (no decode), so most of these run instantly. One
/// end-to-end test takes an emitted handle and materialises it through the
/// per-query <see cref="VideoRegistry"/> to verify the wiring stays intact.
/// </summary>
public sealed class VideoUnnestFramesFunctionTests : ServiceTestBase
{
    private readonly ITableValuedFunction _function = new VideoUnnestFramesFunction();

    private static string SpikeVideoPath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "spike.mp4");

    // ─────────────────────── Metadata / schema ───────────────────────

    [Fact]
    public void Name_IsVideoUnnestFrames()
    {
        Assert.Equal("video_unnest_frames", VideoUnnestFramesFunction.Name);
    }

    [Fact]
    public void Schema_TwoColumns_FrameIndexInt32_FrameVideoFrame()
    {
        Schema schema = _function.ValidateArguments([DataKind.String]);
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("frame_index", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal("frame", schema.Columns[1].Name);
        Assert.Equal(DataKind.VideoFrame, schema.Columns[1].Kind);
    }

    [Fact]
    public void Schema_RejectsZeroArguments()
    {
        Assert.Throws<FunctionArgumentException>(() => _function.ValidateArguments([]));
    }

    [Fact]
    public void Schema_RejectsFiveArguments()
    {
        Assert.Throws<FunctionArgumentException>(() => _function.ValidateArguments(
            [DataKind.String, DataKind.Int32, DataKind.Int32, DataKind.Int32, DataKind.Int32]));
    }

    [Fact]
    public void Schema_RejectsNonStringPath()
    {
        Assert.Throws<FunctionArgumentException>(() => _function.ValidateArguments([DataKind.Int32]));
    }

    [Fact]
    public void Schema_AcceptsVideoKind()
    {
        Schema schema = _function.ValidateArguments([DataKind.Video]);
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal(DataKind.VideoFrame, schema.Columns[1].Kind);
    }

    [Fact]
    public void Schema_RejectsNonNumericStartFrame()
    {
        Assert.Throws<FunctionArgumentException>(() => _function.ValidateArguments(
            [DataKind.String, DataKind.String]));
    }

    // ─────────────────────── Execution ───────────────────────

    [Fact]
    public async Task DefaultArgs_EmitsAllSeventyTwoFramesInOrder()
    {
        List<(int FrameIndex, uint VideoId, int HandleFrameIndex)> rows =
            await CollectAsync([ValueRef.FromString(SpikeVideoPath())]);

        Assert.Equal(72, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i, rows[i].FrameIndex);
            Assert.Equal(i, rows[i].HandleFrameIndex);
        }
        // All rows reference the same registered video.
        Assert.True(rows.All(r => r.VideoId == rows[0].VideoId));
        Assert.NotEqual(0u, rows[0].VideoId);
    }

    [Fact]
    public async Task StartFrame_SkipsLeadingFrames()
    {
        List<(int FrameIndex, uint VideoId, int HandleFrameIndex)> rows =
            await CollectAsync([
                ValueRef.FromString(SpikeVideoPath()),
                ValueRef.FromInt32(10)]);

        Assert.Equal(62, rows.Count);
        Assert.Equal(10, rows[0].FrameIndex);
        Assert.Equal(71, rows[^1].FrameIndex);
    }

    [Fact]
    public async Task Stride_EmitsEveryNthFrame()
    {
        List<(int FrameIndex, uint VideoId, int HandleFrameIndex)> rows =
            await CollectAsync([
                ValueRef.FromString(SpikeVideoPath()),
                ValueRef.FromInt32(0),
                ValueRef.FromInt32(3)]);

        // 72 frames, every 3rd: 0, 3, 6, ..., 69 → 24 rows
        Assert.Equal(24, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i * 3, rows[i].FrameIndex);
        }
    }

    [Fact]
    public async Task MaxFrames_CapsRowCount()
    {
        List<(int FrameIndex, uint VideoId, int HandleFrameIndex)> rows =
            await CollectAsync([
                ValueRef.FromString(SpikeVideoPath()),
                ValueRef.FromInt32(0),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(10)]);

        Assert.Equal(10, rows.Count);
        Assert.Equal(0, rows[0].FrameIndex);
        Assert.Equal(9, rows[^1].FrameIndex);
    }

    [Fact]
    public async Task NegativeStartFrame_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await CollectAsync([
                ValueRef.FromString(SpikeVideoPath()),
                ValueRef.FromInt32(-1)]));
    }

    [Fact]
    public async Task ZeroStride_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await CollectAsync([
                ValueRef.FromString(SpikeVideoPath()),
                ValueRef.FromInt32(0),
                ValueRef.FromInt32(0)]));
    }

    // ─────────────────────── Video-kind input (arena-backed) ───────────────────────

    [Fact]
    public async Task VideoKindInput_RoutesThroughRegisterBytes_AndEmitsFrames()
    {
        byte[] containerBytes = File.ReadAllBytes(SpikeVideoPath());
        ValueRef videoArg = ValueRef.FromBytes(DataKind.Video, containerBytes);

        List<(int FrameIndex, uint VideoId, int HandleFrameIndex)> rows =
            await CollectAsync([videoArg, ValueRef.FromInt32(0), ValueRef.FromInt32(1), ValueRef.FromInt32(3)]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[0].FrameIndex);
        Assert.Equal(2, rows[^1].FrameIndex);
        Assert.True(rows.All(r => r.VideoId == rows[0].VideoId));
    }

    // ─────────────────────── End-to-end: handle → registry → pixels ───────────────────────

    [Fact]
    public async Task EmittedHandleMaterializesThroughRegistry()
    {
        ExecutionContext context = CreateExecutionContext();
        try
        {
            // Take just the first frame's handle and feed it back through the
            // registry attached to the same execution context. This is the
            // contract a future image-consuming function will rely on.
            await using IAsyncEnumerator<RowBatch> e = _function.ExecuteAsync(
                [ValueRef.FromString(SpikeVideoPath())], context).GetAsyncEnumerator();
            Assert.True(await e.MoveNextAsync(), "expected at least one batch");
            RowBatch first = e.Current;
            Assert.True(first.Count > 0);

            DataValue frameHandle = first[0]["frame"];
            Assert.Equal(DataKind.VideoFrame, frameHandle.Kind);
            (uint videoId, int frameIndex) = frameHandle.AsVideoFrame();
            Assert.Equal(0, frameIndex);

            MaterializedFrame materialised = context.VideoRegistry.Materialize(videoId, frameIndex);
            Assert.Equal(1920, materialised.Width);
            Assert.Equal(1080, materialised.Height);
            Assert.Equal(1920 * 1080 * 4, materialised.Bgra8888Pixels.Length);

            // Drain remaining batches so the iterator finalizer doesn't hold
            // a half-yielded batch.
            while (await e.MoveNextAsync()) { }
        }
        finally
        {
            context.Dispose();
        }
    }

    // ─────────────────────── End-to-end SQL note ───────────────────────
    //
    // A natural SQL form for "unnest frames from every row of a videos table" is
    //   SELECT to_image(f.frame)
    //   FROM videos AS v
    //   CROSS APPLY video_unnest_frames(v.video, 0, 1, 3) AS f
    //
    // The parser supports CROSS APPLY <TVF> (see SqlParserTests.CrossApply) via
    // the standalone Parse path, but the single-statement ParseStatement entry
    // point used by TableCatalog.PlanAsync does not yet accept it for arbitrary
    // TVFs. That is a parser/planner gap unrelated to the VideoRegistry work
    // shipped in this PR — the Video-kind argument path is covered end-to-end by
    // the direct-invocation test above (VideoKindInput_RoutesThroughRegisterBytes
    // _AndEmitsFrames), which exercises the full RegisterSource → RegisterBytes
    // path that the SQL surface will eventually use.

    // ─────────────────────── Helpers ───────────────────────

    private async Task<List<(int FrameIndex, uint VideoId, int HandleFrameIndex)>> CollectAsync(ValueRef[] args)
    {
        using ExecutionContext context = CreateExecutionContext();
        List<(int, uint, int)> rows = [];
        await foreach (RowBatch batch in _function.ExecuteAsync(args, context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                int frameIndex = row["frame_index"].AsInt32();
                (uint videoId, int handleFrameIndex) = row["frame"].AsVideoFrame();
                rows.Add((frameIndex, videoId, handleFrameIndex));
            }
        }
        return rows;
    }
}
