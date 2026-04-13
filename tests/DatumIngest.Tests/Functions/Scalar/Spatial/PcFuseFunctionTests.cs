using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PcFuseFunction"/> — concatenation fold for
/// PointCloud SCAN accumulators.
/// </summary>
public sealed class PcFuseFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task FuseTwoColoredClouds_SumsPointCounts()
    {
        ValueRef a = BuildColoredCloud(
            points: new[]
            {
                (new Vector3(0, 0, 0), (byte)255, (byte)0, (byte)0),
                (new Vector3(1, 0, 0), (byte)0, (byte)255, (byte)0),
            });
        ValueRef b = BuildColoredCloud(
            points: new[]
            {
                (new Vector3(0, 1, 0), (byte)0, (byte)0, (byte)255),
            });

        ValueRef fused = await new PcFuseFunction().ExecuteAsync(
            new[] { a, b }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(fused.AsPointCloud());
        Assert.Equal(3u, header.PointCount);
        Assert.True(header.HasColor);
        Assert.False(header.IsOrganized);   // fused clouds are always unorganized
        Assert.Equal(0u, header.Width);
        Assert.Equal(0u, header.Height);
    }

    [Fact]
    public async Task FuseTwoColoredClouds_UnionsBboxes()
    {
        ValueRef a = BuildColoredCloud(new[] { (new Vector3(-5, -5, -5), (byte)0, (byte)0, (byte)0) });
        ValueRef b = BuildColoredCloud(new[] { (new Vector3(3, 7, 2), (byte)255, (byte)255, (byte)255) });

        ValueRef fused = await new PcFuseFunction().ExecuteAsync(
            new[] { a, b }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(fused.AsPointCloud());
        Assert.Equal(new Vector3(-5, -5, -5), header.BboxMin);
        Assert.Equal(new Vector3(3, 7, 2), header.BboxMax);
    }

    [Fact]
    public async Task FusePreservesPointBytesInOrder()
    {
        // a contributes point at (1,2,3) red; b contributes point at (4,5,6) green.
        // Output stride is 16 (position + color); we expect a's bytes first,
        // then b's.
        ValueRef a = BuildColoredCloud(new[] { (new Vector3(1, 2, 3), (byte)255, (byte)0, (byte)0) });
        ValueRef b = BuildColoredCloud(new[] { (new Vector3(4, 5, 6), (byte)0, (byte)255, (byte)0) });

        ValueRef fused = await new PcFuseFunction().ExecuteAsync(
            new[] { a, b }, CreateEvaluationFrame(), default);

        byte[] blob = fused.AsPointCloud();
        ReadOnlySpan<byte> span = blob;
        int p0 = PointCloudHeader.SizeBytes;
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p0 + 0, 4)));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p0 + 4, 4)));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p0 + 8, 4)));
        Assert.Equal(255, span[p0 + 12]);

        int p1 = p0 + 16;
        Assert.Equal(4f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p1 + 0, 4)));
        Assert.Equal(5f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p1 + 4, 4)));
        Assert.Equal(6f, BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p1 + 8, 4)));
        Assert.Equal(255, span[p1 + 13]);
    }

    [Fact]
    public async Task FuseEmptyWithNonEmpty_YieldsNonEmpty()
    {
        ValueRef empty = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);
        ValueRef populated = BuildColoredCloud(new[]
        {
            (new Vector3(1, 1, 1), (byte)100, (byte)150, (byte)200),
            (new Vector3(2, 2, 2), (byte)100, (byte)150, (byte)200),
        });

        ValueRef fused = await new PcFuseFunction().ExecuteAsync(
            new[] { empty, populated }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(fused.AsPointCloud());
        Assert.Equal(2u, header.PointCount);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, header.CoordinateFrame);
        Assert.Equal(new Vector3(1, 1, 1), header.BboxMin);
        Assert.Equal(new Vector3(2, 2, 2), header.BboxMax);
    }

    [Fact]
    public async Task FuseTwoEmpty_YieldsEmpty()
    {
        ValueRef empty = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        ValueRef fused = await new PcFuseFunction().ExecuteAsync(
            new[] { empty, empty }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(fused.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task ScanInitSeed_FuseChain_AccumulatesAllPoints()
    {
        // Smoke test for the SCAN INIT pattern: start with pc_empty(), fuse
        // a sequence of clouds, end with the sum of all points. This is the
        // actual shape the COCO proof will use.
        ValueRef accumulator = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);

        for (int i = 0; i < 5; i++)
        {
            ValueRef step = BuildColoredCloud(new[]
            {
                (new Vector3(i, 0, 0), (byte)0, (byte)0, (byte)0),
                (new Vector3(i, 1, 0), (byte)0, (byte)0, (byte)0),
            });
            accumulator = await new PcFuseFunction().ExecuteAsync(
                new[] { accumulator, step }, CreateEvaluationFrame(), default);
        }

        PointCloudHeader header = PointCloudHeader.Read(accumulator.AsPointCloud());
        Assert.Equal(10u, header.PointCount);
    }

    [Fact]
    public async Task ConflictingFrames_Throws()
    {
        // a is OpenGL (default), b is OpenCV — pc_fuse refuses to mix frames
        // because it has no in-engine transform; the caller must align first.
        ValueRef a = BuildColoredCloud(
            new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) },
            frame: PointCloudCoordinateFrame.CameraOpenGl);
        ValueRef b = BuildColoredCloud(
            new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) },
            frame: PointCloudCoordinateFrame.CameraOpenCv);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PcFuseFunction().ExecuteAsync(
                new[] { a, b }, CreateEvaluationFrame(), default));
        Assert.Contains("coordinate frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef a = BuildColoredCloud(new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) });
        ValueRef nullPc = ValueRef.Null(DataKind.PointCloud);

        ValueRef result = await new PcFuseFunction().ExecuteAsync(
            new[] { a, nullPc }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    private static ValueRef BuildColoredCloud(
        (Vector3 pos, byte r, byte g, byte b)[] points,
        PointCloudCoordinateFrame frame = PointCloudCoordinateFrame.CameraOpenGl)
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
            CoordinateFrame: frame,
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
