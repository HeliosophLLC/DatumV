namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for <see cref="ImageToTensorChwFunction"/>.
/// </summary>
public sealed class ImageToTensorChwFunctionTests
{
    private readonly ImageToTensorChwFunction _function = new();

    /// <summary>Creates a solid-color PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height, SKColor? color = null)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color ?? SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void Name_IsImageToTensorChw()
    {
        Assert.Equal("image_to_tensor_chw", _function.Name);
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
    public void Execute_ReturnsChw3Shape()
    {
        byte[] png = MakeTestPng(4, 3);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);

        Assert.Equal(DataKind.Tensor, result.Kind);
        float[] data = result.AsTensor(out int[] shape);
        Assert.Equal([3, 3, 4], shape); // [C=3, H=3, W=4]
        Assert.Equal(3 * 3 * 4, data.Length);
    }

    [Fact]
    public void Execute_RedImage_HasChannelPlanarLayout()
    {
        byte[] png = MakeTestPng(2, 2);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);
        float[] data = result.AsTensor(out int[] shape);

        int planeSize = 2 * 2; // 4 pixels

        // R plane: all 255
        for (int i = 0; i < planeSize; i++)
        {
            Assert.Equal(255f, data[i]);
        }

        // G plane: all 0
        for (int i = 0; i < planeSize; i++)
        {
            Assert.Equal(0f, data[planeSize + i]);
        }

        // B plane: all 0
        for (int i = 0; i < planeSize; i++)
        {
            Assert.Equal(0f, data[2 * planeSize + i]);
        }
    }

    [Fact]
    public void Execute_BlueImage_HasCorrectPlanes()
    {
        byte[] png = MakeTestPng(2, 2, SKColors.Blue);

        DataValue result = _function.Execute([DataValue.FromImage(png)]);
        float[] data = result.AsTensor(out int[] shape);

        int planeSize = 2 * 2;

        // R plane: all 0
        for (int i = 0; i < planeSize; i++)
        {
            Assert.Equal(0f, data[i]);
        }

        // G plane: all 0
        for (int i = 0; i < planeSize; i++)
        {
            Assert.Equal(0f, data[planeSize + i]);
        }

        // B plane: all 255
        for (int i = 0; i < planeSize; i++)
        {
            Assert.Equal(255f, data[2 * planeSize + i]);
        }
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }
}
