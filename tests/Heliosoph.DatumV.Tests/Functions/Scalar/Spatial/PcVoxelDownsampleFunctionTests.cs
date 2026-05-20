using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

public sealed class PcVoxelDownsampleFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task PointsInSameCell_CollapseToCentroid()
    {
        // Three points all within a 1.0-side cube anchored at origin → one cell → one output point
        // at the centroid of the inputs.
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0.1f, 0.1f, 0.1f), (byte)10, (byte)10, (byte)10),
            (new Vector3(0.2f, 0.2f, 0.2f), (byte)20, (byte)20, (byte)20),
            (new Vector3(0.3f, 0.3f, 0.3f), (byte)30, (byte)30, (byte)30),
        });

        ValueRef result = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f) }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(1u, header.PointCount);

        byte[] blob = result.AsPointCloud();
        ReadOnlySpan<byte> span = blob;
        int o = PointCloudHeader.SizeBytes;
        float cx = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 0, 4));
        float cy = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 4, 4));
        float cz = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 8, 4));
        Assert.Equal(0.2f, cx, precision: 5);
        Assert.Equal(0.2f, cy, precision: 5);
        Assert.Equal(0.2f, cz, precision: 5);
        Assert.Equal(20, blob[o + 12]);   // avg of 10, 20, 30 = 20
    }

    [Fact]
    public async Task PointsInDistinctCells_RemainSeparate()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0.5f, 0.5f, 0.5f), (byte)0, (byte)0, (byte)0),
            (new Vector3(2.5f, 0.5f, 0.5f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.5f, 2.5f, 0.5f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef result = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f) }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(3u, header.PointCount);
    }

    [Fact]
    public async Task Idempotent_TwoApplicationsEqualOne()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
            (new Vector3(0.4f, 0.4f, 0.4f), (byte)0, (byte)0, (byte)0),
            (new Vector3(2.1f, 0.1f, 0.1f), (byte)0, (byte)0, (byte)0),
            (new Vector3(2.4f, 0.4f, 0.4f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef once = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f) }, CreateEvaluationFrame(), default);
        ValueRef twice = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { once, ValueRef.FromFloat32(1.0f) }, CreateEvaluationFrame(), default);

        PointCloudHeader h1 = PointCloudHeader.Read(once.AsPointCloud());
        PointCloudHeader h2 = PointCloudHeader.Read(twice.AsPointCloud());
        Assert.Equal(h1.PointCount, h2.PointCount);
        Assert.Equal(2u, h1.PointCount);
    }

    [Fact]
    public async Task EmptyCloud_ReturnsEmpty()
    {
        ValueRef empty = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        ValueRef result = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { empty, ValueRef.FromFloat32(0.02f) }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task NonPositiveCellSize_Throws()
    {
        ValueRef pc = BuildColoredCloud(new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) });

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PcVoxelDownsampleFunction().ExecuteAsync(
                new[] { pc, ValueRef.FromFloat32(0f) }, CreateEvaluationFrame(), default));
        Assert.Contains("cell_size", ex.Message);
    }

    [Fact]
    public async Task ColorIsPreservedAcrossDownsample()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0.1f, 0.1f, 0.1f), (byte)100, (byte)150, (byte)200),
        });
        ValueRef result = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(1.0f) }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.True(header.HasColor);
        byte[] blob = result.AsPointCloud();
        Assert.Equal(100, blob[PointCloudHeader.SizeBytes + 12]);
        Assert.Equal(150, blob[PointCloudHeader.SizeBytes + 13]);
        Assert.Equal(200, blob[PointCloudHeader.SizeBytes + 14]);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef result = await new PcVoxelDownsampleFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud), ValueRef.FromFloat32(0.02f) },
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
