using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Pooling;
using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ImageCutoutFunction"/>. The function pairs with
/// U²-Net's saliency masks to produce transparent-background cutouts —
/// these tests prove the alpha-replacement logic and resize-to-source
/// fallback without needing the actual ONNX model.
/// </summary>
public sealed class ImageCutoutFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("image_cutout", ImageCutoutFunction.Name);
        Assert.Equal(FunctionCategory.Image, ImageCutoutFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ImageCutoutFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsTwoImages()
    {
        DataKind kind = new ImageCutoutFunction()
            .ValidateArguments([DataKind.Image, DataKind.Image]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public async Task Execute_HalfMask_ReplacesAlphaPerPixel()
    {
        // 4×4 solid red source. Mask is white on the left half (columns 0–1)
        // and black on the right (columns 2–3) — produced as a grayscale-as-
        // RGBA bitmap matching what U2NetModel emits.
        using SKBitmap source = MakeSolidBitmap(4, 4, SKColors.Red);
        using SKBitmap mask = MakeHalfMask(4, 4);

        ValueRef result = await new ImageCutoutFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            MakeFrame(),
            default);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);

        SKBitmap composed = result.AsImage();
        Assert.Equal(4, composed.Width);
        Assert.Equal(4, composed.Height);

        // Left half: opaque red. Right half: transparent (alpha 0), RGB still red.
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                byte expectedAlpha = (byte)(x < 2 ? 255 : 0);
                Assert.Equal(expectedAlpha, c.Alpha);
                Assert.Equal((byte)255, c.Red);
                Assert.Equal((byte)0, c.Green);
                Assert.Equal((byte)0, c.Blue);
            }
        }
    }

    [Fact]
    public async Task Execute_MaskDifferentSize_ResizesToSource()
    {
        // 8×8 source, 4×4 fully-opaque mask. The mask must resize up to
        // 8×8 internally; we expect every output pixel to be opaque.
        using SKBitmap source = MakeSolidBitmap(8, 8, SKColors.Blue);
        using SKBitmap mask = MakeSolidBitmap(4, 4, SKColors.White);

        ValueRef result = await new ImageCutoutFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromImage(mask) },
            MakeFrame(),
            default);

        SKBitmap composed = result.AsImage();
        Assert.Equal(8, composed.Width);
        Assert.Equal(8, composed.Height);

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
    }

    [Fact]
    public async Task Execute_NullImage_ReturnsNullImage()
    {
        using SKBitmap mask = MakeSolidBitmap(4, 4, SKColors.White);

        ValueRef result = await new ImageCutoutFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromImage(mask) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task Execute_NullMask_ReturnsNullImage()
    {
        using SKBitmap source = MakeSolidBitmap(4, 4, SKColors.Red);

        ValueRef result = await new ImageCutoutFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.Null(DataKind.Image) },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    private static SKBitmap MakeSolidBitmap(int width, int height, SKColor color)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bmp.Erase(color);
        return bmp;
    }

    /// <summary>
    /// Builds a width×height grayscale-as-RGBA mask whose left half is white
    /// (R=G=B=255) and right half is black (R=G=B=0). Mirrors the shape
    /// <see cref="DatumIngest.Models.Onnx.U2NetModel"/> emits.
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

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }
}
