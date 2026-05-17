using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// <c>image_decode(bytes)</c> scalar: wraps an encoded-image byte array as a
/// typed <see cref="DataKind.Image"/> value. Mirror of the audio_decode
/// contract — no pixel materialization, kind tag flips, dimensions parse
/// lazily at the materialization boundary.
/// </summary>
public sealed class ImageDecodeFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_ReturnsImage()
    {
        ImageDecodeFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments([DataKind.UInt8]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public async Task ImageDecode_NullBytes_ReturnsNullImage()
    {
        ValueRef result = await new ImageDecodeFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.UInt8) },
            CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Image, result.Kind);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task ImageDecode_PngBytes_ProducesImageWithParsedDimensions()
    {
        byte[] png = BuildSolidPng(width: 17, height: 23, r: 200, g: 100, b: 50);

        ValueRef result = await new ImageDecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromBytes(DataKind.UInt8, png, isArray: true) },
            CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Image, result.Kind);

        using Arena store = CreateArena();
        DataValue image = result.ToDataValue(store);
        Assert.Equal(DataKind.Image, image.Kind);
        Assert.Equal((ushort)17, image.ImageWidth);
        Assert.Equal((ushort)23, image.ImageHeight);
    }

    [Fact]
    public async Task ImageDecode_JpegBytes_ProducesImageWithParsedDimensions()
    {
        byte[] jpeg = BuildSolidJpeg(width: 40, height: 30);

        ValueRef result = await new ImageDecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromBytes(DataKind.UInt8, jpeg, isArray: true) },
            CreateEvaluationFrame(), default);

        using Arena store = CreateArena();
        DataValue image = result.ToDataValue(store);
        Assert.Equal(DataKind.Image, image.Kind);
        Assert.Equal((ushort)40, image.ImageWidth);
        Assert.Equal((ushort)30, image.ImageHeight);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static byte[] BuildSolidPng(int width, int height, byte r, byte g, byte b)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static byte[] BuildSolidJpeg(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(new SKColor(128, 128, 128, 255));
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
