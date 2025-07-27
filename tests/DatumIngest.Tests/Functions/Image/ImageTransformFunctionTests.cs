namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

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

    // ───────────────── Fused Pipeline (nested calls) ─────────────────

    [Fact]
    public void FusedPipeline_ResizeGrayscale_AvoidsRedundantDecode()
    {
        byte[] png = MakeTestPng(100, 80, SKColors.Blue);

        // Simulate: resize(grayscale(img), 50, 40)
        // grayscale returns an ImageHandle-backed DataValue (no encode yet)
        DataValue grayscaleResult = _grayscale.Execute([DataValue.FromImage(png)]);

        // The result should be ImageHandle-backed (not raw bytes)
        Assert.NotNull(grayscaleResult.TryGetOwnedImageHandle());

        // resize consumes the handle — reuses the bitmap, no second decode
        DataValue resizeResult = _resize.Execute([
            grayscaleResult,
            DataValue.FromScalar(50),
            DataValue.FromScalar(40)
        ]);

        (int width, int height) = DecodeDimensions(resizeResult);
        Assert.Equal(50, width);
        Assert.Equal(40, height);
    }

    [Fact]
    public void FusedPipeline_CropRotate_ProducesCorrectDimensions()
    {
        byte[] png = MakeTestPng(100, 100);

        // Simulate: rotate(crop(img, 10, 10, 50, 50), 90)
        DataValue cropResult = _crop.Execute([
            DataValue.FromImage(png),
            DataValue.FromScalar(10), DataValue.FromScalar(10),
            DataValue.FromScalar(50), DataValue.FromScalar(50)
        ]);

        DataValue rotateResult = _rotate.Execute([
            cropResult,
            DataValue.FromScalar(90)
        ]);

        (int width, int height) = DecodeDimensions(rotateResult);
        Assert.Equal(50, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void FusedPipeline_GrayscaleBlur_PreservesFormat()
    {
        byte[] png = MakeTestPng(20, 20);

        // grayscale(img, 'png') then blur — format should propagate as PNG
        DataValue grayscaleResult = _grayscale.Execute([
            DataValue.FromImage(png),
            DataValue.FromString("png")
        ]);

        DataValue blurResult = _blur.Execute([
            grayscaleResult,
            DataValue.FromScalar(2)
        ]);

        byte[] resultBytes = blurResult.AsImage();
        // PNG starts with 0x89 0x50
        Assert.True(resultBytes.Length >= 2);
        Assert.Equal(0x89, resultBytes[0]);
        Assert.Equal(0x50, resultBytes[1]);
    }

    [Fact]
    public void FusedPipeline_FormatOverride_OuterFunctionWins()
    {
        byte[] png = MakeTestPng(20, 20);

        // grayscale(img, 'png') then resize(..., 'jpeg') — outer JPEG should win
        DataValue grayscaleResult = _grayscale.Execute([
            DataValue.FromImage(png),
            DataValue.FromString("png")
        ]);

        DataValue resizeResult = _resize.Execute([
            grayscaleResult,
            DataValue.FromScalar(10), DataValue.FromScalar(10),
            DataValue.FromString("jpeg")
        ]);

        byte[] resultBytes = resizeResult.AsImage();
        // JPEG starts with 0xFF 0xD8
        Assert.True(resultBytes.Length >= 2);
        Assert.Equal(0xFF, resultBytes[0]);
        Assert.Equal(0xD8, resultBytes[1]);
    }

    [Fact]
    public void FusedPipeline_ThreeDeep_GrayscaleCropResize()
    {
        byte[] png = MakeTestPng(100, 80, SKColors.Red);

        // Simulate: resize(crop(grayscale(img), 0, 0, 50, 40), 25, 20)
        DataValue step1 = _grayscale.Execute([DataValue.FromImage(png)]);
        DataValue step2 = _crop.Execute([
            step1,
            DataValue.FromScalar(0), DataValue.FromScalar(0),
            DataValue.FromScalar(50), DataValue.FromScalar(40)
        ]);
        DataValue step3 = _resize.Execute([
            step2,
            DataValue.FromScalar(25), DataValue.FromScalar(20)
        ]);

        (int width, int height) = DecodeDimensions(step3);
        Assert.Equal(25, width);
        Assert.Equal(20, height);
    }

    [Fact]
    public void FusedPipeline_ImageToTensorHwc_ConsumesHandle()
    {
        byte[] png = MakeTestPng(8, 6, SKColors.Green);
        ImageToTensorHwcFunction tensorHwc = new();

        // Simulate: image_to_tensor_hwc(grayscale(img))
        DataValue grayscaleResult = _grayscale.Execute([DataValue.FromImage(png)]);
        DataValue tensor = tensorHwc.Execute([grayscaleResult]);

        Assert.Equal(DataKind.Tensor, tensor.Kind);
        float[] data = tensor.AsTensor(out int[] shape);
        Assert.Equal(3, shape.Length);
        Assert.Equal(6, shape[0]);  // height
        Assert.Equal(8, shape[1]);  // width
        Assert.Equal(3, shape[2]);  // RGB channels
    }

    [Fact]
    public void FusedPipeline_DisposedHandle_IntermediateCleanup()
    {
        byte[] png = MakeTestPng(20, 20);

        DataValue grayscaleResult = _grayscale.Execute([DataValue.FromImage(png)]);
        ImageHandle? intermediateHandle = grayscaleResult.TryGetOwnedImageHandle();
        Assert.NotNull(intermediateHandle);

        // Simulate what ExpressionEvaluator does: consume then dispose intermediate
        DataValue resizeResult = _resize.Execute([
            grayscaleResult,
            DataValue.FromScalar(10), DataValue.FromScalar(10)
        ]);

        intermediateHandle.Dispose();

        // The result should still be usable — it has its own handle
        (int width, int height) = DecodeDimensions(resizeResult);
        Assert.Equal(10, width);
        Assert.Equal(10, height);
    }
}
