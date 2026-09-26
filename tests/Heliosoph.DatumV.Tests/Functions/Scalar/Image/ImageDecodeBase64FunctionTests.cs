using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// <c>image_decode_base64(text)</c> scalar: base64-decodes a string and wraps
/// the resulting bytes as a typed <see cref="DataKind.Image"/> value. Base64
/// sibling of <see cref="ImageDecodeFunction"/> — strict base64 parse, then the
/// same permissive image-format contract (no pixel materialization, kind tag
/// flips, dimensions parse lazily at the materialization boundary).
/// </summary>
public sealed class ImageDecodeBase64FunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_ReturnsImage()
    {
        ImageDecodeBase64Function fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments([DataKind.String]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public async Task ImageDecodeBase64_NullText_ReturnsNullImage()
    {
        ValueRef result = await new ImageDecodeBase64Function().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String) },
            CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Image, result.Kind);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task ImageDecodeBase64_PngBytes_ProducesImageWithParsedDimensions()
    {
        string base64 = Convert.ToBase64String(BuildSolidPng(width: 17, height: 23, r: 200, g: 100, b: 50));

        ValueRef result = await new ImageDecodeBase64Function().ExecuteAsync(
            new[] { ValueRef.FromString(base64) },
            CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Image, result.Kind);

        using Arena store = CreateArena();
        DataValue image = result.ToDataValue(store);
        Assert.Equal(DataKind.Image, image.Kind);
        Assert.Equal((ushort)17, image.ImageWidth);
        Assert.Equal((ushort)23, image.ImageHeight);
    }

    [Fact]
    public async Task ImageDecodeBase64_DataUriPrefix_IsStrippedBeforeDecoding()
    {
        string base64 = Convert.ToBase64String(BuildSolidPng(width: 12, height: 9, r: 10, g: 20, b: 30));
        string dataUri = "data:image/png;base64," + base64;

        ValueRef result = await new ImageDecodeBase64Function().ExecuteAsync(
            new[] { ValueRef.FromString(dataUri) },
            CreateEvaluationFrame(), default);

        using Arena store = CreateArena();
        DataValue image = result.ToDataValue(store);
        Assert.Equal(DataKind.Image, image.Kind);
        Assert.Equal((ushort)12, image.ImageWidth);
        Assert.Equal((ushort)9, image.ImageHeight);
    }

    [Fact]
    public async Task ImageDecodeBase64_ValidBase64OfUnknownFormat_StillProducesImageWithZeroMetadata()
    {
        // Once decoded, image-format handling is permissive like image_decode:
        // bytes that aren't a recognised image still yield a non-NULL Image with
        // zero-sentinel metadata; image_width()/image_height() then return NULL.
        byte[] mystery = new byte[256];
        mystery[0] = 0xDE;
        mystery[1] = 0xAD;
        string base64 = Convert.ToBase64String(mystery);

        ValueRef result = await new ImageDecodeBase64Function().ExecuteAsync(
            new[] { ValueRef.FromString(base64) },
            CreateEvaluationFrame(), default);

        Assert.Equal(DataKind.Image, result.Kind);
        Assert.False(result.IsNull);

        using Arena store = CreateArena();
        DataValue image = result.ToDataValue(store);
        Assert.Equal(DataKind.Image, image.Kind);
        Assert.Equal((ushort)0, image.ImageWidth);
        Assert.Equal((ushort)0, image.ImageHeight);
    }

    [Fact]
    public async Task ImageDecodeBase64_InvalidBase64_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new ImageDecodeBase64Function().ExecuteAsync(
                new[] { ValueRef.FromString("not valid base64!!!") },
                CreateEvaluationFrame(), default));
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
}
