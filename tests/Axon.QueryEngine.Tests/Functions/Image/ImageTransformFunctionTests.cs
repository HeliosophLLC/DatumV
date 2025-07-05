namespace Axon.QueryEngine.Tests.Functions.Image;

using Axon.QueryEngine.Functions.Image;
using Axon.QueryEngine.Model;

using SkiaSharp;

/// <summary>
/// Tests for image transform functions:
/// <see cref="ResizeImageFunction"/>, <see cref="CropImageFunction"/>,
/// <see cref="GrayscaleImageFunction"/>, <see cref="RotateImageFunction"/>,
/// <see cref="NoiseImageFunction"/>, and <see cref="BlurImageFunction"/>.
/// </summary>
public sealed class ImageTransformFunctionTests
{
    // ───────────────── Helpers ─────────────────

    /// <summary>Creates a solid-color PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height, SKColor? color = null)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color ?? SKColors.Blue);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>Decodes a result image and returns its dimensions.</summary>
    private static (int Width, int Height) DecodeDimensions(DataValue result)
    {
        byte[] bytes = result.AsImage();
        using SKBitmap bitmap = SKBitmap.Decode(bytes);
        return (bitmap.Width, bitmap.Height);
    }

    // ───────────────── ResizeImageFunction ─────────────────

    private readonly ResizeImageFunction _resize = new();

    [Fact]
    public void Resize_Name()
    {
        Assert.Equal("resize", _resize.Name);
    }

