namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for extended image transform functions:
/// <see cref="BrightenImageFunction"/>, <see cref="DarkenImageFunction"/>,
/// <see cref="SobelImageFunction"/>, <see cref="ResizeAndCropImageFunction"/>,
/// <see cref="AffineTransformFunction"/>, <see cref="ElasticDeformFunction"/>,
/// and <see cref="PerspectiveWarpFunction"/>.
/// </summary>
public sealed class ImageTransformFunctionExtendedTests
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

    // ───────────────── BrightenImageFunction ─────────────────

    private readonly BrightenImageFunction _brighten = new();

    [Fact]
    public void Brighten_Name()
    {
        Assert.Equal("brighten", _brighten.Name);
    }

    [Fact]
    public void Brighten_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _brighten.ValidateArguments([DataKind.Image, DataKind.Float32]));
    }

    [Fact]
    public void Brighten_Validate_AcceptsOptionalFormat()
    {
        Assert.Equal(DataKind.Image,
            _brighten.ValidateArguments([DataKind.Image, DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void Brighten_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _brighten.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Brighten_ReturnsImage()
    {
        byte[] png = MakeTestPng(8, 8, SKColors.Gray);
        DataValue result = _brighten.Execute(
            [DataValue.FromImage(png), DataValue.FromFloat32(50f)]);

        Assert.Equal(DataKind.Image, result.Kind);
        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(8, width);
        Assert.Equal(8, height);
    }

    [Fact]
    public void Brighten_NullInput_ReturnsNull()
    {
        DataValue result = _brighten.Execute(
            [DataValue.Null(DataKind.Image), DataValue.FromFloat32(50f)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── DarkenImageFunction ─────────────────

    private readonly DarkenImageFunction _darken = new();

    [Fact]
    public void Darken_Name()
    {
        Assert.Equal("darken", _darken.Name);
    }

    [Fact]
    public void Darken_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _darken.ValidateArguments([DataKind.Image, DataKind.Float32]));
    }

    [Fact]
    public void Darken_Validate_AcceptsOptionalFormat()
    {
        Assert.Equal(DataKind.Image,
            _darken.ValidateArguments([DataKind.Image, DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void Darken_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _darken.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Darken_ReturnsImage()
    {
        byte[] png = MakeTestPng(8, 8, SKColors.Gray);
        DataValue result = _darken.Execute(
            [DataValue.FromImage(png), DataValue.FromFloat32(50f)]);

        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public void Darken_NullInput_ReturnsNull()
    {
        DataValue result = _darken.Execute(
            [DataValue.Null(DataKind.Image), DataValue.FromFloat32(50f)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── SobelImageFunction ─────────────────

    private readonly SobelImageFunction _sobel = new();

    [Fact]
    public void Sobel_Name()
    {
        Assert.Equal("sobel", _sobel.Name);
    }

    [Fact]
    public void Sobel_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Image, _sobel.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Sobel_Validate_AcceptsOptionalFormat()
    {
        Assert.Equal(DataKind.Image,
            _sobel.ValidateArguments([DataKind.Image, DataKind.String]));
    }

    [Fact]
    public void Sobel_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _sobel.ValidateArguments([]));
    }

    [Fact]
    public void Sobel_PreservesSize()
    {
        byte[] png = MakeTestPng(16, 16);
        DataValue result = _sobel.Execute([DataValue.FromImage(png)]);

        Assert.Equal(DataKind.Image, result.Kind);
        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(16, width);
        Assert.Equal(16, height);
    }

    [Fact]
    public void Sobel_NullInput_ReturnsNull()
    {
        DataValue result = _sobel.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ResizeAndCropImageFunction ─────────────────

    private readonly ResizeAndCropImageFunction _resizeAndCrop = new();

    [Fact]
    public void ResizeAndCrop_Name()
    {
        Assert.Equal("resize_and_crop", _resizeAndCrop.Name);
    }

    [Fact]
    public void ResizeAndCrop_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _resizeAndCrop.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void ResizeAndCrop_Validate_AcceptsOptionalFormat()
    {
        Assert.Equal(DataKind.Image,
            _resizeAndCrop.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.Float32, DataKind.String, DataKind.String]));
    }

    [Fact]
    public void ResizeAndCrop_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _resizeAndCrop.ValidateArguments([DataKind.Image, DataKind.Float32]));
    }

    [Fact]
    public void ResizeAndCrop_CenterGravity_ProducesExactDimensions()
    {
        byte[] png = MakeTestPng(100, 80);
        DataValue result = _resizeAndCrop.Execute(
            [DataValue.FromImage(png), DataValue.FromFloat32(50f), DataValue.FromFloat32(50f),
             DataValue.FromString("center")]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(50, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void ResizeAndCrop_TopGravity_ProducesExactDimensions()
    {
        byte[] png = MakeTestPng(100, 80);
        DataValue result = _resizeAndCrop.Execute(
            [DataValue.FromImage(png), DataValue.FromFloat32(40f), DataValue.FromFloat32(40f),
             DataValue.FromString("top")]);

        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(40, width);
        Assert.Equal(40, height);
    }

    [Fact]
    public void ResizeAndCrop_InvalidGravity_Throws()
    {
        byte[] png = MakeTestPng(100, 80);

        Assert.Throws<ArgumentException>(() =>
            _resizeAndCrop.Execute(
                [DataValue.FromImage(png), DataValue.FromFloat32(50f), DataValue.FromFloat32(50f),
                 DataValue.FromString("invalid")]));
    }

    [Fact]
    public void ResizeAndCrop_NullInput_ReturnsNull()
    {
        DataValue result = _resizeAndCrop.Execute(
            [DataValue.Null(DataKind.Image), DataValue.FromFloat32(50f),
             DataValue.FromFloat32(50f), DataValue.FromString("center")]);
        Assert.True(result.IsNull);
    }

    // ───────────────── AffineTransformFunction ─────────────────

    private readonly AffineTransformFunction _affineTransform = new();

    [Fact]
    public void AffineTransform_Name()
    {
        Assert.Equal("affine_transform", _affineTransform.Name);
    }

    [Fact]
    public void AffineTransform_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _affineTransform.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.Float32,
                 DataKind.Float32, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void AffineTransform_Validate_AcceptsOptionalFormat()
    {
        Assert.Equal(DataKind.Image,
            _affineTransform.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.Float32,
                 DataKind.Float32, DataKind.Float32, DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void AffineTransform_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _affineTransform.ValidateArguments([DataKind.Image, DataKind.Float32]));
    }

    [Fact]
    public void AffineTransform_IdentityTransform_PreservesSize()
    {
        byte[] png = MakeTestPng(16, 16);
        // angle=0, scale_x=1, scale_y=1, shear_x=0, shear_y=0 (identity)
        DataValue result = _affineTransform.Execute(
            [DataValue.FromImage(png), DataValue.FromFloat32(0f),
             DataValue.FromFloat32(1f), DataValue.FromFloat32(1f),
             DataValue.FromFloat32(0f), DataValue.FromFloat32(0f)]);

        Assert.Equal(DataKind.Image, result.Kind);
        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(16, width);
        Assert.Equal(16, height);
    }

    [Fact]
    public void AffineTransform_NullInput_ReturnsNull()
    {
        DataValue result = _affineTransform.Execute(
            [DataValue.Null(DataKind.Image), DataValue.FromFloat32(0f),
             DataValue.FromFloat32(1f), DataValue.FromFloat32(1f),
             DataValue.FromFloat32(0f), DataValue.FromFloat32(0f)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ElasticDeformFunction ─────────────────

    private readonly ElasticDeformFunction _elasticDeform = new();

    [Fact]
    public void ElasticDeform_Name()
    {
        Assert.Equal("elastic_deform", _elasticDeform.Name);
    }

    [Fact]
    public void ElasticDeform_Validate_AcceptsCorrectArgs()
    {
        Assert.Equal(DataKind.Image,
            _elasticDeform.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void ElasticDeform_Validate_AcceptsOptionalFormat()
    {
        Assert.Equal(DataKind.Image,
            _elasticDeform.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void ElasticDeform_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _elasticDeform.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void ElasticDeform_PreservesSize()
    {
        byte[] png = MakeTestPng(16, 16);
        DataValue result = _elasticDeform.Execute(
            [DataValue.FromImage(png), DataValue.FromFloat32(10f), DataValue.FromFloat32(3f)]);

        Assert.Equal(DataKind.Image, result.Kind);
        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(16, width);
        Assert.Equal(16, height);
    }

    [Fact]
    public void ElasticDeform_NullInput_ReturnsNull()
    {
        DataValue result = _elasticDeform.Execute(
            [DataValue.Null(DataKind.Image), DataValue.FromFloat32(10f), DataValue.FromFloat32(3f)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── PerspectiveWarpFunction ─────────────────

    private readonly PerspectiveWarpFunction _perspectiveWarp = new();

    [Fact]
    public void PerspectiveWarp_Name()
    {
        Assert.Equal("perspective_warp", _perspectiveWarp.Name);
    }

    [Fact]
    public void PerspectiveWarp_Validate_AcceptsIntensityMode()
    {
        Assert.Equal(DataKind.Image,
            _perspectiveWarp.ValidateArguments([DataKind.Image, DataKind.Float32]));
    }

    [Fact]
    public void PerspectiveWarp_Validate_AcceptsIntensityModeWithFormat()
    {
        Assert.Equal(DataKind.Image,
            _perspectiveWarp.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void PerspectiveWarp_Validate_AcceptsExplicitCorners()
    {
        DataKind[] nineArgs =
            [DataKind.Image, DataKind.Float32, DataKind.Float32, DataKind.Float32,
             DataKind.Float32, DataKind.Float32, DataKind.Float32, DataKind.Float32, DataKind.Float32];

        Assert.Equal(DataKind.Image, _perspectiveWarp.ValidateArguments(nineArgs));
    }

    [Fact]
    public void PerspectiveWarp_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _perspectiveWarp.ValidateArguments(
                [DataKind.Image, DataKind.Float32, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void PerspectiveWarp_IntensityMode_PreservesSize()
    {
        byte[] png = MakeTestPng(16, 16);
        DataValue result = _perspectiveWarp.Execute(
            [DataValue.FromImage(png), DataValue.FromFloat32(0.1f)]);

        Assert.Equal(DataKind.Image, result.Kind);
        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(16, width);
        Assert.Equal(16, height);
    }

    [Fact]
    public void PerspectiveWarp_ExplicitCorners_PreservesSize()
    {
        byte[] png = MakeTestPng(16, 16);
        // Corners at normalized coordinates (identity ~ 0,0 / 1,0 / 0,1 / 1,1)
        DataValue result = _perspectiveWarp.Execute(
            [DataValue.FromImage(png),
             DataValue.FromFloat32(0f), DataValue.FromFloat32(0f),     // top-left
             DataValue.FromFloat32(1f), DataValue.FromFloat32(0f),     // top-right
             DataValue.FromFloat32(0f), DataValue.FromFloat32(1f),     // bottom-left
             DataValue.FromFloat32(1f), DataValue.FromFloat32(1f)]);   // bottom-right

        Assert.Equal(DataKind.Image, result.Kind);
        (int width, int height) = DecodeDimensions(result);
        Assert.Equal(16, width);
        Assert.Equal(16, height);
    }

    [Fact]
    public void PerspectiveWarp_NullInput_ReturnsNull()
    {
        DataValue result = _perspectiveWarp.Execute(
            [DataValue.Null(DataKind.Image), DataValue.FromFloat32(0.1f)]);
        Assert.True(result.IsNull);
    }
}
