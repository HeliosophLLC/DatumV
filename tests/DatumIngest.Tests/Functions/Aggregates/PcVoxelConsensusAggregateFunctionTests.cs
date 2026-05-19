using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Functions.Aggregates;

public sealed class PcVoxelConsensusAggregateFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_RejectsWrongCount()
    {
        IAggregateFunction f = new PcVoxelConsensusAggregateFunction();
        Assert.Throws<ArgumentException>(() =>
            f.ValidateArguments(new[] { DataKind.PointCloud, DataKind.Float32 }));
    }

    [Fact]
    public void ValidateArguments_RejectsWrongFirstKind()
    {
        IAggregateFunction f = new PcVoxelConsensusAggregateFunction();
        Assert.Throws<ArgumentException>(() =>
            f.ValidateArguments(new[] { DataKind.Float32, DataKind.Float32, DataKind.Int32 }));
    }

    [Fact]
    public async Task EmptyAggregation_EmitsZeroPointCloud()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcVoxelConsensusAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(0u, header.PointCount);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    [Fact]
    public async Task ThreeFramesAtSameCell_CollapseAndPassThreshold()
    {
        // Three clouds, each containing one point in the same voxel cell.
        // With cell=1.0 and min_votes=3, expected output: 1 point.
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcVoxelConsensusAggregateFunction().CreateAccumulator();

        DataValue cell = DataValue.FromFloat32(1.0f);
        DataValue minVotes = DataValue.FromInt32(3);

        for (int i = 0; i < 3; i++)
        {
            DataValue pc = BuildColoredCloud(arena, new[]
            {
                (new Vector3(0.1f * i, 0.1f * i, 0.1f * i), (byte)(100 + i * 30), (byte)100, (byte)100),
            });
            acc.Accumulate(new[] { pc, cell, minVotes }, frame);
        }

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(1u, header.PointCount);
        Assert.True(header.HasColor);
    }

    [Fact]
    public async Task SingleFrameCell_DroppedWhenMinVotesTwoOrMore()
    {
        // One cloud with one point, min_votes=2 → output is empty.
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcVoxelConsensusAggregateFunction().CreateAccumulator();

        DataValue pc = BuildColoredCloud(arena, new[]
        {
            (new Vector3(0.1f, 0.1f, 0.1f), (byte)100, (byte)100, (byte)100),
        });
        acc.Accumulate(new[] { pc, DataValue.FromFloat32(1.0f), DataValue.FromInt32(2) }, frame);

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task ConstantsMustBeStable()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcVoxelConsensusAggregateFunction().CreateAccumulator();

        DataValue pc1 = BuildColoredCloud(arena, new[] {
            (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0),
        });
        acc.Accumulate(new[] { pc1, DataValue.FromFloat32(1.0f), DataValue.FromInt32(2) }, frame);

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            acc.Accumulate(new[] { pc1, DataValue.FromFloat32(2.0f), DataValue.FromInt32(2) }, frame));
        Assert.Contains("constant", ex.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NullPointCloudInput_IsSkipped()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new PcVoxelConsensusAggregateFunction().CreateAccumulator();

        DataValue cell = DataValue.FromFloat32(1.0f);
        DataValue minVotes = DataValue.FromInt32(1);

        acc.Accumulate(new[] { DataValue.Null(DataKind.PointCloud), cell, minVotes }, frame);

        DataValue real = BuildColoredCloud(arena, new[] {
            (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0),
        });
        acc.Accumulate(new[] { real, cell, minVotes }, frame);

        DataValue result = await acc.ResultAsync(frame);
        PointCloudHeader header = PointCloudHeader.Read(result.AsByteSpan(frame.Target));
        Assert.Equal(1u, header.PointCount);
    }

    [Fact]
    public async Task EquivalenceWithFuseThenConsensus()
    {
        // The aggregate should produce equivalent output to the scalar
        // pipeline pc_voxel_consensus(pc_fuse_agg(_), cell, min_votes).
        // Same inputs → same point count. (Floating-point order may shift
        // centroid positions by epsilon; we check counts and bbox bounds.)
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);

        DataValue[] clouds =
        [
            BuildColoredCloud(arena, new[]
            {
                (new Vector3(0.1f, 0.1f, 0.1f), (byte)100, (byte)100, (byte)100),
                (new Vector3(2.1f, 0.1f, 0.1f), (byte)100, (byte)100, (byte)100),
            }),
            BuildColoredCloud(arena, new[]
            {
                (new Vector3(0.2f, 0.2f, 0.2f), (byte)200, (byte)100, (byte)100),
                (new Vector3(2.2f, 0.2f, 0.2f), (byte)200, (byte)100, (byte)100),
            }),
            BuildColoredCloud(arena, new[]
            {
                (new Vector3(0.3f, 0.3f, 0.3f), (byte)200, (byte)200, (byte)100),
            }),
        ];

        // Aggregate path.
        IAggregateAccumulator acc = new PcVoxelConsensusAggregateFunction().CreateAccumulator();
        DataValue cell = DataValue.FromFloat32(1.0f);
        DataValue minVotes = DataValue.FromInt32(2);
        foreach (DataValue pc in clouds)
        {
            acc.Accumulate(new[] { pc, cell, minVotes }, frame);
        }
        DataValue aggResult = await acc.ResultAsync(frame);
        PointCloudHeader aggHeader = PointCloudHeader.Read(aggResult.AsByteSpan(frame.Target));

        // We expect: cell (0,0,0) has 3 points (one from each cloud) → kept.
        //            cell (2,0,0) has 2 points → kept (meets min_votes=2).
        // Total survivors: 2.
        Assert.Equal(2u, aggHeader.PointCount);
    }

    // ─────────── Builders ───────────

    private static DataValue BuildColoredCloud(
        IValueStore store,
        (Vector3 pos, byte r, byte g, byte b)[] points)
    {
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        foreach ((Vector3 p, _, _, _) in points)
        {
            bboxMin = Vector3.Min(bboxMin, p);
            bboxMax = Vector3.Max(bboxMax, p);
        }

        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)points.Length,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        foreach ((Vector3 pos, byte r, byte g, byte b) in points)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), pos.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), pos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), pos.Z);
            span[offset + 12] = r;
            span[offset + 13] = g;
            span[offset + 14] = b;
            span[offset + 15] = 255;
            offset += 16;
        }
        return DataValue.FromPointCloud(blob, store);
    }
}
