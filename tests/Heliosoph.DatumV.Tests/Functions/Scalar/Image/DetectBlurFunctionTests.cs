using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="DetectBlurFunction"/> — Laplacian variance blur detector.
/// </summary>
public sealed class DetectBlurFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task SolidImage_HasZeroLaplacianVariance()
    {
        ValueRef result = await new DetectBlurFunction().ExecuteAsync(
            new[] { MakeSolid(16, 16, 128, 128, 128) }, CreateEvaluationFrame(), default);
        Assert.Equal(0f, result.AsFloat32(), 3);
    }

    [Fact]
    public async Task CheckerboardImage_HasHighVarianceThanGradient()
    {
        // Sharp transitions (checker) should outscore a smooth gradient.
        ValueRef checker = await new DetectBlurFunction().ExecuteAsync(
            new[] { MakeCheckerboard(16, 16) }, CreateEvaluationFrame(), default);
        ValueRef gradient = await new DetectBlurFunction().ExecuteAsync(
            new[] { MakeHorizontalGradient(16, 16) }, CreateEvaluationFrame(), default);

        Assert.True(checker.AsFloat32() > gradient.AsFloat32(),
            $"Expected checker variance ({checker.AsFloat32()}) > gradient variance ({gradient.AsFloat32()}).");
    }

    [Fact]
    public async Task ImageSmallerThan3x3_ReturnsZero()
    {
        ValueRef result = await new DetectBlurFunction().ExecuteAsync(
            new[] { MakeSolid(2, 2, 128, 128, 128) }, CreateEvaluationFrame(), default);
        Assert.Equal(0f, result.AsFloat32(), 3);
    }

    [Fact]
    public async Task Null_ReturnsNullFloat32()
    {
        ValueRef result = await new DetectBlurFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, CreateEvaluationFrame(), default);
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
}
