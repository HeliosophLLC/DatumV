namespace DatumQuery.Tests.Functions.Image;

using DatumQuery.Functions.Image;
using DatumQuery.Model;

using SkiaSharp;

/// <summary>
/// Tests for <see cref="DecodeImageFunction"/>.
/// </summary>
public sealed class DecodeImageFunctionTests
{
    private readonly DecodeImageFunction _function = new();

    /// <summary>Creates a 4×3 red PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void Name_IsDecodeImage()
    {
        Assert.Equal("decode_image", _function.Name);
    }

    [Fact]
    public void Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Tensor, _function.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Validate_AcceptsUInt8Array()
    {
        Assert.Equal(DataKind.Tensor, _function.ValidateArguments([DataKind.UInt8Array]));
    }

    [Fact]
    public void Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([]));
    }

    [Fact]
    public void Validate_WrongType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Execute_ReturnsCorrectShape()
    {
        byte[] png = MakeTestPng(4, 3);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);

        Assert.Equal(DataKind.Tensor, result.Kind);
        float[] data = result.AsTensor(out int[] shape);
        Assert.Equal([3, 4, 4], shape); // [H, W, C=4 RGBA]
        Assert.Equal(3 * 4 * 4, data.Length);
    }

    [Fact]
    public void Execute_RedImage_HasCorrectPixelValues()
    {
        byte[] png = MakeTestPng(2, 2);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);
        float[] data = result.AsTensor(out int[] shape);

        // All 4 pixels should be red (255, 0, 0, 255)
        for (int pixel = 0; pixel < 4; pixel++)
        {
            int offset = pixel * 4;
            Assert.Equal(255f, data[offset]);     // R
            Assert.Equal(0f, data[offset + 1]);   // G
            Assert.Equal(0f, data[offset + 2]);   // B
            Assert.Equal(255f, data[offset + 3]); // A
        }
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }
}
