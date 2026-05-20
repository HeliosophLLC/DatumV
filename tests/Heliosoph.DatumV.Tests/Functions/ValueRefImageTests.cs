using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Tests for the <see cref="ValueRef"/> Image surface: <c>FromImage(SKBitmap)</c>,
/// <c>AsImage()</c>, and the <c>ToDataValue</c> encode path.
/// </summary>
public sealed class ValueRefImageTests : ServiceTestBase
{
    private static SKBitmap MakeTestBitmap(int width = 4, int height = 4)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bmp.Erase(SKColors.Blue);
        return bmp;
    }

    private static byte[] MakePngBytes()
    {
        using SKBitmap bmp = MakeTestBitmap();
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ─── FromImage(SKBitmap) ──────────────────────────────────────────────────

    [Fact]
    public void FromImage_SKBitmap_HasImageKind()
    {
        using SKBitmap bmp = MakeTestBitmap();
        ValueRef v = ValueRef.FromImage(bmp);

        Assert.Equal(DataKind.Image, v.Kind);
    }

    [Fact]
    public void FromImage_SKBitmap_IsNotNull()
    {
        using SKBitmap bmp = MakeTestBitmap();
        ValueRef v = ValueRef.FromImage(bmp);

        Assert.False(v.IsNull);
    }

    [Fact]
    public void FromImage_SKBitmap_IsNotArray()
    {
        using SKBitmap bmp = MakeTestBitmap();
        ValueRef v = ValueRef.FromImage(bmp);

        Assert.False(v.IsArray);
    }

    // ─── AsImage() ────────────────────────────────────────────────────────────

    [Fact]
    public void FromImage_SKBitmap_AsImage_ReturnsSameBitmap()
    {
        using SKBitmap bmp = MakeTestBitmap();
        ValueRef v = ValueRef.FromImage(bmp);

        SKBitmap result = v.AsImage();

        Assert.Same(bmp, result);
    }

    [Fact]
    public void FromBytes_Image_AsImage_DecodesLazily()
    {
        byte[] pngBytes = MakePngBytes();
        ValueRef v = ValueRef.FromBytes(DataKind.Image, pngBytes);

        SKBitmap decoded = v.AsImage();

        Assert.NotNull(decoded);
        Assert.Equal(4, decoded.Width);
        Assert.Equal(4, decoded.Height);
    }

    // ─── ToDataValue ──────────────────────────────────────────────────────────

    [Fact]
    public void FromImage_SKBitmap_ToDataValue_ProducesImageDataValue()
    {
        using SKBitmap bmp = MakeTestBitmap();
        ValueRef v = ValueRef.FromImage(bmp);
        var arena = CreateArena();

        DataValue dv = v.ToDataValue(arena);

        Assert.Equal(DataKind.Image, dv.Kind);
        Assert.False(dv.IsNull);
    }

    [Fact]
    public void FromImage_SKBitmap_ToDataValue_BytesAreValidPng()
    {
        using SKBitmap bmp = MakeTestBitmap(8, 8);
        ValueRef v = ValueRef.FromImage(bmp);
        var arena = CreateArena();

        DataValue dv = v.ToDataValue(arena);

        byte[] bytes = dv.AsByteSpan(arena, registry: null).ToArray();
        // PNG magic header: 0x89 0x50 0x4E 0x47
        Assert.True(bytes.Length >= 4);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    // ─── Null ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Null_Image_IsNullAndImageKind()
    {
        ValueRef v = ValueRef.Null(DataKind.Image);

        Assert.True(v.IsNull);
        Assert.Equal(DataKind.Image, v.Kind);
    }
}
