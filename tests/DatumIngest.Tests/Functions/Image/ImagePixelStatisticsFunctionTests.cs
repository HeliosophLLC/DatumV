namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for per-channel pixel statistics functions:
/// <see cref="ImagePixelMeanFunction"/> and <see cref="ImagePixelStandardDeviationFunction"/>.
/// </summary>
public sealed class ImagePixelStatisticsFunctionTests
{
    // ───────────────── Helpers ─────────────────

    /// <summary>Creates a solid-color PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height, SKColor? color = null)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color ?? SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // ───────────────── ImagePixelMeanFunction ─────────────────

    private readonly ImagePixelMeanFunction _pixelMean = new();

    [Fact]
    public void PixelMean_Name()
    {
        Assert.Equal("image_pixel_mean", _pixelMean.Name);
    }

    [Fact]
    public void PixelMean_Validate_OneArg_ReturnsScalar()
    {
        Assert.Equal(DataKind.Scalar, _pixelMean.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void PixelMean_Validate_TwoArgs_ReturnsVector()
    {
        Assert.Equal(DataKind.Vector,
            _pixelMean.ValidateArguments([DataKind.Image, DataKind.Vector]));
    }

    [Fact]
    public void PixelMean_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _pixelMean.ValidateArguments([]));
    }

    [Fact]
    public void PixelMean_Validate_WrongType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _pixelMean.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void PixelMean_Validate_WrongChannelsType_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _pixelMean.ValidateArguments([DataKind.Image, DataKind.Scalar]));
    }

    [Fact]
    public void PixelMean_RedImage_NoChannels_ReturnsOverallMean()
    {
        // Red opaque = (255, 0, 0, 255) → mean = (255 + 0 + 0 + 255) / 4 = 127.5
        byte[] png = MakeTestPng(4, 4, SKColors.Red);
        DataValue result = _pixelMean.Execute([DataValue.FromImage(png)]);

        Assert.InRange(result.AsScalar(), 127f, 128f);
    }

    [Fact]
    public void PixelMean_RedImage_ChannelZero_ReturnsRedMean()
    {
        // Red channel (0) of pure red = 255
        byte[] png = MakeTestPng(4, 4, SKColors.Red);
        float[] channels = [0f]; // R channel
        DataValue result = _pixelMean.Execute(
            [DataValue.FromImage(png), DataValue.FromVector(channels)]);

        float[] means = result.AsVector();
        Assert.Single(means);
        Assert.InRange(means[0], 254f, 255.1f);
    }

    [Fact]
    public void PixelMean_RedImage_MultipleChannels()
    {
        // Red image (255, 0, 0, 255) — request R and G channels
        byte[] png = MakeTestPng(4, 4, SKColors.Red);
        float[] channels = [0f, 1f]; // R, G
        DataValue result = _pixelMean.Execute(
            [DataValue.FromImage(png), DataValue.FromVector(channels)]);

        float[] means = result.AsVector();
        Assert.Equal(2, means.Length);
        Assert.InRange(means[0], 254f, 255.1f); // R = 255
        Assert.InRange(means[1], -0.1f, 0.1f);  // G = 0
    }

    [Fact]
    public void PixelMean_InvalidChannel_Throws()
    {
        byte[] png = MakeTestPng(4, 4, SKColors.Red);
        float[] channels = [5f]; // out of range

        Assert.Throws<ArgumentException>(() =>
            _pixelMean.Execute([DataValue.FromImage(png), DataValue.FromVector(channels)]));
    }

    [Fact]
    public void PixelMean_NullInput_ReturnsNull()
    {
        DataValue result = _pixelMean.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ImagePixelStandardDeviationFunction ─────────────────

    private readonly ImagePixelStandardDeviationFunction _pixelStandardDeviation = new();

    [Fact]
    public void PixelStd_Name()
    {
        Assert.Equal("image_pixel_std", _pixelStandardDeviation.Name);
    }

    [Fact]
    public void PixelStd_Validate_OneArg_ReturnsScalar()
    {
        Assert.Equal(DataKind.Scalar,
            _pixelStandardDeviation.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void PixelStd_Validate_TwoArgs_ReturnsVector()
    {
        Assert.Equal(DataKind.Vector,
            _pixelStandardDeviation.ValidateArguments([DataKind.Image, DataKind.Vector]));
    }

    [Fact]
    public void PixelStd_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _pixelStandardDeviation.ValidateArguments([]));
    }

    [Fact]
    public void PixelStd_SolidColor_NoChannels_ReturnsNonZeroOverall()
    {
        // Red opaque = (255, 0, 0, 255) — channels have different values, so overall std > 0
        byte[] png = MakeTestPng(4, 4, SKColors.Red);
        DataValue result = _pixelStandardDeviation.Execute([DataValue.FromImage(png)]);

        Assert.True(result.AsScalar() > 0f);
    }

    [Fact]
    public void PixelStd_SolidColor_SingleChannel_ReturnsZero()
    {
        // Red channel of pure red → all 255 → std dev = 0
        byte[] png = MakeTestPng(4, 4, SKColors.Red);
        float[] channels = [0f]; // R channel
        DataValue result = _pixelStandardDeviation.Execute(
            [DataValue.FromImage(png), DataValue.FromVector(channels)]);

        float[] standardDeviations = result.AsVector();
        Assert.Single(standardDeviations);
        Assert.InRange(standardDeviations[0], -0.1f, 0.1f);
    }

    [Fact]
    public void PixelStd_NullInput_ReturnsNull()
    {
        DataValue result = _pixelStandardDeviation.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }
}
