namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;

using SkiaSharp;

/// <summary>
/// Tests for the <see cref="ImageHandle"/> class: lazy decode/encode,
/// format propagation, and disposal behavior.
/// </summary>
public sealed class ImageHandleTests
{
    // ───────────────── Helpers ─────────────────

    /// <summary>Creates a solid-color PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // ───────────────── Constructor from bytes ─────────────────

    [Fact]
    public void FromBytes_HasBitmap_IsFalse()
    {
        byte[] png = MakeTestPng(10, 10);
        using ImageHandle handle = new(png, SKEncodedImageFormat.Png);

        Assert.False(handle.HasBitmap);
    }

    [Fact]
    public void FromBytes_GetEncodedBytes_ReturnsSameBytes()
    {
        byte[] png = MakeTestPng(10, 10);
        using ImageHandle handle = new(png, SKEncodedImageFormat.Png);

        Assert.Same(png, handle.GetEncodedBytes());
    }

    [Fact]
    public void FromBytes_GetBitmap_DecodesLazily()
    {
        byte[] png = MakeTestPng(8, 6);
        using ImageHandle handle = new(png, SKEncodedImageFormat.Png);

        Assert.False(handle.HasBitmap);

        SKBitmap bitmap = handle.GetBitmap("test");

        Assert.True(handle.HasBitmap);
        Assert.Equal(8, bitmap.Width);
        Assert.Equal(6, bitmap.Height);
    }

    [Fact]
    public void FromBytes_GetBitmap_ReturnsSameInstanceOnRepeatedCalls()
    {
        byte[] png = MakeTestPng(4, 4);
        using ImageHandle handle = new(png, SKEncodedImageFormat.Png);

        SKBitmap first = handle.GetBitmap("test");
        SKBitmap second = handle.GetBitmap("test");

        Assert.Same(first, second);
    }

    // ───────────────── Constructor from bitmap ─────────────────

    [Fact]
    public void FromBitmap_HasBitmap_IsTrue()
    {
        SKBitmap bitmap = new(10, 10);
        using ImageHandle handle = new(bitmap, SKEncodedImageFormat.Jpeg);

        Assert.True(handle.HasBitmap);
    }

    [Fact]
    public void FromBitmap_GetBitmap_ReturnsSameBitmap()
    {
        SKBitmap bitmap = new(10, 10);
        using ImageHandle handle = new(bitmap, SKEncodedImageFormat.Jpeg);

        Assert.Same(bitmap, handle.GetBitmap("test"));
    }

    [Fact]
    public void FromBitmap_GetEncodedBytes_EncodesLazily()
    {
        SKBitmap bitmap = new(5, 5, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Green);
        using ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);

        byte[] encoded = handle.GetEncodedBytes();

        Assert.NotEmpty(encoded);

        // Verify the encoded bytes are valid PNG by decoding
        using SKBitmap decoded = SKBitmap.Decode(encoded);
        Assert.Equal(5, decoded.Width);
        Assert.Equal(5, decoded.Height);
    }

    [Fact]
    public void FromBitmap_GetEncodedBytes_ReturnsSameInstanceOnRepeatedCalls()
    {
        SKBitmap bitmap = new(4, 4, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Blue);
        using ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);

        byte[] first = handle.GetEncodedBytes();
        byte[] second = handle.GetEncodedBytes();

        Assert.Same(first, second);
    }

    // ───────────────── Format ─────────────────

    [Fact]
    public void Format_PreservesSpecifiedFormat()
    {
        byte[] png = MakeTestPng(4, 4);

        using ImageHandle handlePng = new(png, SKEncodedImageFormat.Png);
        using ImageHandle handleJpeg = new(png, SKEncodedImageFormat.Jpeg);

        Assert.Equal(SKEncodedImageFormat.Png, handlePng.Format);
        Assert.Equal(SKEncodedImageFormat.Jpeg, handleJpeg.Format);
    }

    [Fact]
    public void FromBitmap_GetEncodedBytes_UsesSpecifiedFormat()
    {
        SKBitmap bitmap = new(4, 4, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.White);
        using ImageHandle handle = new(bitmap, SKEncodedImageFormat.Jpeg);

        byte[] encoded = handle.GetEncodedBytes();

        // JPEG starts with FF D8
        Assert.True(encoded.Length >= 2);
        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(0xD8, encoded[1]);
    }

    // ───────────────── Disposal ─────────────────

    [Fact]
    public void Dispose_FromBitmap_PreventsFurtherAccess()
    {
        SKBitmap bitmap = new(4, 4);
        ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);

        handle.Dispose();

        Assert.Throws<ObjectDisposedException>(() => handle.GetBitmap("test"));
        Assert.Throws<ObjectDisposedException>(() => handle.GetEncodedBytes());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        SKBitmap bitmap = new(4, 4);
        ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);

        handle.Dispose();
        handle.Dispose(); // should not throw
    }

    [Fact]
    public void Dispose_FromBytes_PreventsFurtherAccess()
    {
        byte[] png = MakeTestPng(4, 4);
        ImageHandle handle = new(png, SKEncodedImageFormat.Png);

        handle.Dispose();

        Assert.Throws<ObjectDisposedException>(() => handle.GetBitmap("test"));
        Assert.Throws<ObjectDisposedException>(() => handle.GetEncodedBytes());
    }
}
