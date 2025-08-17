namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for <see cref="ImageToTensorHwcFunction"/>.
/// </summary>
public sealed class ImageToTensorHwcFunctionTests
{
    private readonly ImageToTensorHwcFunction _function = new();

    /// <summary>Creates a solid-color PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height, SKColor? color = null)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color ?? SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void Name_IsImageToTensorHwc()
    {
        Assert.Equal("image_to_tensor_hwc", _function.Name);
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
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Execute_ReturnsHwc3Shape()
    {
        byte[] png = MakeTestPng(4, 3);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);

        Assert.Equal(DataKind.Tensor, result.Kind);
        float[] data = result.AsTensor(out int[] shape);
        Assert.Equal([3, 4, 3], shape); // [H=3, W=4, C=3]
        Assert.Equal(3 * 4 * 3, data.Length);
    }

    [Fact]
    public void Execute_RedImage_HasCorrectRgbValues()
    {
        byte[] png = MakeTestPng(2, 2);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);
        float[] data = result.AsTensor(out int[] shape);

        // All 4 pixels should be red (255, 0, 0) — no alpha
        for (int pixel = 0; pixel < 4; pixel++)
        {
            int offset = pixel * 3;
            Assert.Equal(255f, data[offset]);     // R
            Assert.Equal(0f, data[offset + 1]);   // G
            Assert.Equal(0f, data[offset + 2]);   // B
        }
    }

    [Fact]
    public void Execute_BlueImage_HasCorrectRgbValues()
    {
        byte[] png = MakeTestPng(2, 2, SKColors.Blue);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);
        float[] data = result.AsTensor(out int[] shape);

        for (int pixel = 0; pixel < 4; pixel++)
        {
            int offset = pixel * 3;
            Assert.Equal(0f, data[offset]);       // R
            Assert.Equal(0f, data[offset + 1]);   // G
            Assert.Equal(255f, data[offset + 2]); // B
        }
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }
}
