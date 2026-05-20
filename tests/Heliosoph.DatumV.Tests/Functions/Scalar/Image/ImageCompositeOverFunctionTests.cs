using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ImageCompositeOverFunction"/>. Pairs with
/// <see cref="ImageCutoutFunction"/> in the typical pipeline: cut out a
/// subject, then flatten over the background colour the consumer model was
/// trained on. The "subject + 0.5 gray" composite is what TripoSR's
/// upstream image_processor produces, so getting this exactly right matters
/// for the mesh-from-image demo.
/// </summary>
public sealed class ImageCompositeOverFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("image_composite_over", ImageCompositeOverFunction.Name);
        Assert.Equal(FunctionCategory.Image, ImageCompositeOverFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ImageCompositeOverFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsImagePlusFloat32Array_ReturnsImage()
    {
        DataKind kind = new ImageCompositeOverFunction()
            .ValidateArguments([DataKind.Image, DataKind.Float32]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public async Task Execute_OpaqueInput_IsRgbNoOpButFlattensAlphaTo255()
    {
        // 4×4 solid red, fully opaque. Background = mid gray. Output should
        // preserve the red and stamp alpha=255 across the board.
        using SKBitmap source = MakeSolidBitmap(4, 4, new SKColor(255, 0, 0, 255));
        ValueRef result = await Run(source, [0.5f, 0.5f, 0.5f]);

        SKBitmap composed = result.AsImage();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)255, c.Red);
                Assert.Equal((byte)0, c.Green);
                Assert.Equal((byte)0, c.Blue);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
    }

    [Fact]
    public async Task Execute_FullyTransparentInput_OutputIsBackgroundColor()
    {
        // 4×4 image with alpha=0 everywhere. Output should be the background
        // colour everywhere, alpha=255. This is the alpha-cutout case: the
        // RGB underneath transparency is irrelevant since alpha=0 makes the
        // background colour fully dominate.
        using SKBitmap source = MakeSolidBitmap(4, 4, new SKColor(255, 0, 0, 0));
        ValueRef result = await Run(source, [0.5f, 0.5f, 0.5f]);

        SKBitmap composed = result.AsImage();
        // 0.5 * 255 = 127.5 → rounds to 128 with the round-to-nearest blend.
        // Same value all 4 channels except alpha.
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                SKColor c = composed.GetPixel(x, y);
                Assert.Equal((byte)128, c.Red);
                Assert.Equal((byte)128, c.Green);
                Assert.Equal((byte)128, c.Blue);
                Assert.Equal((byte)255, c.Alpha);
            }
        }
    }

    [Fact]
    public async Task Execute_HalfTransparentCutout_BlendsHalves()
    {
        // 4×4 image: left half red (opaque), right half red (alpha=0).
        // Background = white. Expected: left half stays red, right half
        // becomes white.
        SKBitmap source = new(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                byte alpha = (byte)(x < 2 ? 255 : 0);
                source.SetPixel(x, y, new SKColor(255, 0, 0, alpha));
            }
        }

        ValueRef result = await Run(source, [1.0f, 1.0f, 1.0f]);
        source.Dispose();

        SKBitmap composed = result.AsImage();
        for (int y = 0; y < 4; y++)
        {
            // Left half: pure red, opaque
            SKColor left = composed.GetPixel(0, y);
            Assert.Equal((byte)255, left.Red);
            Assert.Equal((byte)0, left.Green);
            Assert.Equal((byte)0, left.Blue);
            Assert.Equal((byte)255, left.Alpha);

            // Right half: pure white (background), opaque
            SKColor right = composed.GetPixel(3, y);
            Assert.Equal((byte)255, right.Red);
            Assert.Equal((byte)255, right.Green);
            Assert.Equal((byte)255, right.Blue);
            Assert.Equal((byte)255, right.Alpha);
        }
    }

    [Fact]
    public async Task Execute_WrongBackgroundLength_Throws()
    {
        using SKBitmap source = MakeSolidBitmap(2, 2, SKColors.Red);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ImageCompositeOverFunction().ExecuteAsync(
                new ValueRef[]
                {
                    ValueRef.FromImage(source),
                    ValueRef.FromPrimitiveArray(new float[] { 0.5f, 0.5f }, DataKind.Float32),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("Float32[3]", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_NullImage_ReturnsNullImage()
    {
        ValueRef result = await new ImageCompositeOverFunction().ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.Null(DataKind.Image),
                ValueRef.FromPrimitiveArray(new float[] { 0.5f, 0.5f, 0.5f }, DataKind.Float32),
            },
            CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    // ─────────────────────── Helpers ───────────────────────

    private async Task<ValueRef> Run(SKBitmap source, float[] bg)
    {
        return await new ImageCompositeOverFunction().ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromImage(source),
                ValueRef.FromPrimitiveArray(bg, DataKind.Float32),
            },
            CreateEvaluationFrame(), default);
    }

    private static SKBitmap MakeSolidBitmap(int width, int height, SKColor color)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bmp.Erase(color);
        return bmp;
    }
}
