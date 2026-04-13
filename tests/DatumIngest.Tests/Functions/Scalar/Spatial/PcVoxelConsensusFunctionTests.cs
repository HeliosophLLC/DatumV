using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

public sealed class PcVoxelConsensusFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task SingleVoteCells_AreDroppedWhenMinVotesIs2()
    {
        // 3 cells: one with 3 votes (real surface), two with 1 vote each (ghosts).
        ValueRef pc = BuildColoredCloud(new[]
        {
            // Cell A (around origin): 3 points
            (new Vector3(0.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.2f, 0.2f, 0.2f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.3f, 0.3f, 0.3f), (byte)0, (byte)0, (byte)0),
            // Cell B (ghost): 1 point
            (new Vector3(5.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
            // Cell C (ghost): 1 point
            (new Vector3(0.1f, 5.1f, 0.1f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef result = await new PcVoxelConsensusFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f), ValueRef.FromInt32(2) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(1u, header.PointCount);   // Only cell A survives
    }

    [Fact]
    public async Task MinVotesOne_BehavesLikePlainDownsample()
    {
        // With min_votes=1, every occupied cell survives — same as pc_voxel_downsample.
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
            (new Vector3(2.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.1f, 2.1f, 0.1f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef consensusResult = await new PcVoxelConsensusFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f), ValueRef.FromInt32(1) },
            CreateEvaluationFrame(), default);
        ValueRef downsampleResult = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader hC = PointCloudHeader.Read(consensusResult.AsPointCloud());
        PointCloudHeader hD = PointCloudHeader.Read(downsampleResult.AsPointCloud());
        Assert.Equal(hD.PointCount, hC.PointCount);
        Assert.Equal(3u, hC.PointCount);
    }

    [Fact]
    public async Task HighMinVotes_DropsEverything_WhenNoCellMeetsThreshold()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
            (new Vector3(2.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef result = await new PcVoxelConsensusFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f), ValueRef.FromInt32(5) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task CentroidOfSurvivingCell_IsPositionAverage()
    {
        // Build 5 points clustered in one cell, averaged centroid should be the
        // mean of their positions.
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0.1f, 0.2f, 0.3f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.2f, 0.2f, 0.3f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.3f, 0.2f, 0.3f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.4f, 0.2f, 0.3f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.5f, 0.2f, 0.3f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef result = await new PcVoxelConsensusFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f), ValueRef.FromInt32(3) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(1u, header.PointCount);

        byte[] blob = result.AsPointCloud();
        ReadOnlySpan<byte> span = blob;
        int o = PointCloudHeader.SizeBytes;
        float cx = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o, 4));
        float cy = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 4, 4));
        float cz = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 8, 4));
        Assert.Equal(0.3f, cx, precision: 5);   // mean of 0.1, 0.2, 0.3, 0.4, 0.5
        Assert.Equal(0.2f, cy, precision: 5);
        Assert.Equal(0.3f, cz, precision: 5);
    }

    [Fact]
    public async Task EmptyCloud_ReturnsEmpty()
    {
        ValueRef empty = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        ValueRef result = await new PcVoxelConsensusFunction().ExecuteAsync(
            new[] { empty, ValueRef.FromFloat32(0.02f), ValueRef.FromInt32(2) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task ZeroMinVotes_Throws()
    {
        ValueRef pc = BuildColoredCloud(new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) });

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PcVoxelConsensusFunction().ExecuteAsync(
                new[] { pc, ValueRef.FromFloat32(0.02f), ValueRef.FromInt32(0) },
                CreateEvaluationFrame(), default));
        Assert.Contains("min_votes", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef result = await new PcVoxelConsensusFunction().ExecuteAsync(
            new[] {
                ValueRef.Null(DataKind.PointCloud),
                ValueRef.FromFloat32(0.02f),
                ValueRef.FromInt32(2),
            },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    private static ValueRef BuildColoredCloud((Vector3 pos, byte r, byte g, byte b)[] points)
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
        return ValueRef.FromPointCloud(blob);
    }
}
