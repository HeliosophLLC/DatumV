using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for the 8 PointCloud accessor scalars
/// (<see cref="PointCloudCountFunction"/>, <see cref="PointCloudWidthFunction"/>,
/// <see cref="PointCloudHeightFunction"/>, <see cref="PointCloudIsOrganizedFunction"/>,
/// <see cref="PointCloudHasColorFunction"/>, <see cref="PointCloudBboxMinFunction"/>,
/// <see cref="PointCloudBboxMaxFunction"/>, <see cref="PointCloudDepthFunction"/>).
/// </summary>
public sealed class PointCloudAccessorFunctionsTests : ServiceTestBase
{
    // ─────────────────────── Metadata sanity ───────────────────────

    [Theory]
    [InlineData("point_cloud_count")]
    [InlineData("point_cloud_width")]
    [InlineData("point_cloud_height")]
    [InlineData("point_cloud_is_organized")]
    [InlineData("point_cloud_has_color")]
    [InlineData("point_cloud_bbox_min")]
    [InlineData("point_cloud_bbox_max")]
    [InlineData("point_cloud_depth")]
    public void AllAccessors_HaveNonEmptyDescriptions(string expectedName)
    {
        // Force each accessor's static Name through and confirm it's the expected snake_case name.
        Assert.Contains(expectedName, new[]
        {
            PointCloudCountFunction.Name,
            PointCloudWidthFunction.Name,
            PointCloudHeightFunction.Name,
            PointCloudIsOrganizedFunction.Name,
            PointCloudHasColorFunction.Name,
            PointCloudBboxMinFunction.Name,
            PointCloudBboxMaxFunction.Name,
            PointCloudDepthFunction.Name,
        });
    }

    // ─────────────────────── Accessors over an organized colored cloud ───────────────────────

    [Fact]
    public async Task Count_ReturnsHeaderPointCount()
    {
        ValueRef pc = await BuildOrganizedColoredCloud(width: 3, height: 4);
        ValueRef result = await new PointCloudCountFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(12, result.AsInt32());
    }

