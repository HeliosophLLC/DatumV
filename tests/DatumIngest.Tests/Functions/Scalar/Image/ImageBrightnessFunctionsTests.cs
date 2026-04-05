using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for the brightness-summary functions
/// (<see cref="ImageBrightnessMeanFunction"/>,
/// <see cref="ImageBrightnessStdFunction"/>,
/// <see cref="ImageBrightnessHistogramFunction"/>).
/// </summary>
public sealed class ImageBrightnessFunctionsTests : ServiceTestBase
{
    [Fact]
    public async Task BrightnessMean_SolidWhite_Is255()
    {
        ValueRef result = await new ImageBrightnessMeanFunction().ExecuteAsync(
            new[] { MakeSolid(8, 8, 255, 255, 255) }, MakeFrame(), default);
        Assert.Equal(255f, result.AsFloat32(), 2);
    }

    [Fact]
    public async Task BrightnessMean_SolidBlack_Is0()
    {
        ValueRef result = await new ImageBrightnessMeanFunction().ExecuteAsync(
            new[] { MakeSolid(8, 8, 0, 0, 0) }, MakeFrame(), default);
        Assert.Equal(0f, result.AsFloat32(), 2);
    }

    [Fact]
    public async Task BrightnessMean_SolidColor_MatchesBt601()
    {
        // BT.601: 0.299·R + 0.587·G + 0.114·B
        // (100, 150, 200) → 29.9 + 88.05 + 22.8 = 140.75
        ValueRef result = await new ImageBrightnessMeanFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 100, 150, 200) }, MakeFrame(), default);
        Assert.Equal(140.75f, result.AsFloat32(), 1);
    }

    [Fact]
    public async Task BrightnessStd_SolidColor_IsZero()
    {
        ValueRef result = await new ImageBrightnessStdFunction().ExecuteAsync(
            new[] { MakeSolid(8, 8, 128, 128, 128) }, MakeFrame(), default);
        Assert.Equal(0f, result.AsFloat32(), 3);
    }

    [Fact]
    public async Task BrightnessStd_CheckerboardBlackWhite_HasMaximalSpread()
    {
        ValueRef result = await new ImageBrightnessStdFunction().ExecuteAsync(
            new[] { MakeCheckerboard(8, 8) }, MakeFrame(), default);
        // 50/50 black/white luminance → std = 127.5
        Assert.Equal(127.5f, result.AsFloat32(), 1);
    }

    [Fact]
    public async Task BrightnessMean_Null_ReturnsNullFloat32()
    {
        ValueRef result = await new ImageBrightnessMeanFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, MakeFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task BrightnessHistogram_Returns256BinArrayOfFloat32()
    {
        ValueRef result = await new ImageBrightnessHistogramFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 100, 150, 200) }, MakeFrame(), default);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal(256, result.GetArrayLength());
    }

    [Fact]
    public async Task BrightnessHistogram_SolidColor_ConcentratesOneBin()
    {
        // Luminance 140 → bin 140 should hold all 16 pixels.
        ValueRef result = await new ImageBrightnessHistogramFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 100, 150, 200) }, MakeFrame(), default);
        float[] bins = (float[])result.Materialized!;
        Assert.Equal(16f, bins[140]); // 0.299·100+0.587·150+0.114·200 = 140.75 → bin 140
        for (int i = 0; i < bins.Length; i++)
        {
            if (i == 140) continue;
            Assert.Equal(0f, bins[i]);
        }
    }

    [Fact]
    public async Task BrightnessHistogram_Null_ReturnsNullArray()
    {
        ValueRef result = await new ImageBrightnessHistogramFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, MakeFrame(), default);
        Assert.True(result.IsArray);
        Assert.True(result.IsNull);
    }

    private static ValueRef MakeSolid(int w, int h, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return ValueRef.FromImage(bmp);
    }

    private static ValueRef MakeCheckerboard(int w, int h)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(((x + y) & 1) == 0 ? 0 : 255);
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }
        return ValueRef.FromImage(bmp);
    }

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }
}
