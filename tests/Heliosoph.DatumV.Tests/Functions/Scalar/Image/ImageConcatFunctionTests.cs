using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// <c>image_concat</c> — joins two images side-by-side or stacked. Verifies the
/// output dimensions for both axes, the default (horizontal) shape, that each
/// source's pixels land in the expected half with cross-axis centring, the
/// unequal-size transparent-margin behaviour, bad-direction rejection, and null
/// propagation.
/// </summary>
public sealed class ImageConcatFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task Concat_DefaultsToHorizontal()
    {
        ValueRef result = await new ImageConcatFunction().ExecuteAsync(
            new[] { MakeSolid(20, 10, 255, 0, 0), MakeSolid(30, 10, 0, 0, 255) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(50, bmp.Width);   // 20 + 30
        Assert.Equal(10, bmp.Height);  // max(10, 10)
    }

    [Fact]
    public async Task Concat_Horizontal_PlacesImagesLeftThenRight()
    {
        ValueRef result = await new ImageConcatFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(20, 10, 255, 0, 0),   // red on the left
                MakeSolid(30, 10, 0, 0, 255),   // blue on the right
                ValueRef.FromString("h"),
            },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();

        Assert.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(5, 5));    // left half → red
        Assert.Equal(new SKColor(0, 0, 255, 255), bmp.GetPixel(35, 5));   // right half → blue
    }

    [Fact]
    public async Task Concat_Vertical_PlacesImagesTopThenBottom()
    {
        ValueRef result = await new ImageConcatFunction().ExecuteAsync(
            new[]
            {
                MakeSolid(10, 20, 255, 0, 0),   // red on top
                MakeSolid(10, 30, 0, 0, 255),   // blue on bottom
                ValueRef.FromString("vertical"),
            },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();

        Assert.Equal(10, bmp.Width);   // max(10, 10)
        Assert.Equal(50, bmp.Height);  // 20 + 30
        Assert.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(5, 5));    // top → red
        Assert.Equal(new SKColor(0, 0, 255, 255), bmp.GetPixel(5, 35));   // bottom → blue
    }

    [Fact]
    public async Task Concat_UnequalCrossAxis_FillsMarginWithTransparency()
    {
        // Short image (h=4) beside a taller one (h=10): the short image is
        // centred, leaving transparent bands above and below it.
        ValueRef result = await new ImageConcatFunction().ExecuteAsync(
            new[] { MakeSolid(10, 4, 255, 0, 0), MakeSolid(10, 10, 0, 0, 255) },
            CreateEvaluationFrame(), default);
        SKBitmap bmp = result.AsImage();

        Assert.Equal(10, bmp.Height);
        // Top-left corner is above the centred 4px-tall red image → transparent.
        Assert.Equal(0, bmp.GetPixel(5, 0).Alpha);
        // The red image occupies rows 3..6 (centred in 10).
        Assert.Equal(new SKColor(255, 0, 0, 255), bmp.GetPixel(5, 4));
    }

    [Fact]
    public async Task Concat_RejectsUnknownDirection()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            new ImageConcatFunction().ExecuteAsync(
                new[]
                {
                    MakeSolid(10, 10, 1, 1, 1),
                    MakeSolid(10, 10, 2, 2, 2),
                    ValueRef.FromString("diagonal"),
                },
                CreateEvaluationFrame(), default).AsTask());
    }

    [Fact]
    public async Task Concat_NullPropagation()
    {
        ValueRef result = await new ImageConcatFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image), MakeSolid(10, 10, 1, 1, 1) },
            CreateEvaluationFrame(), default);
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
}
