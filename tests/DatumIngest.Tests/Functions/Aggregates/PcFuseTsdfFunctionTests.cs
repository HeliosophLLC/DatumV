using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="PcFuseTsdfFunction"/> — TSDF fusion aggregate.
/// Verifies the wiring (signature validation, accumulator lifecycle,
/// empty/non-empty result shape) and a small end-to-end "single virtual
/// plane → mesh" sanity test. The Marching Cubes math itself is exercised
/// by MeshFromDensityGridFunction's tests; we don't re-verify it here.
/// </summary>
public sealed class PcFuseTsdfFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_RejectsWrongCount()
    {
        IAggregateFunction f = new PcFuseTsdfFunction();
        Assert.Throws<ArgumentException>(() =>
            f.ValidateArguments(new[] { DataKind.Float32, DataKind.Float32 }));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongKind()
    {
        IAggregateFunction f = new PcFuseTsdfFunction();
        Assert.Throws<ArgumentException>(() =>
            f.ValidateArguments(new[]
            {
                DataKind.Int32,                  // depth should be Float32
                DataKind.Float32, DataKind.Float32,
                DataKind.Float32, DataKind.Float32,
            }));
    }

    [Fact]
    public void ValidateArguments_AcceptsAllFloat32()
    {
        IAggregateFunction f = new PcFuseTsdfFunction();
        DataKind result = f.ValidateArguments(new[]
        {
            DataKind.Float32, DataKind.Float32, DataKind.Float32,
            DataKind.Float32, DataKind.Float32,
        });
        Assert.Equal(DataKind.Mesh, result);
    }

    [Fact]
    public async Task EmptyAggregation_EmitsEmptyMesh()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseTsdfFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        MeshHeader header = MeshHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(0u, header.VertexCount);
        Assert.Equal(0u, header.TriangleCount);
        Assert.Equal(DataKind.Mesh, result.Kind);
    }

    [Fact]
    public async Task ConstantsMismatch_ThrowsOnSecondRow()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseTsdfFunction().CreateAccumulator();

        // Tiny synthetic frame — 4x4 depth, identity pose, identity K.
        DataValue depth = MakeDepthArray(arena, 4, 4, 1.0f);
        DataValue pose = MakePose(arena, identity: true);
        DataValue k = MakeIntrinsics(arena, fx: 4f, fy: 4f, cx: 2f, cy: 2f);

        acc.Accumulate(
            new[] { depth, pose, k, DataValue.FromFloat32(0.05f), DataValue.FromFloat32(0.15f) },
            frame);

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            acc.Accumulate(
                new[] { depth, pose, k, DataValue.FromFloat32(0.10f), DataValue.FromFloat32(0.15f) },
                frame));
        Assert.Contains("constant", ex.Message);
    }

    [Fact]
    public async Task SingleFrame_ProducesNonEmptyMesh()
    {
        // Synthetic 8x8 depth image where every pixel is at depth 1.0 (a
        // flat plane facing the camera). With cell_size=0.1 and truncation=0.3,
        // we should observe a band of voxels around the plane and Marching
        // Cubes should extract a surface from it.
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseTsdfFunction().CreateAccumulator();

        DataValue depth = MakeDepthArray(arena, 8, 8, 1.0f);
        DataValue pose = MakePose(arena, identity: true);
        DataValue k = MakeIntrinsics(arena, fx: 8f, fy: 8f, cx: 4f, cy: 4f);

        acc.Accumulate(
            new[] { depth, pose, k, DataValue.FromFloat32(0.1f), DataValue.FromFloat32(0.3f) },
            frame);

        DataValue result = await acc.ResultAsync(frame);
        MeshHeader header = MeshHeader.Read(result.AsByteSpan(frame.Target));
        Assert.True(header.VertexCount > 0, $"expected non-zero vertices, got {header.VertexCount}");
        Assert.True(header.TriangleCount > 0, $"expected non-zero triangles, got {header.TriangleCount}");
    }

    [Fact]
    public async Task MultipleFrames_AccumulateConsistently()
    {
        // Two identical frames should produce a similar-sized mesh as one
        // frame (TSDF averaging means the SDF doesn't drift with repeated
        // identical observations). Sanity check that Accumulate+Result is
        // stable across multiple identical inputs.
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseTsdfFunction().CreateAccumulator();

        DataValue depth = MakeDepthArray(arena, 8, 8, 1.0f);
        DataValue pose = MakePose(arena, identity: true);
        DataValue k = MakeIntrinsics(arena, fx: 8f, fy: 8f, cx: 4f, cy: 4f);
        DataValue cell = DataValue.FromFloat32(0.1f);
        DataValue trunc = DataValue.FromFloat32(0.3f);

        acc.Accumulate(new[] { depth, pose, k, cell, trunc }, frame);
        uint singleFrameVertices;
        {
            // Snapshot the result after one frame.
            DataValue r1 = await acc.ResultAsync(frame);
            singleFrameVertices = MeshHeader.Read(r1.AsByteSpan(frame.Target)).VertexCount;
        }

        // Accumulate the same frame a second time. The voxel grid has the
        // same cells touched again; SDFs should average to the same value.
        // Reset the accumulator to a fresh state to avoid the "ResultAsync
        // mutates state" question.
        acc.Reset();
        acc.Accumulate(new[] { depth, pose, k, cell, trunc }, frame);
        acc.Accumulate(new[] { depth, pose, k, cell, trunc }, frame);
        DataValue r2 = await acc.ResultAsync(frame);
        uint doubleFrameVertices = MeshHeader.Read(r2.AsByteSpan(frame.Target)).VertexCount;

        // Should produce essentially the same mesh — same scene, same
        // observations. Allow a small variance for floating-point order
        // sensitivity in the averaging.
        Assert.InRange((int)doubleFrameVertices,
            (int)(singleFrameVertices * 0.9),
            (int)(singleFrameVertices * 1.1));
    }

    [Fact]
    public async Task TruncationSmallerThanCellSize_Throws()
    {
        Arena arena = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcFuseTsdfFunction().CreateAccumulator();

        DataValue depth = MakeDepthArray(arena, 4, 4, 1.0f);
        DataValue pose = MakePose(arena, identity: true);
        DataValue k = MakeIntrinsics(arena, fx: 4f, fy: 4f, cx: 2f, cy: 2f);

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            acc.Accumulate(
                new[] { depth, pose, k, DataValue.FromFloat32(0.1f), DataValue.FromFloat32(0.05f) },
                frame));
        Assert.Contains("truncation", ex.Message);
        await Task.CompletedTask;
    }

    // ───────────────────── Builders ─────────────────────

    private static DataValue MakeDepthArray(IValueStore store, int h, int w, float fillValue)
    {
        float[] data = new float[h * w];
        Array.Fill(data, fillValue);
        return DataValue.FromArenaMultiDimArray<float>(data, new[] { h, w }, DataKind.Float32, store);
    }

    private static DataValue MakePose(IValueStore store, bool identity)
    {
        float[] pose;
        if (identity)
        {
            pose = new float[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1,
            };
        }
        else
        {
            // Slight translation along Z so the integration produces something
            // observable in tests that don't care about specific positions.
            pose = new float[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0.5f,
                0, 0, 0, 1,
            };
        }
        return DataValue.FromArenaArray<float>(pose, DataKind.Float32, store);
    }

    private static DataValue MakeIntrinsics(IValueStore store, float fx, float fy, float cx, float cy)
    {
        float[] k =
        [
            fx, 0, cx,
            0, fy, cy,
            0, 0, 1,
        ];
        return DataValue.FromArenaArray<float>(k, DataKind.Float32, store);
    }
}
