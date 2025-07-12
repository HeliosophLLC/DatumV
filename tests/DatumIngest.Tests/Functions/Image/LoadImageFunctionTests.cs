namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for <see cref="LoadImageFunction"/>.
/// </summary>
public sealed class LoadImageFunctionTests
{
    private readonly LoadImageFunction _function = new();

    /// <summary>Creates a minimal PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void Name_IsLoadImage()
    {
        Assert.Equal("load_image", _function.Name);
    }

    [Fact]
    public void Validate_AcceptsUInt8Array()
    {
        Assert.Equal(DataKind.Image, _function.ValidateArguments([DataKind.UInt8Array]));
    }

    [Fact]
    public void Validate_RejectsImage()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void Validate_RejectsScalar()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() =>
            _function.ValidateArguments([DataKind.UInt8Array, DataKind.UInt8Array]));
    }

    [Fact]
    public void Execute_ReturnsSameBytesAsImage()
    {
        byte[] png = MakeTestPng(4, 3);

        DataValue result = _function.Execute([DataValue.FromUInt8Array(png)]);

        Assert.Equal(DataKind.Image, result.Kind);
        Assert.False(result.IsNull);
        Assert.Equal(png, result.AsImage());
    }

    [Fact]
    public void Execute_NullInput_ReturnsNullImage()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.UInt8Array)]);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public void Execute_ResultWorksWithImageTransforms()
    {
        byte[] png = MakeTestPng(8, 6);

        DataValue loaded = _function.Execute([DataValue.FromUInt8Array(png)]);

        // The loaded image should be usable by downstream image functions via GetImageHandle.
        ResizeImageFunction resize = new();
        DataValue resized = resize.Execute([
            loaded,
            DataValue.FromScalar(4),
            DataValue.FromScalar(3)
        ]);

        Assert.Equal(DataKind.Image, resized.Kind);
        Assert.False(resized.IsNull);
    }
}
