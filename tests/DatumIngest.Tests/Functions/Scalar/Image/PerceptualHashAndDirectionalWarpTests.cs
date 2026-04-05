using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Phase-5 + addition: <see cref="PerceptualHashFunction"/> and
/// <see cref="DirectionalWarpFunction"/>.
/// </summary>
public sealed class PerceptualHashAndDirectionalWarpTests : ServiceTestBase
{
    // ----- perceptual_hash -----

    [Fact]
    public async Task PerceptualHash_Returns64ElementFloat32Array()
    {
        ValueRef result = await new PerceptualHashFunction().ExecuteAsync(
            new[] { MakeSolid(32, 32, 128, 128, 128) }, MakeFrame(), default);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal(64, result.GetArrayLength());
    }

    [Fact]
    public async Task PerceptualHash_OnlyZerosAndOnes()
    {
        ValueRef result = await new PerceptualHashFunction().ExecuteAsync(
            new[] { MakeHorizontalGradient(64, 64) }, MakeFrame(), default);
        float[] bits = (float[])result.Materialized!;
        foreach (float bit in bits)
        {
            Assert.True(bit == 0f || bit == 1f, $"Hash bit {bit} not in {{0, 1}}.");
        }
    }

    [Fact]
    public async Task PerceptualHash_SolidImage_AllZeros()
    {
        // Equal neighbouring pixels → left > right is always false → all zeros.
        ValueRef result = await new PerceptualHashFunction().ExecuteAsync(
            new[] { MakeSolid(64, 64, 100, 100, 100) }, MakeFrame(), default);
        float[] bits = (float[])result.Materialized!;
        Assert.All(bits, b => Assert.Equal(0f, b));
    }

    [Fact]
    public async Task PerceptualHash_DescendingGradient_AllOnes()
    {
        // Left-bright, right-dark: every adjacent comparison yields left > right.
        SKBitmap bmp = new(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                byte v = (byte)(255 - x * 4);
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        ValueRef result = await new PerceptualHashFunction().ExecuteAsync(
            new[] { ValueRef.FromImage(bmp) }, MakeFrame(), default);
        float[] bits = (float[])result.Materialized!;
        Assert.All(bits, b => Assert.Equal(1f, b));
    }

    [Fact]
    public async Task PerceptualHash_Null_ReturnsNullArray()
    {
        ValueRef result = await new PerceptualHashFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, MakeFrame(), default);
        Assert.True(result.IsArray);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    // ----- directional_warp -----

    [Fact]
    public async Task DirectionalWarp_ZeroIntensity_IsIdentity()
    {
        ValueRef result = await new DirectionalWarpFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(16, 16, 100, 150, 200),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(0),
            },
            MakeFrame(), default);
        SKColor px = result.AsImage().GetPixel(8, 8);
        Assert.Equal(100, px.Red);
        Assert.Equal(150, px.Green);
        Assert.Equal(200, px.Blue);
    }

    [Fact]
    public async Task DirectionalWarp_PreservesDimensions()
    {
        ValueRef result = await new DirectionalWarpFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(28, 28, 50, 50, 50),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(3),
            },
            MakeFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(28, bmp.Width);
        Assert.Equal(28, bmp.Height);
    }

    [Fact]
    public async Task DirectionalWarp_SolidImage_StaysSolid()
    {
        // Bilinear sampling of a solid colour reads the same value regardless
        // of where the resample coordinates land.
        ValueRef result = await new DirectionalWarpFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(28, 28, 80, 120, 200),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(0.3f),
                ValueRef.FromFloat32(4),
            },
            MakeFrame(), default);
        SKColor px = result.AsImage().GetPixel(14, 14);
        Assert.Equal(80, px.Red);
        Assert.Equal(120, px.Green);
        Assert.Equal(200, px.Blue);
    }

    [Fact]
    public async Task DirectionalWarp_HorizontalShear_OnVerticalLine_ShiftsItLeaning()
    {
        // Image: black background, single white vertical line at the centre column.
        const int w = 32, h = 32;
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, x == 16 ? new SKColor(255, 255, 255, 255) : new SKColor(0, 0, 0, 255));

        ValueRef result = await new DirectionalWarpFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromImage(bmp),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(0),  // shear horizontally
                ValueRef.FromFloat32(6),                            // 6-pixel edge displacement
            },
            MakeFrame(), default);
        SKBitmap warped = result.AsImage();

        // After horizontal shear with direction (1, 0): top of line shifts opposite to bottom.
        // The white line should still cross y=center at x=center (centre line doesn't move).
        Assert.True(warped.GetPixel(16, 16).Red > 200,
            $"Centre pixel should remain bright, got {warped.GetPixel(16, 16).Red}.");
        // At y=0 (top edge), the line should have shifted left or right of the original column.
        int topBrightX = FindBrightestColumn(warped, y: 0);
        int bottomBrightX = FindBrightestColumn(warped, y: 31);
        Assert.NotEqual(16, topBrightX);
        Assert.NotEqual(topBrightX, bottomBrightX);
        // Opposite displacement on opposite edges.
        Assert.True((topBrightX - 16) * (bottomBrightX - 16) < 0,
            $"Expected opposite-side displacement; got top={topBrightX}, bottom={bottomBrightX}.");
    }

    [Fact]
    public async Task DirectionalWarp_ZeroDirection_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new DirectionalWarpFunction().ExecuteAsync(
                new[]
                {
                    MakeSolid(8, 8, 0, 0, 0),
                    ValueRef.FromFloat32(0), ValueRef.FromFloat32(0),
                    ValueRef.FromFloat32(1),
                },
                MakeFrame(), default));
        Assert.Contains("zero", ex.Message);
    }

    [Fact]
    public async Task DirectionalWarp_DirectionLengthDoesNotMatter()
    {
        // (1, 0) and (10, 0) should give identical output — direction is normalized.
        ValueRef a = await new DirectionalWarpFunction().ExecuteAsync(
            new[]
            {
                MakeHorizontalGradient(28, 28),
                ValueRef.FromFloat32(1), ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(3),
            },
            MakeFrame(), default);
        ValueRef b = await new DirectionalWarpFunction().ExecuteAsync(
            new[]
            {
                MakeHorizontalGradient(28, 28),
                ValueRef.FromFloat32(10), ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(3),
            },
            MakeFrame(), default);
        SKColor pa = a.AsImage().GetPixel(14, 5);
        SKColor pb = b.AsImage().GetPixel(14, 5);
        Assert.Equal(pa.Red, pb.Red);
        Assert.Equal(pa.Green, pb.Green);
        Assert.Equal(pa.Blue, pb.Blue);
    }

    // ----- helpers -----

    private static ValueRef MakeSolid(int w, int h, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return ValueRef.FromImage(bmp);
    }

    private static ValueRef MakeHorizontalGradient(int w, int h)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)((x * 255) / System.Math.Max(1, w - 1));
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        return ValueRef.FromImage(bmp);
    }

    private static int FindBrightestColumn(SKBitmap bmp, int y)
    {
        int bestX = 0;
        int bestVal = -1;
        for (int x = 0; x < bmp.Width; x++)
        {
            int v = bmp.GetPixel(x, y).Red;
            if (v > bestVal) { bestVal = v; bestX = x; }
        }
        return bestX;
    }

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }
}
