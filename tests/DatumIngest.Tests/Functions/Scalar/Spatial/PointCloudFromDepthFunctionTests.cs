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
/// Tests for the two depth-unprojection scalars,
/// <see cref="PointCloudFromDepthPinholeFunction"/> and
/// <see cref="PointCloudFromDepthOrthographicFunction"/>. Most assertions
/// run against both variants via <see cref="ProjectionVariants"/>; the
/// projection-specific geometry checks (close-pixel-clusters-near-center
/// for pinhole vs preserves-pixel-position for orthographic) are their
/// own facts.
/// </summary>
public sealed class PointCloudFromDepthFunctionTests : ServiceTestBase
{
    // ─────────────────────── Metadata ───────────────────────

    [Fact]
    public void PinholeMetadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("point_cloud_from_depth_pinhole", PointCloudFromDepthPinholeFunction.Name);
        Assert.Equal(FunctionCategory.Image, PointCloudFromDepthPinholeFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(PointCloudFromDepthPinholeFunction.Description));
    }

    [Fact]
    public void OrthographicMetadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("point_cloud_from_depth_orthographic", PointCloudFromDepthOrthographicFunction.Name);
        Assert.Equal(FunctionCategory.Image, PointCloudFromDepthOrthographicFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(PointCloudFromDepthOrthographicFunction.Description));
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public void Validate_AcceptsImageImageFloat32_ReturnsPointCloud(IScalarFunction fn)
    {
        DataKind kind = fn.ValidateArguments([DataKind.Image, DataKind.Image, DataKind.Float32]);
        Assert.Equal(DataKind.PointCloud, kind);
    }

    // ─────────────────────── Null propagation ───────────────────────

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_NullColor_ReturnsNullPointCloud(IScalarFunction fn)
    {
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.PointCloud, result.Kind);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_NullDepth_ReturnsNullPointCloud(IScalarFunction fn)
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.Null(DataKind.Image), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_AcceptsIntegerFov_WidensToFloat32(IScalarFunction fn)
    {
        // fov_deg matcher is NumericScalar — passing an Int32 should work
        // without an explicit cast on the SQL side.
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(0, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromInt32(60) },
            MakeFrame(),
            default);

        Assert.Equal(DataKind.PointCloud, result.Kind);
        Assert.False(result.IsNull);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_NullFov_ReturnsNullPointCloud(IScalarFunction fn)
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.Null(DataKind.Float32) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
    }

    // ─────────────────────── Error paths ───────────────────────

    [Theory]
    [MemberData(nameof(InvalidFovVariants))]
    public async Task Execute_InvalidFov_Throws(IScalarFunction fn, float fov)
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await fn.ExecuteAsync(
                new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(fov) },
                MakeFrame(),
                default));
        Assert.Contains("fov_deg", ex.Message);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_DimensionMismatch_Throws(IScalarFunction fn)
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(255, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(8, 8, intensity: 128);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await fn.ExecuteAsync(
                new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
                MakeFrame(),
                default));
        Assert.Contains("dimensions must match", ex.Message);
    }

    // ─────────────────────── Header shape (shared geometry) ───────────────────────

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_FlatDepth_ProducesOrganizedColoredCloudAtConstantZ(IScalarFunction fn)
    {
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(10, 20, 30, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await fn.ExecuteAsync(
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

        // Sanity-check a sample point: forward Z is negative, color matches input.
        (Vector3 pos, byte r, byte g, byte b, byte a) = ReadPoint(blob, header, pointIndex: 5);
        Assert.True(pos.Z < 0f, $"Expected forward (negative) Z, got {pos.Z}");
        Assert.Equal(10, r);
        Assert.Equal(20, g);
        Assert.Equal(30, b);
        Assert.Equal(255, a);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_DepthRamp_ProducesIncreasingForwardDistance(IScalarFunction fn)
    {
        using SKBitmap color = MakeSolidColor(8, 8, new SKColor(128, 128, 128, 255));
        using SKBitmap depth = MakeVerticalRampDepth(8, 8);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        // Row 0 = intensity 0 (far) → forward = 1.0 → Z = -1.0
        // Row 7 = intensity 255 (close) → forward = 0.1 → Z = -0.1
        (Vector3 farPos, _, _, _, _) = ReadPoint(blob, header, pointIndex: 4);
        (Vector3 closePos, _, _, _, _) = ReadPoint(blob, header, pointIndex: 7 * 8 + 4);

        Assert.True(farPos.Z < closePos.Z, $"Far Z={farPos.Z} should be less than close Z={closePos.Z}");
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_BboxCoversAllPointsInExpectedRange(IScalarFunction fn)
    {
        using SKBitmap color = MakeSolidColor(8, 8, new SKColor(255, 255, 255, 255));
        using SKBitmap depth = MakeDiagonalRampDepth(8, 8);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        Assert.True(header.BboxMax.X > header.BboxMin.X);
        Assert.True(header.BboxMax.Y > header.BboxMin.Y);
        Assert.True(header.BboxMax.Z > header.BboxMin.Z);
        // Z range fits inside [-1, -0.1] (NEAR=0.1, FAR=1.0).
        Assert.True(header.BboxMin.Z >= -1.001f);
        Assert.True(header.BboxMax.Z <= -0.099f);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_BlobSizeMatchesHeaderTotalSizeBytes(IScalarFunction fn)
    {
        using SKBitmap color = MakeSolidColor(5, 7, new SKColor(0, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(5, 7, intensity: 50);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(45f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        Assert.Equal(header.TotalSizeBytes, blob.LongLength);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_PerPixelColor_PreservedExactly(IScalarFunction fn)
    {
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

        ValueRef result = await fn.ExecuteAsync(
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

    // ─────────────────────── Projection-specific geometry ───────────────────────

    /// <summary>
    /// Pinhole: a closest-intensity pixel at the bottom-right corner ends up
    /// near the optical axis because its (X, Y) scales with forward distance
    /// (X = (u-cx) * NEAR / focal at intensity=255). The angular position is
    /// preserved relative to the camera, but the close-slice of the visible
    /// frustum is small, so corner pixels sit close to the origin in absolute
    /// terms. This is the perspective effect that motivated splitting off
    /// the orthographic variant.
    /// </summary>
    [Fact]
    public async Task Pinhole_BrightestPixel_ScalesXYWithDepth()
    {
        const int w = 100;
        const int h = 100;
        using SKBitmap color = MakeSolidColor(w, h, new SKColor(255, 255, 255, 255));
        using SKBitmap depth = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        // All far (intensity 0) except the bottom-right pixel which is close (255).
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                depth.SetPixel(x, y, new SKColor(0, 0, 0, 255));
            }
        }
        depth.SetPixel(w - 1, h - 1, new SKColor(255, 255, 255, 255));

        ValueRef result = await new PointCloudFromDepthPinholeFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        // Brightest pixel at (w-1, h-1): with NEAR=0.1, FAR=1.0, intensity=255 →
        // forward=0.1, so X = (w-1+0.5 - w/2) * 0.1 / focal ≈ small.
        // Far pixel at (w-1, h-1) - 1 row up has forward=1.0, X is ~10× larger.
        (Vector3 brightPos, _, _, _, _) = ReadPoint(blob, header, pointIndex: (h - 1) * w + (w - 1));
        (Vector3 farPos, _, _, _, _) = ReadPoint(blob, header, pointIndex: (h - 1) * w + (w - 2));

        Assert.True(MathF.Abs(brightPos.X) < MathF.Abs(farPos.X),
            $"Pinhole: bright corner X={brightPos.X} should have smaller magnitude than adjacent far X={farPos.X}");
    }

    /// <summary>
    /// Orthographic: every pixel's (X, Y) is fixed by its (u, v) coordinate,
    /// regardless of depth. A bright corner pixel should sit at the same
    /// (X, Y) as a far corner pixel — only Z differs.
    /// </summary>
    [Fact]
    public async Task Orthographic_PixelPosition_IndependentOfDepth()
    {
        const int w = 100;
        const int h = 100;
        using SKBitmap color = MakeSolidColor(w, h, new SKColor(255, 255, 255, 255));
        using SKBitmap depth = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Bottom row is close (255), top row is far (0), middle gradient.
                byte intensity = (byte)(y * 255 / (h - 1));
                depth.SetPixel(x, y, new SKColor(intensity, intensity, intensity, 255));
            }
        }

        ValueRef result = await new PointCloudFromDepthOrthographicFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        // Two pixels at the same column but different rows have different Z
        // (because depth varies by row) but identical X — orthographic
        // preserves pixel position independent of depth.
        (Vector3 topPos, _, _, _, _) = ReadPoint(blob, header, pointIndex: 0 * w + 50);            // row 0, col 50 (far)
        (Vector3 midPos, _, _, _, _) = ReadPoint(blob, header, pointIndex: 50 * w + 50);           // row 50, col 50 (mid)
        (Vector3 botPos, _, _, _, _) = ReadPoint(blob, header, pointIndex: (h - 1) * w + 50);      // row h-1, col 50 (close)

        Assert.Equal(topPos.X, midPos.X, precision: 5);
        Assert.Equal(topPos.X, botPos.X, precision: 5);

        // Y differs by row (row 0 is +Y, row h-1 is -Y in the GL frame) but
        // doesn't compress with depth the way pinhole would. Far and near
        // pixels at the same column hit the same X.
        Assert.True(topPos.Y > midPos.Y, $"row 0 Y={topPos.Y} should be greater than row 50 Y={midPos.Y}");
        Assert.True(midPos.Y > botPos.Y, $"row 50 Y={midPos.Y} should be greater than row h-1 Y={botPos.Y}");
    }

    /// <summary>
    /// Cross-check the brightness=255 collapse-to-origin bug that motivated
    /// the NEAR plane fix: even on pinhole (where X/Y scale with depth),
    /// the brightest pixel should not land at exactly (0, 0, 0) — its
    /// angular position is preserved at the small near-plane scale.
    /// </summary>
    [Fact]
    public async Task Pinhole_BrightestCornerPixel_DoesNotCollapseToOrigin()
    {
        using SKBitmap color = MakeSolidColor(64, 64, new SKColor(255, 255, 255, 255));
        using SKBitmap depth = MakeConstantDepth(64, 64, intensity: 255);  // all close

        ValueRef result = await new PointCloudFromDepthPinholeFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            MakeFrame(),
            default);

        byte[] blob = result.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        // Top-left corner pixel — at (0, 0) in image coords, should have
        // negative X and positive Y in GL frame, both non-zero.
        (Vector3 topLeft, _, _, _, _) = ReadPoint(blob, header, pointIndex: 0);
        Assert.True(topLeft.X < 0f, $"top-left X={topLeft.X} should be negative");
        Assert.True(topLeft.Y > 0f, $"top-left Y={topLeft.Y} should be positive");
        Assert.NotEqual(0f, topLeft.X);
        Assert.NotEqual(0f, topLeft.Y);
    }

    // ─────────────────────── Theory data ───────────────────────

    public static IEnumerable<object[]> ProjectionVariants() => new[]
    {
        new object[] { (IScalarFunction)new PointCloudFromDepthPinholeFunction() },
        new object[] { (IScalarFunction)new PointCloudFromDepthOrthographicFunction() },
    };

    public static IEnumerable<object[]> InvalidFovVariants()
    {
        foreach (float fov in new[] { 0f, 180f, -30f, 360f, float.NaN })
        {
            yield return new object[] { (IScalarFunction)new PointCloudFromDepthPinholeFunction(), fov };
            yield return new object[] { (IScalarFunction)new PointCloudFromDepthOrthographicFunction(), fov };
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
