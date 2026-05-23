using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// Phase-3 basic transforms: resize, grayscale, rotate, blur, brighten,
/// darken, sobel.
/// </summary>
public sealed class BasicTransformsTests : ServiceTestBase
{
    // ----- resize -----

    [Fact]
    public async Task Resize_ProducesRequestedDimensions()
    {
        ValueRef result = await new ResizeImageFunction().ExecuteAsync(
            new[] { MakeSolid(8, 8, 50, 100, 150), ValueRef.FromInt32(16), ValueRef.FromInt32(32) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(16, bmp.Width);
        Assert.Equal(32, bmp.Height);
    }

    [Fact]
    public async Task Resize_NonPositiveDimension_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ResizeImageFunction().ExecuteAsync(
                new[] { MakeSolid(8, 8, 0, 0, 0), ValueRef.FromInt32(0), ValueRef.FromInt32(8) },
                CreateEvaluationFrame(), default));
        Assert.Contains("positive", ex.Message);
    }

    [Fact]
    public async Task Resize_NullPropagation()
    {
        ValueRef result = await new ResizeImageFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image), ValueRef.FromInt32(4), ValueRef.FromInt32(4) },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task Resize_DefaultModeMatchesExplicitBilinear()
    {
        ValueRef src = MakeCheckerboard(4, 4);
        ValueRef defaultResult = await new ResizeImageFunction().ExecuteAsync(
            new[] { src, ValueRef.FromInt32(8), ValueRef.FromInt32(8) },
            CreateEvaluationFrame(), default);
        ValueRef explicitBilinear = await new ResizeImageFunction().ExecuteAsync(
            new[] { src, ValueRef.FromInt32(8), ValueRef.FromInt32(8), ValueRef.FromString("bilinear") },
            CreateEvaluationFrame(), default);
        AssertBitmapsEqual(defaultResult.AsImage(), explicitBilinear.AsImage());
    }

    [Fact]
    public async Task Resize_NearestProducesOnlySourceValues()
    {
        // 4×4 checkerboard of pure black + pure white upscaled 2×. Nearest must
        // never invent intermediate greys.
        ValueRef src = MakeCheckerboard(4, 4);
        ValueRef result = await new ResizeImageFunction().ExecuteAsync(
            new[] { src, ValueRef.FromInt32(8), ValueRef.FromInt32(8), ValueRef.FromString("nearest") },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                byte r = bmp.GetPixel(x, y).Red;
                Assert.True(r == 0 || r == 255,
                    $"nearest produced intermediate value {r} at ({x},{y})");
            }
        }
    }

    [Fact]
    public async Task Resize_BilinearBlendsAcrossBoundary()
    {
        // Same source. Bilinear must produce at least one strictly-intermediate
        // value somewhere along the checker boundary.
        ValueRef src = MakeCheckerboard(4, 4);
        ValueRef result = await new ResizeImageFunction().ExecuteAsync(
            new[] { src, ValueRef.FromInt32(8), ValueRef.FromInt32(8), ValueRef.FromString("bilinear") },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        bool foundBlend = false;
        for (int y = 0; y < bmp.Height && !foundBlend; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                byte r = bmp.GetPixel(x, y).Red;
                if (r > 0 && r < 255) { foundBlend = true; break; }
            }
        }
        Assert.True(foundBlend, "bilinear produced no intermediate values — looks like nearest");
    }

    [Fact]
    public async Task Resize_ModeIsCaseInsensitive()
    {
        ValueRef src = MakeCheckerboard(4, 4);
        ValueRef lower = await new ResizeImageFunction().ExecuteAsync(
            new[] { src, ValueRef.FromInt32(8), ValueRef.FromInt32(8), ValueRef.FromString("nearest") },
            CreateEvaluationFrame(), default);
        ValueRef upper = await new ResizeImageFunction().ExecuteAsync(
            new[] { src, ValueRef.FromInt32(8), ValueRef.FromInt32(8), ValueRef.FromString("NEAREST") },
            CreateEvaluationFrame(), default);
        AssertBitmapsEqual(lower.AsImage(), upper.AsImage());
    }

    [Fact]
    public async Task Resize_UnknownMode_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ResizeImageFunction().ExecuteAsync(
                new[] { MakeSolid(4, 4, 0, 0, 0), ValueRef.FromInt32(8), ValueRef.FromInt32(8), ValueRef.FromString("lanczos") },
                CreateEvaluationFrame(), default));
        Assert.Contains("lanczos", ex.Message);
        Assert.Contains("bilinear", ex.Message);
    }

    [Fact]
    public async Task Resize_NullMode_ReturnsNullImage()
    {
        ValueRef result = await new ResizeImageFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 0, 0, 0), ValueRef.FromInt32(8), ValueRef.FromInt32(8), ValueRef.Null(DataKind.String) },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public void Resize_AvailableModes_ContainsAllFive()
    {
        IReadOnlyCollection<string> modes = ResizeImageFunction.AvailableModes;
        Assert.Contains("nearest", modes);
        Assert.Contains("bilinear", modes);
        Assert.Contains("trilinear", modes);
        Assert.Contains("mitchell", modes);
        Assert.Contains("catmullrom", modes);
        Assert.Equal(5, modes.Count);
    }

    [Theory]
    [InlineData("nearest")]
    [InlineData("bilinear")]
    [InlineData("trilinear")]
    [InlineData("mitchell")]
    [InlineData("catmullrom")]
    public async Task Resize_EveryMode_ProducesRequestedDimensions(string mode)
    {
        ValueRef result = await new ResizeImageFunction().ExecuteAsync(
            new[] { MakeCheckerboard(4, 4), ValueRef.FromInt32(16), ValueRef.FromInt32(32), ValueRef.FromString(mode) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(16, bmp.Width);
        Assert.Equal(32, bmp.Height);
    }

    // ----- grayscale -----

    [Fact]
    public async Task Grayscale_RedImage_AllChannelsEqualLuminance()
    {
        ValueRef result = await new GrayscaleImageFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 255, 0, 0) }, CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        SKColor px = bmp.GetPixel(0, 0);
        // BT.601: R contributes 0.299, so luminance ≈ 76 for pure red.
        Assert.InRange((int)px.Red, 72, 80);
        Assert.Equal(px.Red, px.Green);
        Assert.Equal(px.Red, px.Blue);
        Assert.Equal(255, px.Alpha);
    }

    [Fact]
    public async Task Grayscale_SolidWhiteStaysWhite()
    {
        ValueRef result = await new GrayscaleImageFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 255, 255, 255) }, CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(0, 0);
        Assert.Equal(255, px.Red);
        Assert.Equal(255, px.Green);
        Assert.Equal(255, px.Blue);
    }

    // ----- rotate -----

    [Fact]
    public async Task Rotate_90Degrees_SwapsDimensions()
    {
        ValueRef result = await new RotateImageFunction().ExecuteAsync(
            new[] { MakeSolid(20, 10, 50, 50, 50), ValueRef.FromFloat32(90) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(10, bmp.Width);
        Assert.Equal(20, bmp.Height);
    }

    [Fact]
    public async Task Rotate_45Degrees_ExpandsCanvas()
    {
        ValueRef result = await new RotateImageFunction().ExecuteAsync(
            new[] { MakeSolid(10, 10, 50, 50, 50), ValueRef.FromFloat32(45) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        // diagonal of 10×10 is ~14
        Assert.InRange(bmp.Width, 14, 15);
        Assert.InRange(bmp.Height, 14, 15);
    }

    [Fact]
    public async Task Rotate_Zero_PreservesDimensions()
    {
        ValueRef result = await new RotateImageFunction().ExecuteAsync(
            new[] { MakeSolid(12, 8, 50, 50, 50), ValueRef.FromFloat32(0) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(12, bmp.Width);
        Assert.Equal(8, bmp.Height);
    }

    // ----- blur -----

    [Fact]
    public async Task Blur_ZeroRadius_IsIdentity()
    {
        ValueRef result = await new BlurImageFunction().ExecuteAsync(
            new[] { MakeSolid(8, 8, 100, 150, 200), ValueRef.FromFloat32(0) },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(4, 4);
        Assert.Equal(100, px.Red);
        Assert.Equal(150, px.Green);
        Assert.Equal(200, px.Blue);
    }

    [Fact]
    public async Task Blur_NegativeRadius_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new BlurImageFunction().ExecuteAsync(
                new[] { MakeSolid(8, 8, 0, 0, 0), ValueRef.FromFloat32(-1) },
                CreateEvaluationFrame(), default));
        Assert.Contains("non-negative", ex.Message);
    }

    [Fact]
    public async Task Blur_PreservesDimensions()
    {
        ValueRef result = await new BlurImageFunction().ExecuteAsync(
            new[] { MakeSolid(16, 16, 100, 100, 100), ValueRef.FromFloat32(2) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(16, bmp.Width);
        Assert.Equal(16, bmp.Height);
    }

    // ----- brighten / darken -----

    [Fact]
    public async Task Brighten_AddsToRgbChannels()
    {
        ValueRef result = await new BrightenImageFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 100, 100, 100), ValueRef.FromFloat32(50) },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(0, 0);
        // 100 + 50 = 150 (within tolerance of color-matrix rounding)
        Assert.InRange((int)px.Red, 148, 152);
        Assert.InRange((int)px.Green, 148, 152);
        Assert.InRange((int)px.Blue, 148, 152);
    }

    [Fact]
    public async Task Brighten_ClampsAt255()
    {
        ValueRef result = await new BrightenImageFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 200, 200, 200), ValueRef.FromFloat32(100) },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(0, 0);
        Assert.Equal(255, px.Red);
    }

    [Fact]
    public async Task Darken_SubtractsFromRgbChannels()
    {
        ValueRef result = await new DarkenImageFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 200, 200, 200), ValueRef.FromFloat32(50) },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(0, 0);
        Assert.InRange((int)px.Red, 148, 152);
    }

    [Fact]
    public async Task Darken_ClampsAtZero()
    {
        ValueRef result = await new DarkenImageFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 30, 30, 30), ValueRef.FromFloat32(100) },
            CreateEvaluationFrame(), default);
        SKColor px = result.AsImage().GetPixel(0, 0);
        Assert.Equal(0, px.Red);
    }

    // ----- sobel -----

    [Fact]
    public async Task Sobel_SolidImage_AllZerosOrBorder()
    {
        ValueRef result = await new SobelImageFunction().ExecuteAsync(
            new[] { MakeSolid(16, 16, 128, 128, 128) }, CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        // Interior pixels — no edges anywhere, magnitude 0.
        Assert.Equal(0, bmp.GetPixel(8, 8).Red);
        Assert.Equal(255, bmp.GetPixel(8, 8).Alpha);
    }

    [Fact]
    public async Task Sobel_VerticalEdge_ProducesNonZeroResponse()
    {
        // Left half black, right half white. Edge runs at x=8.
        SKBitmap bmp = new(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                bmp.SetPixel(x, y, x < 8 ? new SKColor(0, 0, 0, 255) : new SKColor(255, 255, 255, 255));
        ValueRef result = await new SobelImageFunction().ExecuteAsync(
            new[] { ValueRef.FromImage(bmp) }, CreateEvaluationFrame(), default);
        SKBitmap edge = result.AsImage();
        // Sobel at x=8, y interior, should clamp at 255 (large jump).
        Assert.Equal(255, edge.GetPixel(8, 8).Red);
    }

    [Fact]
    public async Task Sobel_BorderIsOpaqueBlack()
    {
        ValueRef result = await new SobelImageFunction().ExecuteAsync(
            new[] { MakeSolid(8, 8, 50, 100, 200) }, CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        SKColor border = bmp.GetPixel(0, 0);
        Assert.Equal(0, border.Red);
        Assert.Equal(0, border.Green);
        Assert.Equal(0, border.Blue);
        Assert.Equal(255, border.Alpha);
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

    private static void AssertBitmapsEqual(SKBitmap a, SKBitmap b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
            }
        }
    }
}
