using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

public sealed class PcFilterDepthFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task KeepsPointsInRange_DropsRest()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0, 0, -0.5f), (byte)10, (byte)20, (byte)30),
            (new Vector3(0, 0, -1.5f), (byte)40, (byte)50, (byte)60),
            (new Vector3(0, 0, -5.0f), (byte)70, (byte)80, (byte)90),
        });

        ValueRef result = await new PcFilterDepthFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(-2.0f), ValueRef.FromFloat32(0f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(2u, header.PointCount);
        Assert.False(header.IsOrganized);
        Assert.Equal(0u, header.Width);
    }

    [Fact]
    public async Task BboxIsRecomputedFromSurvivors()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(-100f, -100f, -100f), (byte)0, (byte)0, (byte)0),   // dropped
            (new Vector3(1f, 2f, -0.5f), (byte)0, (byte)0, (byte)0),
            (new Vector3(3f, 4f, -1.0f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef result = await new PcFilterDepthFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(-2f), ValueRef.FromFloat32(0f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(new Vector3(1f, 2f, -1.0f), header.BboxMin);
        Assert.Equal(new Vector3(3f, 4f, -0.5f), header.BboxMax);
    }

    [Fact]
    public async Task AllDropped_ReturnsEmptyCloudWithOriginBbox()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0, 0, -10f), (byte)0, (byte)0, (byte)0),
        });

        ValueRef result = await new PcFilterDepthFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f) },
            CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
        Assert.Equal(Vector3.Zero, header.BboxMin);
        Assert.Equal(Vector3.Zero, header.BboxMax);
    }

    [Fact]
    public async Task ColorBytesPreservedOnSurvivors()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0, 0, -0.5f), (byte)200, (byte)100, (byte)50),
        });

        ValueRef result = await new PcFilterDepthFunction().ExecuteAsync(
            new[] { pc, ValueRef.FromFloat32(-1f), ValueRef.FromFloat32(0f) },
            CreateEvaluationFrame(), default);

        byte[] blob = result.AsPointCloud();
        Assert.Equal(200, blob[PointCloudHeader.SizeBytes + 12]);
        Assert.Equal(100, blob[PointCloudHeader.SizeBytes + 13]);
        Assert.Equal(50, blob[PointCloudHeader.SizeBytes + 14]);
    }

    [Fact]
    public async Task MinAboveMax_Throws()
    {
        ValueRef pc = BuildColoredCloud(new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) });

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PcFilterDepthFunction().ExecuteAsync(
                new[] { pc, ValueRef.FromFloat32(5f), ValueRef.FromFloat32(0f) },
                CreateEvaluationFrame(), default));
        Assert.Contains("min_z", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef result = await new PcFilterDepthFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f) },
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