    [Fact]
    public void Resize_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _resize.ValidateArguments([DataKind.Image, DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void Resize_Validate_WithFormat()
    {
        Assert.Equal(DataKind.Image,
            _resize.ValidateArguments([DataKind.Image, DataKind.Scalar, DataKind.Scalar, DataKind.String]));
    }

    [Fact]
    public void Resize_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _resize.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Resize_Execute_ChangesSize()
    {
        byte[] png = MakeTestPng(100, 80);

        DataValue result = _resize.Execute([
            DataValue.FromImage(png),
            DataValue.FromScalar(50),
            DataValue.FromScalar(40)
        ]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(50, width);
        Assert.Equal(40, height);
    }

    [Fact]
    public void Resize_NullInput_ReturnsNull()
    {
        DataValue result = _resize.Execute([
            DataValue.Null(DataKind.Image),
            DataValue.FromScalar(50),
            DataValue.FromScalar(40)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── CropImageFunction ─────────────────

    private readonly CropImageFunction _crop = new();

    [Fact]
    public void Crop_Name()
    {
        Assert.Equal("crop", _crop.Name);
    }

    [Fact]
    public void Crop_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _crop.ValidateArguments([
                DataKind.Image, DataKind.Scalar, DataKind.Scalar,
                DataKind.Scalar, DataKind.Scalar
            ]));
    }

    [Fact]
    public void Crop_Execute_ExtractsRegion()
    {
        byte[] png = MakeTestPng(100, 80);

        DataValue result = _crop.Execute([
            DataValue.FromImage(png),
            DataValue.FromScalar(10),  // x
            DataValue.FromScalar(20),  // y
            DataValue.FromScalar(30),  // width
            DataValue.FromScalar(25)   // height
        ]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(30, width);
        Assert.Equal(25, height);
    }

    [Fact]
    public void Crop_NullInput_ReturnsNull()
    {
        DataValue result = _crop.Execute([
            DataValue.Null(DataKind.Image),
            DataValue.FromScalar(0),
            DataValue.FromScalar(0),
            DataValue.FromScalar(10),
            DataValue.FromScalar(10)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── GrayscaleImageFunction ─────────────────

    private readonly GrayscaleImageFunction _grayscale = new();

    [Fact]
    public void Grayscale_Name()
    {
        Assert.Equal("grayscale", _grayscale.Name);
    }

    [Fact]
    public void Grayscale_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Image, _grayscale.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Grayscale_Validate_WithFormat()
    {
        Assert.Equal(DataKind.Image, _grayscale.ValidateArguments([DataKind.Image, DataKind.String]));
    }

    [Fact]
    public void Grayscale_Execute_PreservesDimensions()
    {
        byte[] png = MakeTestPng(50, 30);

        DataValue result = _grayscale.Execute([DataValue.FromImage(png)]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(50, width);
        Assert.Equal(30, height);
    }

    [Fact]
    public void Grayscale_Execute_ConvertsColors()
    {
        byte[] png = MakeTestPng(2, 2, SKColors.Red);

        DataValue result = _grayscale.Execute([DataValue.FromImage(png)]);

        byte[] resultBytes = result.AsImage();
        using SKBitmap bitmap = SKBitmap.Decode(resultBytes);
        SKColor pixel = bitmap.GetPixel(0, 0);

        // After BT.601 grayscale, R=G=B for the output pixel
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Green, pixel.Blue);
    }

    [Fact]
    public void Grayscale_NullInput_ReturnsNull()
    {
        DataValue result = _grayscale.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RotateImageFunction ─────────────────

    private readonly RotateImageFunction _rotate = new();

    [Fact]
    public void Rotate_Name()
    {
        Assert.Equal("rotate", _rotate.Name);
    }

    [Fact]
    public void Rotate_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image, _rotate.ValidateArguments([DataKind.Image, DataKind.Scalar]));
    }

    [Fact]
    public void Rotate_Execute_90Degrees()
    {
        byte[] png = MakeTestPng(100, 60);

        DataValue result = _rotate.Execute([
            DataValue.FromImage(png),
            DataValue.FromScalar(90)
        ]);

        (int width, int height) = DecodeDimensions(result);
        // 90° rotation swaps width and height
        Assert.Equal(60, width);
        Assert.Equal(100, height);
    }

    [Fact]
    public void Rotate_NullInput_ReturnsNull()
    {
        DataValue result = _rotate.Execute([
            DataValue.Null(DataKind.Image),
            DataValue.FromScalar(45)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── NoiseImageFunction ─────────────────

    private readonly NoiseImageFunction _noise = new();

    [Fact]
    public void Noise_Name()
    {
        Assert.Equal("noise", _noise.Name);
    }

    [Fact]
    public void Noise_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _noise.ValidateArguments([DataKind.Image, DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void Noise_Validate_WithFormat()
    {
        Assert.Equal(DataKind.Image,
            _noise.ValidateArguments([DataKind.Image, DataKind.String, DataKind.Scalar, DataKind.String]));
    }

    [Fact]
    public void Noise_Gaussian_PreservesDimensions()
    {
        byte[] png = MakeTestPng(20, 20);

        DataValue result = _noise.Execute([
            DataValue.FromImage(png),
            DataValue.FromString("gaussian"),
            DataValue.FromScalar(10)
        ]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(20, width);
        Assert.Equal(20, height);
    }

    [Fact]
    public void Noise_SaltPepper_PreservesDimensions()
    {
        byte[] png = MakeTestPng(20, 20);

        DataValue result = _noise.Execute([
            DataValue.FromImage(png),
            DataValue.FromString("salt_pepper"),
            DataValue.FromScalar(0.1f)
        ]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(20, width);
        Assert.Equal(20, height);
    }

    [Fact]
    public void Noise_InvalidType_Throws()
    {
        byte[] png = MakeTestPng(10, 10);

        Assert.Throws<ArgumentException>(() =>
            _noise.Execute([
                DataValue.FromImage(png),
                DataValue.FromString("invalid"),
                DataValue.FromScalar(10)
            ]));
    }

    [Fact]
    public void Noise_NullInput_ReturnsNull()
    {
        DataValue result = _noise.Execute([
            DataValue.Null(DataKind.Image),
            DataValue.FromString("gaussian"),
            DataValue.FromScalar(10)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── BlurImageFunction ─────────────────

    private readonly BlurImageFunction _blur = new();

    [Fact]
    public void Blur_Name()
    {
        Assert.Equal("blur", _blur.Name);
    }

    [Fact]
    public void Blur_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image, _blur.ValidateArguments([DataKind.Image, DataKind.Scalar]));
    }

    [Fact]
    public void Blur_Validate_WithFormat()
    {
        Assert.Equal(DataKind.Image,
            _blur.ValidateArguments([DataKind.Image, DataKind.Scalar, DataKind.String]));
    }

    [Fact]
    public void Blur_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _blur.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Blur_Execute_PreservesDimensions()
    {
        byte[] png = MakeTestPng(40, 30);

        DataValue result = _blur.Execute([
            DataValue.FromImage(png),
            DataValue.FromScalar(3)
        ]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(40, width);
        Assert.Equal(30, height);
    }

    [Fact]
    public void Blur_NullInput_ReturnsNull()
    {
        DataValue result = _blur.Execute([
            DataValue.Null(DataKind.Image),
            DataValue.FromScalar(5)
        ]);
        Assert.True(result.IsNull);
    }
}
