using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PcTransformFunction"/> and
/// <see cref="PoseTranslateFunction"/> — the affine transform applied
/// per-fold-step to lay out per-frame clouds in a shared world frame.
/// </summary>
public sealed class PcTransformFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task TranslatePose_ShiftsAllPointsByOffset()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(1, 2, 3), (byte)10, (byte)20, (byte)30),
            (new Vector3(4, 5, 6), (byte)40, (byte)50, (byte)60),
        });
        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(10f), ValueRef.FromFloat32(20f), ValueRef.FromFloat32(30f) },
            CreateEvaluationFrame(), default);

        ValueRef result = await new PcTransformFunction().ExecuteAsync(
            new[] { pc, pose }, CreateEvaluationFrame(), default);

        // Each point should be shifted by (10, 20, 30).
        ReadPoints(result, out Vector3[] positions, out _);
        Assert.Equal(new Vector3(11, 22, 33), positions[0]);
        Assert.Equal(new Vector3(14, 25, 36), positions[1]);
    }

    [Fact]
    public async Task TransformPreservesColorBytes()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(0, 0, 0), (byte)200, (byte)100, (byte)50),
        });
        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(5f), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(0f) },
            CreateEvaluationFrame(), default);

        ValueRef result = await new PcTransformFunction().ExecuteAsync(
            new[] { pc, pose }, CreateEvaluationFrame(), default);

        ReadPoints(result, out _, out (byte r, byte g, byte b, byte a)[] colors);
        Assert.Equal((200, 100, 50, 255), colors[0]);
    }

    [Fact]
    public async Task TransformRecomputesBbox()
    {
        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(-1, -1, -1), (byte)0, (byte)0, (byte)0),
            (new Vector3(1, 1, 1), (byte)0, (byte)0, (byte)0),
        });
        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(100f), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(0f) },
            CreateEvaluationFrame(), default);

        ValueRef result = await new PcTransformFunction().ExecuteAsync(
            new[] { pc, pose }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(new Vector3(99, -1, -1), header.BboxMin);
        Assert.Equal(new Vector3(101, 1, 1), header.BboxMax);
    }

    [Fact]
    public async Task NonTranslationMatrix_AppliesRotation()
    {
        // 90° rotation around the Z axis: (x, y, z) → (-y, x, z).
        // Row-major matrix: [0 -1 0 0, 1 0 0 0, 0 0 1 0, 0 0 0 1].
        float[] rotZ90 =
        [
            0, -1, 0, 0,
            1,  0, 0, 0,
            0,  0, 1, 0,
            0,  0, 0, 1,
        ];

        ValueRef pc = BuildColoredCloud(new[]
        {
            (new Vector3(1, 0, 5), (byte)0, (byte)0, (byte)0),
        });
        ValueRef pose = ValueRef.FromPrimitiveArray(rotZ90, DataKind.Float32);

        ValueRef result = await new PcTransformFunction().ExecuteAsync(
            new[] { pc, pose }, CreateEvaluationFrame(), default);

        ReadPoints(result, out Vector3[] positions, out _);
        Assert.Equal(0f, positions[0].X, precision: 5);
        Assert.Equal(1f, positions[0].Y, precision: 5);
        Assert.Equal(5f, positions[0].Z, precision: 5);
    }

    [Fact]
    public async Task EmptyCloud_PassesThrough()
    {
        ValueRef empty = await new PcEmptyFunction().ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty, CreateEvaluationFrame(), default);
        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(5f), ValueRef.FromFloat32(5f), ValueRef.FromFloat32(5f) },
            CreateEvaluationFrame(), default);

        ValueRef result = await new PcTransformFunction().ExecuteAsync(
            new[] { empty, pose }, CreateEvaluationFrame(), default);

        PointCloudHeader header = PointCloudHeader.Read(result.AsPointCloud());
        Assert.Equal(0u, header.PointCount);
    }

    [Fact]
    public async Task WrongMatrixLength_Throws()
    {
        ValueRef pc = BuildColoredCloud(new[] { (new Vector3(0, 0, 0), (byte)0, (byte)0, (byte)0) });
        ValueRef wrongPose = ValueRef.FromPrimitiveArray(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PcTransformFunction().ExecuteAsync(
                new[] { pc, wrongPose }, CreateEvaluationFrame(), default));
        Assert.Contains("16", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef pose = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(1f), ValueRef.FromFloat32(1f), ValueRef.FromFloat32(1f) },
            CreateEvaluationFrame(), default);

        ValueRef result = await new PcTransformFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud), pose }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    [Fact]
    public async Task PoseTranslate_ReturnsIdentityFor000()
    {
        EvaluationFrame frame = CreateEvaluationFrame();
        ValueRef result = await new PoseTranslateFunction().ExecuteAsync(
            new[] { ValueRef.FromFloat32(0f), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(0f) },
            frame, default);

        DataValue dv = result.ToDataValue(frame.Source);
        ReadOnlySpan<float> matrix = dv.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        Assert.Equal(16, matrix.Length);
        // Identity matrix diagonal.
        Assert.Equal(1f, matrix[0]);
        Assert.Equal(1f, matrix[5]);
        Assert.Equal(1f, matrix[10]);
        Assert.Equal(1f, matrix[15]);
        // Translation slots are zero.
        Assert.Equal(0f, matrix[3]);
        Assert.Equal(0f, matrix[7]);
        Assert.Equal(0f, matrix[11]);
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

    private static void ReadPoints(
        ValueRef pcValue,
        out Vector3[] positions,
        out (byte r, byte g, byte b, byte a)[] colors)
    {
        byte[] blob = pcValue.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);
        int n = checked((int)header.PointCount);
        positions = new Vector3[n];
        colors = new (byte, byte, byte, byte)[n];
        ReadOnlySpan<byte> span = blob;
        int stride = header.PointStrideBytes;
        int basis = PointCloudHeader.SizeBytes;
        for (int i = 0; i < n; i++)
        {
            int offset = basis + i * stride;
            positions[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 0, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 8, 4)));
            if (header.HasColor)
            {
                colors[i] = (span[offset + 12], span[offset + 13], span[offset + 14], span[offset + 15]);
            }
        }
    }
}
