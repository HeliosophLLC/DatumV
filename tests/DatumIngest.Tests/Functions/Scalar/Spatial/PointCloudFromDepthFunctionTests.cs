using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PointCloudFromDepthFunction"/> — verifies metadata,
/// argument validation, geometric correctness of the unprojection, color
/// sampling, error paths, and CoordinateFrame tagging.
/// </summary>
public sealed class PointCloudFromDepthFunctionTests : ServiceTestBase
{
    // ─────────────────────── Metadata ───────────────────────

    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("point_cloud_from_depth", PointCloudFromDepthFunction.Name);
        Assert.Equal(FunctionCategory.Image, PointCloudFromDepthFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(PointCloudFromDepthFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsImageImageFloat32_ReturnsPointCloud()
    {
        DataKind kind = new PointCloudFromDepthFunction()
            .ValidateArguments([DataKind.Image, DataKind.Image, DataKind.Float32]);
        Assert.Equal(DataKind.PointCloud, kind);
    }

    // ─────────────────────── Null propagation ───────────────────────

    [Fact]
    public async Task Execute_NullColor_ReturnsNullPointCloud()
    {
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    [Fact]
    public async Task Execute_NullDepth_ReturnsNullPointCloud()
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.Null(DataKind.Image), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Execute_NullFov_ReturnsNullPointCloud()
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.Null(DataKind.Float32) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
    }

    // ─────────────────────── Error paths ───────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(180f)]
    [InlineData(-30f)]
    [InlineData(360f)]
    [InlineData(float.NaN)]
    public async Task Execute_InvalidFov_Throws(float fov)
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PointCloudFromDepthFunction().ExecuteAsync(
                new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(fov) },
                MakeFrame(),
                default));
        Assert.Contains("fov_deg", ex.Message);
    }

    [Fact]
    public async Task Execute_DimensionMismatch_Throws()
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(8, 8, intensity: 128);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new PointCloudFromDepthFunction().ExecuteAsync(
                new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
                MakeFrame(),
                default));
        Assert.Contains("dimensions must match", ex.Message);
    }

    // ─────────────────────── Geometry ───────────────────────

    [Fact]
    public async Task Execute_FlatDepth_ProducesOrganizedColoredCloudAtConstantZ()
    {
        // intensity=128 → forward = 1 - 128/255 ≈ 0.498; all points share that Z (post-flip: negative).
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(10, 20, 30, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        Assert.Equal(PointCloudFlags.HasColor, header.Flags);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, header.CoordinateFrame);
        Assert.True(header.IsOrganized);
        Assert.Equal(4u, header.Width);
        Assert.Equal(4u, header.Height);
        Assert.Equal(16u, header.PointCount);

        // All Z values agree; bbox Z is degenerate (min == max).
        Assert.Equal(header.BboxMin.Z, header.BboxMax.Z, precision: 5);

        // Sanity-check a sample point: position-Z negative (forward), color matches input.
        (Vector3 pos, byte r, byte g, byte b, byte a) = ReadPoint(blob, header, pointIndex: 5);
        Assert.True(pos.Z < 0f, $"Expected forward (negative) Z, got {pos.Z}");
        Assert.Equal(10, r);
        Assert.Equal(20, g);
        Assert.Equal(30, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public async Task Execute_DepthRamp_ProducesIncreasingForwardDistance()
    {
        // Vertical depth ramp: row 0 = intensity 0 (far), row H-1 = intensity 255 (close).
        // Verify points get progressively closer (less negative Z) as v increases.
        using SKBitmap color = MakeSolidColor(8, 8, new SKColor(128, 128, 128, 255));
        using SKBitmap depth = MakeVerticalRampDepth(8, 8);

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        // Sample one point from row 0 (far) and one from row H-1 (close).
        // Far point has forward ≈ 1, so Z = -1 (in GL frame).
        // Close point has forward ≈ 0, so Z ≈ 0.
        (Vector3 farPos, _, _, _, _) = ReadPoint(blob, header, pointIndex: 4);                // row 0, col 4
        (Vector3 closePos, _, _, _, _) = ReadPoint(blob, header, pointIndex: 7 * 8 + 4);      // row 7, col 4

        Assert.True(farPos.Z < closePos.Z, $"Far Z={farPos.Z} should be less than close Z={closePos.Z}");
        Assert.True(farPos.Z < -0.5f, $"Far Z={farPos.Z} should be near -1");
        Assert.True(closePos.Z > -0.5f, $"Close Z={closePos.Z} should be near 0");
    }

    [Fact]
    public async Task Execute_BboxCoversAllPointsInExpectedRange()
    {
        // Diagonal depth ramp: each pixel's intensity = (u + v). Verify bbox is
        // sensible — non-degenerate X/Y range, Z spans roughly [-1, 0].
        using SKBitmap color = MakeSolidColor(8, 8, new SKColor(255, 255, 255, 255));
        using SKBitmap depth = MakeDiagonalRampDepth(8, 8);

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        // Bbox dimensions must be non-zero in every axis with this depth pattern.
        Assert.True(header.BboxMax.X > header.BboxMin.X);
        Assert.True(header.BboxMax.Y > header.BboxMin.Y);
        Assert.True(header.BboxMax.Z > header.BboxMin.Z);
        // Z range fits inside [-1, 0] (the normalized inverse-depth output range).
        Assert.True(header.BboxMin.Z >= -1.001f);
        Assert.True(header.BboxMax.Z <= 0.001f);
    }

    [Fact]
    public async Task Execute_BlobSizeMatchesHeaderTotalSizeBytes()
    {
        using SKBitmap color = MakeSolidColor(5, 7, new SKColor(0, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(5, 7, intensity: 50);

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(45f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        Assert.Equal(header.TotalSizeBytes, blob.LongLength);
    }

    [Fact]
    public async Task Execute_PerPixelColor_PreservedExactly()
    {
        // Build a color image where each pixel has a unique R = (u + v*W) byte,
        // G = constant, B = constant. Verify the cloud's per-point RGBA matches
        // the source pixel exactly (no premultiplied-alpha mangling, no swizzle).
        const int w = 4;
        const int h = 4;
        SKBitmap color = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int v = 0; v < h; v++)
        {
            for (int u = 0; u < w; u++)
            {
                color.SetPixel(u, v, new SKColor((byte)(u + v * w), 99, 200, 255));
            }
        }
        using SKBitmap depth = MakeConstantDepth(w, h, intensity: 128);

        ValueRef result = await new PointCloudFromDepthFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);
        color.Dispose();

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        for (int i = 0; i < w * h; i++)
        {
            (_, byte r, byte g, byte b, byte a) = ReadPoint(blob, header, pointIndex: i);
            Assert.Equal((byte)i, r);
            Assert.Equal(99, g);
            Assert.Equal(200, b);
            Assert.Equal(255, a);
        }
    }

    // ─────────────────────── Helpers ───────────────────────

    private static SKBitmap MakeSolidColor(int width, int height, SKColor color)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, color);
            }
        }
        return bmp;
    }

    private static SKBitmap MakeConstantDepth(int width, int height, byte intensity)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKColor px = new(intensity, intensity, intensity, 255);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, px);
            }
        }
        return bmp;
    }

    private static SKBitmap MakeVerticalRampDepth(int width, int height)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            byte v = (byte)(y * 255 / Math.Max(1, height - 1));
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }
        return bmp;
    }

    private static SKBitmap MakeDiagonalRampDepth(int width, int height)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        int maxSum = (width - 1) + (height - 1);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = (byte)((x + y) * 255 / Math.Max(1, maxSum));
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }
        return bmp;
    }

    private static (Vector3 pos, byte r, byte g, byte b, byte a) ReadPoint(
        byte[] blob, PointCloudHeader header, int pointIndex)
    {
        int offset = PointCloudHeader.SizeBytes + pointIndex * header.PointStrideBytes;
        ReadOnlySpan<byte> span = blob;
        float x = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 0, 4));
        float y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4));
        float z = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 8, 4));
        byte r = span[offset + 12];
        byte g = span[offset + 13];
        byte b = span[offset + 14];
        byte a = span[offset + 15];
        return (new Vector3(x, y, z), r, g, b, a);
    }

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }
}
