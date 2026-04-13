using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ImageToBytesFunction"/> — raw RGBA pixel byte
/// extraction.
/// </summary>
public sealed class ImageToBytesFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task ReturnsArrayOfExpectedLength()
    {
        const int width = 8;
        const int height = 4;

        ValueRef result = await Invoke(MakeSolidImage(width, height, 0, 0, 0));

        Assert.Equal(DataKind.UInt8, result.Kind);
        Assert.True(result.IsArray);
        Assert.False(result.IsNull);
        Assert.Equal(width * height * 4, result.GetArrayLength());
    }

    [Fact]
    public async Task SolidRedImage_ProducesRGBARedPixels()
    {
        ValueRef result = await Invoke(MakeSolidImage(2, 2, 255, 0, 0));

        byte[] pixels = result.AsBytes();
        Assert.Equal(2 * 2 * 4, pixels.Length);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i + 0]); // R
            Assert.Equal(0,   pixels[i + 1]); // G
            Assert.Equal(0,   pixels[i + 2]); // B
            Assert.Equal(255, pixels[i + 3]); // A
        }
    }

    [Fact]
    public async Task SolidGreenImage_ProducesRGBAGreenPixels()
    {
        ValueRef result = await Invoke(MakeSolidImage(3, 3, 0, 200, 0));

        byte[] pixels = result.AsBytes();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(0,   pixels[i + 0]);
            Assert.Equal(200, pixels[i + 1]);
            Assert.Equal(0,   pixels[i + 2]);
            Assert.Equal(255, pixels[i + 3]);
        }
    }

    [Fact]
    public async Task NullInput_ReturnsNullArray()
    {
        ValueRef result = await Invoke(ValueRef.Null(DataKind.Image));

        Assert.True(result.IsArray);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.UInt8, result.Kind);
    }

    [Fact]
    public async Task NonRgba8888Bitmap_ConvertsAndExtractsCorrectly()
    {
        // BGRA-native bitmap — the function must color-convert to RGBA.
        SKImageInfo info = new(2, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        SKBitmap bgr = new(info);
        using (SKCanvas canvas = new(bgr))
        {
            canvas.Clear(new SKColor(10, 20, 30, 255));
        }

        ValueRef result = await Invoke(ValueRef.FromImage(bgr));

        byte[] pixels = result.AsBytes();
        Assert.Equal(2 * 1 * 4, pixels.Length);
        Assert.Equal(10, pixels[0]);
        Assert.Equal(20, pixels[1]);
        Assert.Equal(30, pixels[2]);
        Assert.Equal(255, pixels[3]);
    }

    private async Task<ValueRef> Invoke(ValueRef image)
    {
        return await new ImageToBytesFunction().ExecuteAsync(
            new[] { image },
            CreateEvaluationFrame(),
            default);
    }

    private static ValueRef MakeSolidImage(int w, int h, byte r, byte g, byte b)
    {
        SKImageInfo info = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap bmp = new(info);
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return ValueRef.FromImage(bmp);
    }
}
