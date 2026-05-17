using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// <c>image_encode(img, format[, quality])</c> scalar: re-encodes an Image as
/// a byte array in the requested container (JPEG / PNG / WebP).
/// </summary>
public sealed class ImageEncodeFunctionTests : ServiceTestBase
{
    [Fact]
    public void ValidateArguments_TwoArgs_ReturnsUInt8()
    {
        ImageEncodeFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments(
            [DataKind.Image, DataKind.String]);
        Assert.Equal(DataKind.UInt8, kind);
    }

    [Fact]
    public void ValidateArguments_ThreeArgs_ReturnsUInt8()
    {
        ImageEncodeFunction fn = new();
        DataKind kind = ((IScalarFunction)fn).ValidateArguments(
            [DataKind.Image, DataKind.String, DataKind.Int32]);
        Assert.Equal(DataKind.UInt8, kind);
    }

    [Fact]
    public async Task NullImage_ReturnsNullArray()
    {
        ValueRef result = await new ImageEncodeFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image), ValueRef.FromString("png") },
            CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.UInt8, result.Kind);
    }

    [Fact]
    public async Task Png_EncodesWithPngSignature()
    {
        byte[] sourcePng = BuildSolidPng(20, 15, 200, 100, 50);
        ValueRef result = await new ImageEncodeFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromBytes(DataKind.Image, sourcePng),
                ValueRef.FromString("png"),
            },
            CreateEvaluationFrame(), default);

        byte[] encoded = result.AsBytes();
        AssertPngSignature(encoded);

        using SKBitmap decoded = SKBitmap.Decode(encoded);
        Assert.Equal(20, decoded.Width);
        Assert.Equal(15, decoded.Height);
    }

    [Fact]
    public async Task Jpeg_EncodesWithJpegSignature()
    {
        byte[] sourcePng = BuildSolidPng(20, 15, 200, 100, 50);
        ValueRef result = await new ImageEncodeFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromBytes(DataKind.Image, sourcePng),
                ValueRef.FromString("jpeg"),
                ValueRef.FromInt32(85),
            },
            CreateEvaluationFrame(), default);

        byte[] encoded = result.AsBytes();
        AssertJpegSignature(encoded);

        using SKBitmap decoded = SKBitmap.Decode(encoded);
        Assert.Equal(20, decoded.Width);
        Assert.Equal(15, decoded.Height);
    }

    [Fact]
    public async Task JpgAlias_AcceptedSameAsJpeg()
    {
        byte[] sourcePng = BuildSolidPng(10, 10, 0, 200, 0);
        ValueRef result = await new ImageEncodeFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromBytes(DataKind.Image, sourcePng),
                ValueRef.FromString("jpg"),
            },
            CreateEvaluationFrame(), default);

        AssertJpegSignature(result.AsBytes());
    }

    [Fact]
    public async Task Webp_EncodesWithWebpSignature()
    {
        byte[] sourcePng = BuildSolidPng(20, 15, 50, 100, 200);
        ValueRef result = await new ImageEncodeFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromBytes(DataKind.Image, sourcePng),
                ValueRef.FromString("webp"),
                ValueRef.FromInt32(80),
            },
            CreateEvaluationFrame(), default);

        AssertWebpSignature(result.AsBytes());
    }

    [Fact]
    public async Task FormatIsCaseInsensitive()
    {
        byte[] sourcePng = BuildSolidPng(10, 10, 0, 0, 200);
        ValueRef result = await new ImageEncodeFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromBytes(DataKind.Image, sourcePng),
                ValueRef.FromString("JPEG"),
            },
            CreateEvaluationFrame(), default);

        AssertJpegSignature(result.AsBytes());
    }

    [Fact]
    public async Task UnknownFormat_Throws()
    {
        byte[] sourcePng = BuildSolidPng(10, 10, 0, 0, 200);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new ImageEncodeFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromBytes(DataKind.Image, sourcePng),
                    ValueRef.FromString("tiff"),
                },
                CreateEvaluationFrame(), default));
    }

    [Fact]
    public async Task QualityOutOfRange_Throws()
    {
        byte[] sourcePng = BuildSolidPng(10, 10, 0, 0, 200);
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new ImageEncodeFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromBytes(DataKind.Image, sourcePng),
                    ValueRef.FromString("jpeg"),
                    ValueRef.FromInt32(150),
                },
                CreateEvaluationFrame(), default));
    }

    [Fact]
    public async Task JpegQuality_AffectsOutputSize()
    {
        byte[] sourcePng = BuildSolidPng(64, 64, 200, 100, 50);

        ValueRef lowQ = await new ImageEncodeFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromBytes(DataKind.Image, sourcePng),
                ValueRef.FromString("jpeg"),
                ValueRef.FromInt32(5),
            },
            CreateEvaluationFrame(), default);

        ValueRef highQ = await new ImageEncodeFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromBytes(DataKind.Image, sourcePng),
                ValueRef.FromString("jpeg"),
                ValueRef.FromInt32(95),
            },
            CreateEvaluationFrame(), default);

        Assert.True(lowQ.AsBytes().Length < highQ.AsBytes().Length,
            $"expected low-quality JPEG to be smaller; got {lowQ.AsBytes().Length} vs {highQ.AsBytes().Length}.");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static void AssertPngSignature(byte[] bytes)
    {
        Assert.True(bytes.Length >= 8, "PNG output too short.");
        byte[] expected = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(expected, bytes[..8]);
    }

    private static void AssertJpegSignature(byte[] bytes)
    {
        Assert.True(bytes.Length >= 3, "JPEG output too short.");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[2]);
    }

    private static void AssertWebpSignature(byte[] bytes)
    {
        Assert.True(bytes.Length >= 12, "WebP output too short.");
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal((byte)'W', bytes[8]);
        Assert.Equal((byte)'E', bytes[9]);
        Assert.Equal((byte)'B', bytes[10]);
        Assert.Equal((byte)'P', bytes[11]);
    }

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
