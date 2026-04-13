using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ImageMultiplyFunction"/> — Photoshop-style multiply
/// blend with a grayscale mask. Proves the per-channel arithmetic, alpha
/// preservation, and the resize-to-source fallback.
/// </summary>
public sealed class ImageMultiplyFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("image_multiply", ImageMultiplyFunction.Name);
        Assert.Equal(FunctionCategory.Image, ImageMultiplyFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ImageMultiplyFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsTwoImages()
    {
        DataKind kind = new ImageMultiplyFunction()
            .ValidateArguments([DataKind.Image, DataKind.Image]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public async Task Execute_WhiteMask_PassesSourceThrough()
    {
        using SKBitmap source = MakeSolidBitmap(4, 4, new SKColor(200, 150, 100, 255));
        using SKBitmap mask = MakeSolidBitmap(4, 4, SKColors.White);

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            CreateEvaluationFrame(),
            default);

        SKBitmap composed = result.AsImage();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)200, c.Red);
                Assert.Equal((byte)150, c.Green);
                Assert.Equal((byte)100, c.Blue);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
    }

    [Fact]
    public async Task Execute_BlackMask_ZeroesRgbButKeepsAlpha()
    {
        using SKBitmap source = MakeSolidBitmap(4, 4, new SKColor(200, 150, 100, 255));
        using SKBitmap mask = MakeSolidBitmap(4, 4, SKColors.Black);

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            CreateEvaluationFrame(),
            default);

        SKBitmap composed = result.AsImage();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)0, c.Red);
                Assert.Equal((byte)0, c.Green);
                Assert.Equal((byte)0, c.Blue);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
    }

    [Fact]
    public async Task Execute_HalfMask_MultipliesPerPixel()
    {
        // 4×4 solid red source. Mask is white on the left half (cols 0–1)
        // and black on the right (cols 2–3).
        using SKBitmap source = MakeSolidBitmap(4, 4, SKColors.Red);
        using SKBitmap mask = MakeHalfMask(4, 4);

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            CreateEvaluationFrame(),
            default);

        SKBitmap composed = result.AsImage();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                byte expectedRed = (byte)(x < 2 ? 255 : 0);
                Assert.Equal(expectedRed, c.Red);
                Assert.Equal((byte)0, c.Green);
                Assert.Equal((byte)0, c.Blue);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
    }

    [Fact]
    public async Task Execute_GrayMask_ScalesProportionally()
    {
        // Mask R = 128 → out ≈ src * 128 / 255 = src * 0.502.
        using SKBitmap source = MakeSolidBitmap(2, 2, new SKColor(200, 100, 50, 255));
        using SKBitmap mask = MakeSolidBitmap(2, 2, new SKColor(128, 128, 128, 255));

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            CreateEvaluationFrame(),
            default);

        SKBitmap composed = result.AsImage();
        SKColor c = composed.GetPixel(0, 0);
        // Integer-truncated multiply: (200*128)/255 = 100, (100*128)/255 = 50, (50*128)/255 = 25.
        Assert.Equal((byte)100, c.Red);
        Assert.Equal((byte)50, c.Green);
        Assert.Equal((byte)25, c.Blue);
        Assert.Equal((byte)255, c.Alpha);
    }

    [Fact]
    public async Task Execute_PreservesSourceAlpha()
    {
        // Semi-transparent source — alpha must pass through unchanged
        // regardless of mask intensity.
        using SKBitmap source = MakeSolidBitmap(2, 2, new SKColor(200, 200, 200, 128));
        using SKBitmap mask = MakeSolidBitmap(2, 2, SKColors.Black);

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            CreateEvaluationFrame(),
            default);

        SKBitmap composed = result.AsImage();
        SKColor c = composed.GetPixel(0, 0);
        Assert.Equal((byte)0, c.Red);
        Assert.Equal((byte)128, c.Alpha);
    }

    [Fact]
    public async Task Execute_MaskDifferentSize_ResizesToSource()
    {
        using SKBitmap source = MakeSolidBitmap(8, 8, SKColors.Blue);
        using SKBitmap mask = MakeSolidBitmap(4, 4, SKColors.White);

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            CreateEvaluationFrame(),
            default);

        SKBitmap composed = result.AsImage();
        Assert.Equal(8, composed.Width);
        Assert.Equal(8, composed.Height);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)0, c.Red);
                Assert.Equal((byte)0, c.Green);
                Assert.Equal((byte)255, c.Blue);
            }
        }
    }

    [Fact]
    public async Task Execute_NullImage_ReturnsNullImage()
    {
        using SKBitmap mask = MakeSolidBitmap(4, 4, SKColors.White);

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromImage(mask) },
            CreateEvaluationFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task Execute_NullMask_ReturnsNullImage()
    {
        using SKBitmap source = MakeSolidBitmap(4, 4, SKColors.Red);

        ValueRef result = await new ImageMultiplyFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.Null(DataKind.Image) },
            CreateEvaluationFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    private static SKBitmap MakeSolidBitmap(int width, int height, SKColor color)
    {
        SKAlphaType alphaType = color.Alpha == 255 ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, alphaType);
        bmp.Erase(color);
        return bmp;
    }

    /// <summary>
    /// Width×height grayscale-as-RGBA mask: left half white, right half black.
    /// </summary>
    private static SKBitmap MakeHalfMask(int width, int height)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = (byte)(x < width / 2 ? 255 : 0);
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }
        return bmp;
    }
}
