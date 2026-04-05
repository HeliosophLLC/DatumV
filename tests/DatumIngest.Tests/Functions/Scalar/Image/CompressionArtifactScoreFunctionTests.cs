using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="CompressionArtifactScoreFunction"/> — JPEG blockiness
/// estimator.
/// </summary>
public sealed class CompressionArtifactScoreFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task SolidImage_ScoresZero()
    {
        ValueRef result = await new CompressionArtifactScoreFunction().ExecuteAsync(
            new[] { MakeSolid(32, 32, 128, 128, 128) }, MakeFrame(), default);
        Assert.Equal(0f, result.AsFloat32(), 3);
    }

    [Fact]
    public async Task ImageSmallerThanBlockBoundary_ReturnsZero()
    {
        ValueRef result = await new CompressionArtifactScoreFunction().ExecuteAsync(
            new[] { MakeSolid(15, 15, 128, 128, 128) }, MakeFrame(), default);
        Assert.Equal(0f, result.AsFloat32(), 3);
    }

    [Fact]
    public async Task BlockyImage_ScoresHigherThanSmoothGradient()
    {
        ValueRef blocky = await new CompressionArtifactScoreFunction().ExecuteAsync(
            new[] { MakeBlocky(32, 32) }, MakeFrame(), default);
        ValueRef gradient = await new CompressionArtifactScoreFunction().ExecuteAsync(
            new[] { MakeHorizontalGradient(32, 32) }, MakeFrame(), default);

        Assert.True(blocky.AsFloat32() > gradient.AsFloat32(),
            $"Blocky ({blocky.AsFloat32()}) should outscore smooth gradient ({gradient.AsFloat32()}).");
    }

    [Fact]
    public async Task ResultIsClampedToZeroOne()
    {
        ValueRef result = await new CompressionArtifactScoreFunction().ExecuteAsync(
            new[] { MakeBlocky(32, 32) }, MakeFrame(), default);
        float score = result.AsFloat32();
        Assert.InRange(score, 0f, 1f);
    }

    [Fact]
    public async Task Null_ReturnsNullFloat32()
    {
        ValueRef result = await new CompressionArtifactScoreFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, MakeFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
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

    /// <summary>
    /// Image with sharp 8×8 block boundaries plus a small intra-block linear
    /// ramp so the interior-gradient denominator is non-zero. The boundary
    /// gradient (per-block intensity jump) should dominate the interior
    /// gradient (the 2-per-pixel ramp), giving a non-zero blockiness score.
    /// </summary>
    private static ValueRef MakeBlocky(int w, int h)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int by = 0; by < h; by += 8)
        {
            for (int bx = 0; bx < w; bx += 8)
            {
                int baseValue = ((bx / 8) * 73 + (by / 8) * 41) & 0xC0; // 0/64/128/192 per block
                for (int dy = 0; dy < 8 && by + dy < h; dy++)
                {
                    for (int dx = 0; dx < 8 && bx + dx < w; dx++)
                    {
                        // Small intra-block gradient: +2 per pixel horizontally.
                        byte v = (byte)System.Math.Clamp(baseValue + dx * 2, 0, 255);
                        bmp.SetPixel(bx + dx, by + dy, new SKColor(v, v, v, 255));
                    }
                }
            }
        }
        return ValueRef.FromImage(bmp);
    }

    private static ValueRef MakeHorizontalGradient(int w, int h)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)((x * 255) / System.Math.Max(1, w - 1));
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
