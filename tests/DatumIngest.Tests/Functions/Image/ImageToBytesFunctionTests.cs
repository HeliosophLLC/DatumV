namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for <see cref="ImageToBytesFunction"/>.
/// </summary>
public sealed class ImageToBytesFunctionTests : ServiceTestBase
{
    private readonly ImageToBytesFunction _function = new();

    /// <summary>Creates a solid-color PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height, SKColor? color = null)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color ?? SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void Name_IsImageToBytes()
    {
        Assert.Equal("image_to_bytes", _function.Name);
    }

    [Fact]
    public void Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.UInt8Array, _function.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Validate_AcceptsUInt8Array()
    {
        Assert.Equal(DataKind.UInt8Array, _function.ValidateArguments([DataKind.UInt8Array]));
    }

    [Fact]
    public void Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([]));
    }

    [Fact]
    public void Validate_WrongType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Execute_ReturnsCorrectLength()
    {
        byte[] png = MakeTestPng(4, 3);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);

        Assert.Equal(DataKind.UInt8Array, result.Kind);
        byte[] data = result.AsUInt8Array();
        Assert.Equal(4 * 3 * 4, data.Length); // W × H × RGBA
    }

    [Fact]
    public void Execute_RedImage_HasCorrectPixelValues()
    {
        byte[] png = MakeTestPng(2, 2);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);
        byte[] data = result.AsUInt8Array();

        // All 4 pixels should be red (255, 0, 0, 255)
        for (int pixel = 0; pixel < 4; pixel++)
        {
            int offset = pixel * 4;
            Assert.Equal(255, data[offset]);     // R
            Assert.Equal(0, data[offset + 1]);   // G
            Assert.Equal(0, data[offset + 2]);   // B
            Assert.Equal(255, data[offset + 3]); // A
        }
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }
}
