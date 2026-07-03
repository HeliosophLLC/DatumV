using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PcRenderFunction"/> — pinhole point-splat rendering
/// of PointCloud values. Geometry constants: 64×64 canvas with fov 90° gives
/// focalPx = 32, principal point (32, 32); a GL-frame point at (0, 0, −1)
/// projects to the canvas center.
/// </summary>
public sealed class PcRenderFunctionTests : ServiceTestBase
{
    private const int Size = 64;
    private const float Fov = 90f;

    [Fact]
    public async Task NullCloud_ReturnsNullImage()
    {
        ValueRef result = await Render(ValueRef.Null(DataKind.PointCloud), IdentityPose());
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task NullPose_ReturnsNullImage()
    {
        ValueRef cloud = BuildCloud([(new Vector3(0, 0, -1), (byte)255, (byte)0, (byte)0)]);
        ValueRef result = await Render(cloud, ValueRef.NullArray(DataKind.Float32));
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task EmptyCloud_RendersOpaqueBlackBackground()
    {
        ValueRef result = await Render(BuildEmptyCloud(), IdentityPose());
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(Size, bmp.Width);
        Assert.Equal(Size, bmp.Height);
        Assert.Equal(new SKColor(0, 0, 0, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task PointOnOpticalAxis_LandsAtCanvasCenter()
    {
        ValueRef cloud = BuildCloud([(new Vector3(0, 0, -1), (byte)255, (byte)0, (byte)0)]);
        ValueRef result = await Render(cloud, IdentityPose(), pointSize: 4);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(32, 32));
        // Far corner stays background.
        Assert.Equal(new SKColor(0, 0, 0, 255), bmp.GetPixel(2, 2));
    }

    [Fact]
    public async Task OffAxisPoint_ProjectsWithPinholeScaling()
    {
        // x = +0.5 at forward 1 with focal 32 → u = 32 + 0.5·32 − 0.5 = 47.5.
        // +y in GL is up, which is −v on the canvas: y = +0.5 → v = 15.5.
        ValueRef cloud = BuildCloud([(new Vector3(0.5f, 0.5f, -1), (byte)0, (byte)255, (byte)0)]);
        ValueRef result = await Render(cloud, IdentityPose(), pointSize: 4);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(0, 255, 0, 255), bmp.GetPixel(48, 16));
        Assert.Equal(new SKColor(0, 0, 0, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task NearPointOccludesFarPoint()
    {
        ValueRef cloud = BuildCloud(
        [
            (new Vector3(0, 0, -2), (byte)0, (byte)0, (byte)255),   // far blue
            (new Vector3(0, 0, -1), (byte)255, (byte)0, (byte)0),   // near red
        ]);
        ValueRef result = await Render(cloud, IdentityPose(), pointSize: 4);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task PointBehindCamera_IsCulled()
    {
        ValueRef cloud = BuildCloud([(new Vector3(0, 0, 1), (byte)255, (byte)0, (byte)0)]);
        ValueRef result = await Render(cloud, IdentityPose(), pointSize: 8);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(0, 0, 0, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task CameraToWorldPose_IsInverted()
    {
        // Camera translated to x = 1 (camera-to-world pose) looking down −z.
        // A world point at (1, 0, −1) sits on the moved camera's optical axis.
        float[] pose =
        [
            1, 0, 0, 1,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        ValueRef cloud = BuildCloud([(new Vector3(1, 0, -1), (byte)255, (byte)255, (byte)0)]);
        ValueRef result = await Render(
            cloud, ValueRef.FromPrimitiveArray(pose, DataKind.Float32), pointSize: 4);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(255, 255, 0, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task Float64ViewPose_IsNarrowedAndAccepted()
    {
        // Same camera-inversion scenario as the Float32 test, with the pose
        // supplied as Float64[] — what an AVG-smoothed trajectory produces.
        double[] pose =
        [
            1, 0, 0, 1,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        ValueRef cloud = BuildCloud([(new Vector3(1, 0, -1), (byte)255, (byte)255, (byte)0)]);
        ValueRef result = await Render(
            cloud, ValueRef.FromPrimitiveArray(pose, DataKind.Float64), pointSize: 4);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(255, 255, 0, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task OpenCvFrameCloud_IsConvertedBeforeProjection()
    {
        // CV frame: +z forward. (0, 0, 1) CV = (0, 0, −1) GL → canvas center.
        ValueRef cloud = BuildCloud(
            [(new Vector3(0, 0, 1), (byte)0, (byte)255, (byte)255)],
            PointCloudCoordinateFrame.CameraOpenCv);
        ValueRef result = await Render(cloud, IdentityPose(), pointSize: 4);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(0, 255, 255, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task ColorlessCloud_RendersWhite()
    {
        ValueRef cloud = BuildPositionOnlyCloud([new Vector3(0, 0, -1)]);
        ValueRef result = await Render(cloud, IdentityPose(), pointSize: 4);
        using SKBitmap bmp = result.AsImage();
        Assert.Equal(new SKColor(255, 255, 255, 255), bmp.GetPixel(32, 32));
    }

    [Fact]
    public async Task MalformedPose_Throws()
    {
        ValueRef cloud = BuildCloud([(new Vector3(0, 0, -1), (byte)255, (byte)0, (byte)0)]);
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await Render(cloud, ValueRef.FromPrimitiveArray(new float[9], DataKind.Float32)));
    }

    [Fact]
    public async Task NonPositiveDimensions_Throw()
    {
        ValueRef cloud = BuildCloud([(new Vector3(0, 0, -1), (byte)255, (byte)0, (byte)0)]);
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new PcRenderFunction().ExecuteAsync(
                new[]
                {
                    cloud, IdentityPose(),
                    ValueRef.FromInt32(0), ValueRef.FromInt32(Size), ValueRef.FromFloat32(Fov),
                },
                CreateEvaluationFrame(), default));
    }

    [Fact]
    public async Task OutOfRangeFov_Throws()
    {
        ValueRef cloud = BuildCloud([(new Vector3(0, 0, -1), (byte)255, (byte)0, (byte)0)]);
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new PcRenderFunction().ExecuteAsync(
                new[]
                {
                    cloud, IdentityPose(),
                    ValueRef.FromInt32(Size), ValueRef.FromInt32(Size), ValueRef.FromFloat32(180f),
                },
                CreateEvaluationFrame(), default));
    }

    // ─────────── Helpers ───────────

    private async Task<ValueRef> Render(ValueRef cloud, ValueRef pose, int? pointSize = null)
    {
        List<ValueRef> args =
        [
            cloud, pose,
            ValueRef.FromInt32(Size), ValueRef.FromInt32(Size), ValueRef.FromFloat32(Fov),
        ];
        if (pointSize is int s)
        {
            args.Add(ValueRef.FromInt32(s));
        }
        return await new PcRenderFunction().ExecuteAsync(
            args.ToArray(), CreateEvaluationFrame(), default);
    }

    private static ValueRef IdentityPose()
    {
        float[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        return ValueRef.FromPrimitiveArray(identity, DataKind.Float32);
    }

    private static ValueRef BuildEmptyCloud()
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: 0,
            BboxMin: Vector3.Zero,
            BboxMax: Vector3.Zero,
            Width: 0,
            Height: 0);
        byte[] blob = new byte[PointCloudHeader.SizeBytes];
        header.Write(blob);
        return ValueRef.FromPointCloud(blob);
    }

    private static ValueRef BuildCloud(
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

    private static ValueRef BuildPositionOnlyCloud(Vector3[] points)
    {
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);
        foreach (Vector3 p in points)
        {
            bboxMin = Vector3.Min(bboxMin, p);
            bboxMax = Vector3.Max(bboxMax, p);
        }

        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.None,
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
        foreach (Vector3 pos in points)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), pos.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), pos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), pos.Z);
            offset += 12;
        }
        return ValueRef.FromPointCloud(blob);
    }
}
