using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// <c>image_transpose</c> — reflection across the main diagonal. Verifies the
/// dimension swap, the exact pixel mapping (dst[x,y] = src[y,x]), the
/// round-trip identity, and null propagation.
/// </summary>
public sealed class ImageTransposeFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task Transpose_SwapsDimensions()
    {
        ValueRef result = await new ImageTransposeFunction().ExecuteAsync(
            new[] { MakeSolid(20, 10, 40, 80, 120) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(10, bmp.Width);
        Assert.Equal(20, bmp.Height);
    }

    [Fact]
    public async Task Transpose_MovesPixelAcrossDiagonal()
    {
        // Distinctive pixel at src (3, 1) must land at dst (1, 3).
        SKBitmap src = new(new SKImageInfo(6, 4, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(src))
        {
            canvas.Clear(new SKColor(0, 0, 0, 255));
        }
        SKColor marker = new(200, 100, 50, 255);
        src.SetPixel(3, 1, marker);

        ValueRef result = await new ImageTransposeFunction().ExecuteAsync(
            new[] { ValueRef.FromImage(src) }, CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();

        Assert.Equal(marker, bmp.GetPixel(1, 3));
        // The original location is now background.
        Assert.Equal(new SKColor(0, 0, 0, 255), bmp.GetPixel(3, 1));
    }

    [Fact]
    public async Task Transpose_Twice_IsIdentity()
    {
        ValueRef src = MakeCheckerboard(7, 5);
        ImageTransposeFunction fn = new();

        ValueRef once = await fn.ExecuteAsync(new[] { src }, CreateEvaluationFrame(), default);
        ValueRef twice = await fn.ExecuteAsync(new[] { once }, CreateEvaluationFrame(), default);

        SKBitmap original = src.AsImage();
        SKBitmap roundTripped = twice.AsImage();
        Assert.Equal(original.Width, roundTripped.Width);
        Assert.Equal(original.Height, roundTripped.Height);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                Assert.Equal(original.GetPixel(x, y), roundTripped.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public async Task Transpose_NullPropagation()
    {
        ValueRef result = await new ImageTransposeFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
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
                byte v = (byte)((x + y) % 2 == 0 ? 0 : 255);
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }
        return ValueRef.FromImage(bmp);
    }
}
