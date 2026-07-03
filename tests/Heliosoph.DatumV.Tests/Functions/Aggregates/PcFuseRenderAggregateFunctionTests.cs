using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="PcFuseRenderAggregateFunction"/> — the progressive
/// fusion renderer behind the "world building out" movie. Geometry constants
/// match <c>PcRenderFunctionTests</c>: 64×64 canvas, fov 90° → focalPx 32,
/// GL point (0, 0, −1) lands at the canvas center.
/// </summary>
public sealed class PcFuseRenderAggregateFunctionTests : ServiceTestBase
{
    private const int Size = 64;

    [Fact]
    public void ValidateArguments_RejectsWrongCount()
    {
        IAggregateFunction f = new PcFuseRenderAggregateFunction();
        Assert.Throws<ArgumentException>(() =>
            f.ValidateArguments(new[] { DataKind.PointCloud, DataKind.Float32 }));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongFirstKind()
    {
        IAggregateFunction f = new PcFuseRenderAggregateFunction();
        Assert.Throws<ArgumentException>(() =>
            f.ValidateArguments(new[]
            {
                DataKind.Image, DataKind.Float32, DataKind.Int32, DataKind.Int32, DataKind.Float32,
            }));
    }

    [Fact]
    public async Task EmptyAggregation_EmitsNullImageArray()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task OneFramePerAccumulatedRow()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        for (int i = 0; i < 3; i++)
        {
            acc.Accumulate(BuildArgs(arena,
                CloudWithPoint(arena, new Vector3(0.1f * i, 0, -1), 255, 0, 0)), frame);
        }

        DataValue result = await acc.ResultAsync(frame);
        byte[][] frames = result.AsImageArray(frame.Target);
        Assert.Equal(3, frames.Length);
        foreach (byte[] png in frames)
        {
            using SKBitmap bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);
            Assert.Equal(Size, bmp.Width);
            Assert.Equal(Size, bmp.Height);
        }
    }

    [Fact]
    public async Task FramesAccrete_LaterFramesContainEarlierPoints()
    {
        // Cloud A projects to the canvas center; cloud B projects right of
        // center. Frame 0 must show A only; frame 1 must show both.
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        acc.Accumulate(BuildArgs(arena,
            CloudWithPoint(arena, new Vector3(0, 0, -1), 255, 0, 0)), frame);
        acc.Accumulate(BuildArgs(arena,
            CloudWithPoint(arena, new Vector3(0.5f, 0, -1), 0, 255, 0)), frame);

        DataValue result = await acc.ResultAsync(frame);
        byte[][] frames = result.AsImageArray(frame.Target);
        Assert.Equal(2, frames.Length);

        using SKBitmap first = SKBitmap.Decode(frames[0]);
        Assert.Equal(new SKColor(255, 0, 0, 255), first.GetPixel(32, 32));
        Assert.Equal(new SKColor(0, 0, 0, 255), first.GetPixel(48, 32));

        using SKBitmap second = SKBitmap.Decode(frames[1]);
        Assert.Equal(new SKColor(255, 0, 0, 255), second.GetPixel(32, 32));
        Assert.Equal(new SKColor(0, 255, 0, 255), second.GetPixel(48, 32));
    }

    [Fact]
    public async Task PerRowPose_MovesTheCamera()
    {
        // Same world point both rows; the second row's camera shifts +x by
        // 0.5, so the point should slide left of center in frame 1.
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        DataValue cloud = CloudWithPoint(arena, new Vector3(0, 0, -1), 255, 0, 0);
        acc.Accumulate(BuildArgs(arena, cloud), frame);

        float[] shifted =
        [
            1, 0, 0, 0.5f,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        acc.Accumulate(BuildArgs(arena, cloud, shifted), frame);

        DataValue result = await acc.ResultAsync(frame);
        byte[][] frames = result.AsImageArray(frame.Target);

        using SKBitmap first = SKBitmap.Decode(frames[0]);
        Assert.Equal(new SKColor(255, 0, 0, 255), first.GetPixel(32, 32));

        // x_cam = −0.5 at forward 1 with focal 32 → u = 32 − 16 − 0.5 = 15.5.
        using SKBitmap second = SKBitmap.Decode(frames[1]);
        Assert.Equal(new SKColor(255, 0, 0, 255), second.GetPixel(16, 32));
        Assert.Equal(new SKColor(0, 0, 0, 255), second.GetPixel(32, 32));
    }

    [Fact]
    public async Task NullCloudRow_EmitsNoFrame()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        acc.Accumulate(BuildArgs(arena, DataValue.Null(DataKind.PointCloud)), frame);
        acc.Accumulate(BuildArgs(arena,
            CloudWithPoint(arena, new Vector3(0, 0, -1), 255, 0, 0)), frame);

        DataValue result = await acc.ResultAsync(frame);
        Assert.Single(result.AsImageArray(frame.Target));
    }

    [Fact]
    public void RenderConstantsMustBeStable()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        DataValue cloud = CloudWithPoint(arena, new Vector3(0, 0, -1), 255, 0, 0);
        acc.Accumulate(BuildArgs(arena, cloud), frame);

        DataValue[] mismatched = BuildArgs(arena, cloud);
        mismatched[2] = DataValue.FromInt32(Size * 2);
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            acc.Accumulate(mismatched, frame));
        Assert.Contains("constant", ex.Message);
    }

    [Fact]
    public async Task Float64ViewPose_RendersFrame()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        DataValue[] args = BuildArgs(arena,
            CloudWithPoint(arena, new Vector3(0, 0, -1), 255, 0, 0));
        double[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        args[1] = DataValue.FromArenaArray<double>(identity, DataKind.Float64, arena);
        acc.Accumulate(args, frame);

        DataValue result = await acc.ResultAsync(frame);
        byte[][] frames = result.AsImageArray(frame.Target);
        Assert.Single(frames);
        using SKBitmap bmp = SKBitmap.Decode(frames[0]);
        Assert.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public void MalformedPose_Throws()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseRenderAggregateFunction().CreateAccumulator();

        DataValue[] args = BuildArgs(arena, CloudWithPoint(arena, new Vector3(0, 0, -1), 255, 0, 0));
        args[1] = DataValue.FromArenaArray<float>(new float[9], DataKind.Float32, arena);
        Assert.Throws<FunctionArgumentException>(() => acc.Accumulate(args, frame));
    }

    [Fact]
    public async Task Merge_IsRefused()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        PcFuseRenderAggregateFunction f = new();
        IAggregateAccumulator a = f.CreateAccumulator();
        IAggregateAccumulator b = f.CreateAccumulator();

        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await a.MergeAsync(b, frame));
    }

    // ─────────── SQL end-to-end ───────────

    /// <summary>
    /// The announcement-script tail: aggregate through parser + planner +
    /// executor with WITHIN GROUP ordering, producing Array&lt;Image&gt;.
    /// </summary>
    [Fact]
    public async Task Sql_WithinGroup_ProducesOneFramePerRow()
    {
        TableCatalog catalog = CreateCatalog("frames",
            columns: ["id"],
            [3],
            [1],
            [2]);

        List<(bool isArray, DataKind kind, int frameCount)> result = await RunAsync(catalog,
            "SELECT pc_fuse_render_agg(pc_empty(), pose_identity(), 64, 64, 90.0) "
            + "WITHIN GROUP (ORDER BY id) AS movie_frames FROM frames",
            (row, arena) =>
            {
                DataValue arr = row["movie_frames"];
                return (arr.IsArray, arr.Kind, arr.AsImageArray(arena).Length);
            });

        (bool isArray, DataKind kind, int frameCount) = Assert.Single(result);
        Assert.True(isArray);
        Assert.Equal(DataKind.Image, kind);
        Assert.Equal(3, frameCount);
    }

    /// <summary>
    /// Composing into <c>frames_to_gif</c> yields a single animated GIF Image
    /// — the exact expression the reconstruction demo leads with.
    /// </summary>
    [Fact]
    public async Task Sql_FramesToGifComposition_YieldsAnimatedGif()
    {
        TableCatalog catalog = CreateCatalog("frames",
            columns: ["id"],
            [1],
            [2],
            [3]);

        List<byte[]> result = await RunAsync(catalog,
            "SELECT frames_to_gif(pc_fuse_render_agg(pc_empty(), pose_identity(), 64, 64, 90.0) "
            + "WITHIN GROUP (ORDER BY id), 12.0) AS movie FROM frames",
            (row, arena) => row["movie"].AsImage(arena, null).ToArray());

        byte[] movie = Assert.Single(result);
        Assert.Equal("GIF89a", Encoding.ASCII.GetString(movie, 0, 6));
    }

    /// <summary>
    /// Float64 view poses must survive plan-time validation too — the
    /// signature accepts the float family, not just Float32.
    /// </summary>
    [Fact]
    public async Task Sql_Float64ViewPose_PlansAndExecutes()
    {
        TableCatalog catalog = CreateCatalog("frames",
            columns: ["id"],
            [1],
            [2]);

        List<int> result = await RunAsync(catalog,
            "SELECT pc_fuse_render_agg(pc_empty(), ["
            + "1.0::Float64, 0.0::Float64, 0.0::Float64, 0.0::Float64, "
            + "0.0::Float64, 1.0::Float64, 0.0::Float64, 0.0::Float64, "
            + "0.0::Float64, 0.0::Float64, 1.0::Float64, 0.0::Float64, "
            + "0.0::Float64, 0.0::Float64, 0.0::Float64, 1.0::Float64"
            + "]::Float64[], 64, 64, 90.0) "
            + "WITHIN GROUP (ORDER BY id) AS movie_frames FROM frames",
            (row, arena) => row["movie_frames"].AsImageArray(arena).Length);

        Assert.Equal(2, Assert.Single(result));
    }

    /// <summary>
    /// Executes <paramref name="sql"/> and projects each row through
    /// <paramref name="project"/> INSIDE the enumeration — arena-backed
    /// values are recycled once the batch enumerator advances, so projections
    /// must materialize everything they need immediately.
    /// </summary>
    private async Task<List<T>> RunAsync<T>(
        TableCatalog catalog, string sql, Func<Row, Arena, T> project)
    {
        StatementPlan plan = catalog.Plan(sql);
        List<T> result = [];
        await foreach (RowBatch batch in ExecutePlanAsync(catalog, plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                result.Add(project(batch[i], batch.Arena));
            }
        }
        return result;
    }

    // ─────────── Builders ───────────

    /// <summary>
    /// Standard argument row: (pc, view_pose, 64, 64, 90°). Pose defaults to
    /// identity; pass <paramref name="pose"/> to move the camera.
    /// </summary>
    private static DataValue[] BuildArgs(IValueStore store, DataValue cloud, float[]? pose = null)
    {
        pose ??=
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        return
        [
            cloud,
            DataValue.FromArenaArray<float>(pose, DataKind.Float32, store),
            DataValue.FromInt32(Size),
            DataValue.FromInt32(Size),
            DataValue.FromFloat32(90f),
        ];
    }

    private static DataValue CloudWithPoint(
        IValueStore store, Vector3 position, byte r, byte g, byte b)
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: 1,
            BboxMin: position,
            BboxMax: position,
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), position.X);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), position.Z);
        span[offset + 12] = r;
        span[offset + 13] = g;
        span[offset + 14] = b;
        span[offset + 15] = 255;
        return DataValue.FromPointCloud(blob, store);
    }
}