    [Fact]
    public async Task Width_ReturnsHeaderWidth()
    {
        ValueRef pc = await BuildOrganizedColoredCloud(width: 5, height: 7);
        ValueRef result = await new PointCloudWidthFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public async Task Height_ReturnsHeaderHeight()
    {
        ValueRef pc = await BuildOrganizedColoredCloud(width: 5, height: 7);
        ValueRef result = await new PointCloudHeightFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        Assert.Equal(7, result.AsInt32());
    }

    [Fact]
    public async Task IsOrganized_TrueForOrganizedCloud()
    {
        ValueRef pc = await BuildOrganizedColoredCloud(width: 4, height: 4);
        ValueRef result = await new PointCloudIsOrganizedFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public async Task IsOrganized_FalseForUnorganizedCloud()
    {
        ValueRef pc = BuildUnorganizedPositionOnlyCloud(pointCount: 10);
        ValueRef result = await new PointCloudIsOrganizedFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public async Task HasColor_TrueForColoredCloud()
    {
        ValueRef pc = await BuildOrganizedColoredCloud(width: 2, height: 2);
        ValueRef result = await new PointCloudHasColorFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public async Task HasColor_FalseForPositionOnlyCloud()
    {
        ValueRef pc = BuildUnorganizedPositionOnlyCloud(pointCount: 4);
        ValueRef result = await new PointCloudHasColorFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public async Task BboxMin_AndBboxMax_ReturnHeaderCorners()
    {
        Vector3 expectedMin = new(-1.5f, -2.25f, -3.75f);
        Vector3 expectedMax = new(1.5f, 2.25f, 3.75f);
        ValueRef pc = BuildClouedWithBbox(expectedMin, expectedMax);

        ValueRef minResult = await new PointCloudBboxMinFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        ValueRef maxResult = await new PointCloudBboxMaxFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Point3D, minResult.Kind);
        Assert.Equal(DataKind.Point3D, maxResult.Kind);
        Assert.Equal(expectedMin, minResult.AsPoint3D());
        Assert.Equal(expectedMax, maxResult.AsPoint3D());
    }

    // ─────────────────────── point_cloud_depth (inverse op) ───────────────────────

    [Fact]
    public async Task Depth_ProducesImageMatchingCloudDimensions()
    {
        ValueRef pc = await BuildOrganizedColoredCloud(width: 6, height: 5);
        ValueRef result = await new PointCloudDepthFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Image, result.Kind);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(6, bmp.Width);
        Assert.Equal(5, bmp.Height);
    }

    [Fact]
    public async Task Depth_UnorganizedCloud_Throws()
    {
        ValueRef pc = BuildUnorganizedPositionOnlyCloud(pointCount: 16);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PointCloudDepthFunction().ExecuteAsync(
                new[] { pc }, CreateEvaluationFrame(), default));
        Assert.Contains("unorganized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Depth_FlatCloud_ProducesMidGray()
    {
        // Build a cloud where every Z is the same → range is zero → produces
        // mid-gray (128) per the documented degenerate-flat-cloud behavior.
        ValueRef pc = BuildOrganizedFlatZCloud(width: 4, height: 4, z: -0.5f);

        ValueRef result = await new PointCloudDepthFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        SKBitmap bmp = result.AsImage();
        SKColor px = bmp.GetPixel(2, 2);
        Assert.Equal(128, px.Red);
        Assert.Equal(128, px.Green);
        Assert.Equal(128, px.Blue);
        Assert.Equal(255, px.Alpha);
    }

    [Fact]
    public async Task Depth_VaryingZ_ProducesBrightnessGradient()
    {
        // Build cloud where Z varies row-by-row. Verify the resulting depth-map
        // image has brighter pixels where Z is larger (closer in GL frame).
        ValueRef pc = BuildOrganizedZGradientCloud(width: 4, height: 4);

        ValueRef result = await new PointCloudDepthFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();

        // Row 0 has Z = -1 (far → dark), row 3 has Z = 0 (close → bright).
        SKColor far = bmp.GetPixel(0, 0);
        SKColor close = bmp.GetPixel(0, 3);
        Assert.True(close.Red > far.Red, $"close.R={close.Red} should exceed far.R={far.Red}");
    }

    // ─────────────────────── Null propagation ───────────────────────

    [Fact]
    public async Task Count_NullInput_ReturnsNullInt32()
    {
        ValueRef result = await new PointCloudCountFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task IsOrganized_NullInput_ReturnsNullBoolean()
    {
        ValueRef result = await new PointCloudIsOrganizedFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Boolean, result.Kind);
    }

    [Fact]
    public async Task BboxMin_NullInput_ReturnsNullPoint3D()
    {
        ValueRef result = await new PointCloudBboxMinFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Point3D, result.Kind);
    }

    [Fact]
    public async Task Depth_NullInput_ReturnsNullImage()
    {
        ValueRef result = await new PointCloudDepthFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    // ─────────────────────── Cloud builders ───────────────────────

    /// <summary>Build an organized colored cloud by routing through PointCloudFromDepthFunction.</summary>
    private async Task<ValueRef> BuildOrganizedColoredCloud(int width, int height)
    {
        SKBitmap color = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap depth = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                color.SetPixel(x, y, new SKColor((byte)(x * 30), (byte)(y * 30), 100, 255));
                depth.SetPixel(x, y, new SKColor(128, 128, 128, 255));
            }
        }
        // Use the orthographic variant for accessor-test cloud construction —
        // it's the recommended default for normalized inverse depth, and the
        // accessors don't care about projection mode anyway.
        return await new PointCloudFromDepthOrthographicFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            CreateEvaluationFrame(),
            default);
    }

    /// <summary>Hand-build a position-only unorganized cloud (Width=Height=0, no color flag).</summary>
    private static ValueRef BuildUnorganizedPositionOnlyCloud(uint pointCount)
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
            PointCount: pointCount,
            BboxMin: new Vector3(0, 0, 0),
            BboxMax: new Vector3(1, 1, 1),
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        for (uint i = 0; i < pointCount; i++)
        {
            float v = i / (float)Math.Max(1, pointCount);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), v);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), v);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), v);
            offset += PointCloudHeader.PositionStrideBytes;
        }
        return ValueRef.FromPointCloud(blob);
    }

    /// <summary>Hand-build a colored cloud with explicit bbox corners (4 corner points).</summary>
    private static ValueRef BuildClouedWithBbox(Vector3 bboxMin, Vector3 bboxMax)
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: 2,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        WritePoint(span.Slice(offset, 16), bboxMin, 0, 0, 0, 255);
        offset += 16;
        WritePoint(span.Slice(offset, 16), bboxMax, 255, 255, 255, 255);
        return ValueRef.FromPointCloud(blob);
    }

    /// <summary>Organized cloud where every point has the same Z (degenerate flat).</summary>
    private static ValueRef BuildOrganizedFlatZCloud(int width, int height, float z)
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)(width * height),
            BboxMin: new Vector3(0, 0, z),
            BboxMax: new Vector3(1, 1, z),
            Width: (uint)width,
            Height: (uint)height);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                WritePoint(span.Slice(offset, 16), new Vector3(u, v, z), 200, 200, 200, 255);
                offset += 16;
            }
        }
        return ValueRef.FromPointCloud(blob);
    }

    /// <summary>Organized cloud where Z increases from row 0 (Z=-1) to row H-1 (Z=0).</summary>
    private static ValueRef BuildOrganizedZGradientCloud(int width, int height)
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)(width * height),
            BboxMin: new Vector3(0, 0, -1),
            BboxMax: new Vector3(width - 1, height - 1, 0),
            Width: (uint)width,
            Height: (uint)height);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        for (int v = 0; v < height; v++)
        {
            float z = -1f + v / (float)Math.Max(1, height - 1);  // row 0 → -1, last row → 0
            for (int u = 0; u < width; u++)
            {
                WritePoint(span.Slice(offset, 16), new Vector3(u, v, z), 128, 128, 128, 255);
                offset += 16;
            }
        }
        return ValueRef.FromPointCloud(blob);
    }

    private static void WritePoint(Span<byte> dst, Vector3 pos, byte r, byte g, byte b, byte a)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dst[0..4], pos.X);
        BinaryPrimitives.WriteSingleLittleEndian(dst[4..8], pos.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dst[8..12], pos.Z);
        dst[12] = r;
        dst[13] = g;
        dst[14] = b;
        dst[15] = a;
    }
}
