using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ImageResizeForegroundFunction"/>. Covers the
/// canonical-subject-placement preprocessing — alpha bbox detection,
/// square padding, ratio padding — that TripoSR-family models need.
/// </summary>
public sealed class ImageResizeForegroundFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("image_resize_foreground", ImageResizeForegroundFunction.Name);
        Assert.Equal(FunctionCategory.Image, ImageResizeForegroundFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ImageResizeForegroundFunction.Description));
    }

    [Fact]
    public async Task Execute_OffCentreSubject_TightlyCropsAndCentres()
    {
        // 10×10 canvas, a 2×2 opaque red square at (3, 4)..(4, 5).
        // Alpha bbox is 2×2; with ratio = 0.5, final = 2 / 0.5 = 4.
        // Subject centred: at offset ((4-2)/2, (4-2)/2) = (1, 1).
        SKBitmap source = new(10, 10, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        source.Erase(new SKColor(0, 0, 0, 0));
        for (int y = 4; y <= 5; y++)
        {
            for (int x = 3; x <= 4; x++)
            {
                source.SetPixel(x, y, new SKColor(255, 0, 0, 255));
            }
        }

        ValueRef result = await Run(source, ratio: 0.5f);
        source.Dispose();

        SKBitmap composed = result.AsImage();
        Assert.Equal(4, composed.Width);
        Assert.Equal(4, composed.Height);
        // Centre 2×2 = the subject pixels, all opaque red.
        for (int y = 1; y <= 2; y++)
        {
            for (int x = 1; x <= 2; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)255, c.Red);
                Assert.Equal((byte)0, c.Green);
                Assert.Equal((byte)0, c.Blue);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
        // Outer ring of pixels: transparent black (the padding).
        SKColor corner = composed.GetPixel(0, 0);
        Assert.Equal((byte)0, corner.Alpha);
    }

    [Fact]
    public async Task Execute_NonSquareSubject_PadsToSquareAndCentres()
    {
        // 12×8 canvas with a 6×2 opaque rectangle at rows 3..4, columns 2..7.
        // Bbox is 2×6; side = 6; final = round(6 / 0.5) = 12. Centred at
        // ((12-2)/2, (12-6)/2) = (5, 3).
        SKBitmap source = new(12, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        source.Erase(new SKColor(0, 0, 0, 0));
        for (int y = 3; y <= 4; y++)
        {
            for (int x = 2; x <= 7; x++)
            {
                source.SetPixel(x, y, new SKColor(0, 255, 0, 255));
            }
        }

        ValueRef result = await Run(source, ratio: 0.5f);
        source.Dispose();

        SKBitmap composed = result.AsImage();
        Assert.Equal(12, composed.Width);
        Assert.Equal(12, composed.Height);

        // Subject rows are at y ∈ [5, 6], x ∈ [3, 8].
        for (int y = 5; y <= 6; y++)
        {
            for (int x = 3; x <= 8; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)255, c.Green);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
        // Padded rows have alpha = 0.
        for (int x = 0; x < 12; x++)
        {
            Assert.Equal((byte)0, composed.GetPixel(x, 0).Alpha);
            Assert.Equal((byte)0, composed.GetPixel(x, 11).Alpha);
        }
    }

    [Fact]
    public async Task Execute_FullyOpaqueInput_UsesWholeImageAsBbox()
    {
        // No real alpha mask — every pixel has alpha = 255. The bbox is
        // the whole image, so the function pads to ratio without cropping.
        SKBitmap source = MakeSolidBitmap(4, 4, new SKColor(0, 0, 255, 255));
        ValueRef result = await Run(source, ratio: 0.5f);

        SKBitmap composed = result.AsImage();
        // 4 / 0.5 = 8.
        Assert.Equal(8, composed.Width);
        Assert.Equal(8, composed.Height);
        // Inner 4×4 is the source; outer ring is transparent.
        for (int y = 2; y <= 5; y++)
        {
            for (int x = 2; x <= 5; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)255, c.Blue);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
        Assert.Equal((byte)0, composed.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task Execute_FullyTransparentInput_ReturnsSourceUnchanged()
    {
        // Degenerate case — no foreground pixels. Should not crash; should
        // fall back to the source so downstream stays alive.
        using SKBitmap source = new(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        source.Erase(new SKColor(0, 0, 0, 0));

        ValueRef result = await Run(source, ratio: 0.5f);

        SKBitmap composed = result.AsImage();
        Assert.Equal(4, composed.Width);
        Assert.Equal(4, composed.Height);
        // All pixels still transparent.
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal((byte)0, composed.GetPixel(x, y).Alpha);
    }

    [Fact]
    public async Task Execute_NullImage_ReturnsNullImage()
    {
        ValueRef result = await new ImageResizeForegroundFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromFloat32(0.85f) },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-0.5f)]
    [InlineData(1.5f)]
    [InlineData(float.NaN)]
    public async Task Execute_RatioOutOfRange_Throws(float ratio)
    {
        using SKBitmap source = MakeSolidBitmap(2, 2, SKColors.Red);

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Run(source, ratio));
    }

    // ─────────────────────── Helpers ───────────────────────

    private async Task<ValueRef> Run(SKBitmap source, float ratio)
    {
        return await new ImageResizeForegroundFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromFloat32(ratio) },
            CreateEvaluationFrame(), default);
    }

    private static SKBitmap MakeSolidBitmap(int width, int height, SKColor color)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bmp.Erase(color);
        return bmp;
    }
}
