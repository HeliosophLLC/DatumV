namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for image analysis functions:
/// <see cref="ImageBrightnessMeanFunction"/>, <see cref="ImageBrightnessStandardDeviationFunction"/>,
/// <see cref="ImageBrightnessHistogramFunction"/>, <see cref="DetectBlurFunction"/>,
/// and <see cref="CompressionArtifactScoreFunction"/>.
/// </summary>
public sealed class ImageAnalysisFunctionTests
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

    /// <summary>Creates a PNG with a specific pixel pattern for blur testing.</summary>
    private static byte[] MakeCheckerboardPng(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor color = (x + y) % 2 == 0 ? SKColors.White : SKColors.Black;
                bitmap.SetPixel(x, y, color);
            }
        }

        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // ───────────────── ImageBrightnessMeanFunction ─────────────────

    private readonly ImageBrightnessMeanFunction _brightnessMean = new();

    [Fact]
    public void BrightnessMean_Name()
    {
        Assert.Equal("image_brightness_mean", _brightnessMean.Name);
    }

    [Fact]
    public void BrightnessMean_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Float32, _brightnessMean.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void BrightnessMean_Validate_AcceptsUInt8Array()
    {
        Assert.Equal(DataKind.Float32, _brightnessMean.ValidateArguments([DataKind.UInt8Array]));
    }

    [Fact]
    public void BrightnessMean_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _brightnessMean.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() =>
            _brightnessMean.ValidateArguments([DataKind.Image, DataKind.Float32]));
    }

    [Fact]
    public void BrightnessMean_Validate_WrongType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _brightnessMean.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void BrightnessMean_WhiteImage_Returns255()
    {
        byte[] png = MakeTestPng(4, 4, SKColors.White);
        DataValue result = _brightnessMean.Execute([DataValue.FromImage(png)]);

        // White = (255, 255, 255) → luminance ≈ 255
        Assert.InRange(result.AsFloat32(), 254f, 255.1f);
    }

    [Fact]
    public void BrightnessMean_BlackImage_ReturnsZero()
    {
        byte[] png = MakeTestPng(4, 4, SKColors.Black);
        DataValue result = _brightnessMean.Execute([DataValue.FromImage(png)]);

        Assert.InRange(result.AsFloat32(), -0.1f, 0.1f);
    }

    [Fact]
    public void BrightnessMean_NullInput_ReturnsNull()
    {
        DataValue result = _brightnessMean.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ImageBrightnessStandardDeviationFunction ─────────────────

    private readonly ImageBrightnessStandardDeviationFunction _brightnessStandardDeviation = new();

    [Fact]
    public void BrightnessStd_Name()
    {
        Assert.Equal("image_brightness_std", _brightnessStandardDeviation.Name);
    }

    [Fact]
    public void BrightnessStd_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Float32,
            _brightnessStandardDeviation.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void BrightnessStd_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _brightnessStandardDeviation.ValidateArguments([]));
    }

    [Fact]
    public void BrightnessStd_SolidColor_ReturnsZero()
    {
        byte[] png = MakeTestPng(4, 4, SKColors.Blue);
        DataValue result = _brightnessStandardDeviation.Execute([DataValue.FromImage(png)]);

        // All pixels have identical luminance → std dev = 0
        Assert.InRange(result.AsFloat32(), -0.1f, 0.1f);
    }

    [Fact]
    public void BrightnessStd_Checkerboard_ReturnsNonZero()
    {
        byte[] png = MakeCheckerboardPng(8, 8);
        DataValue result = _brightnessStandardDeviation.Execute([DataValue.FromImage(png)]);

        // Black/white checkerboard → high std dev
        Assert.True(result.AsFloat32() > 50f);
    }

    [Fact]
    public void BrightnessStd_NullInput_ReturnsNull()
    {
        DataValue result = _brightnessStandardDeviation.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ImageBrightnessHistogramFunction ─────────────────

    private readonly ImageBrightnessHistogramFunction _brightnessHistogram = new();

    [Fact]
    public void BrightnessHistogram_Name()
    {
        Assert.Equal("image_brightness_histogram", _brightnessHistogram.Name);
    }

    [Fact]
    public void BrightnessHistogram_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Vector, _brightnessHistogram.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void BrightnessHistogram_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _brightnessHistogram.ValidateArguments([]));
    }

    [Fact]
    public void BrightnessHistogram_Returns256Bins()
    {
        byte[] png = MakeTestPng(4, 4, SKColors.Red);
        DataValue result = _brightnessHistogram.Execute([DataValue.FromImage(png)]);

        float[] histogram = result.AsVector();
        Assert.Equal(256, histogram.Length);
    }

    [Fact]
    public void BrightnessHistogram_BlackImage_AllInBinZero()
    {
        byte[] png = MakeTestPng(4, 4, SKColors.Black);
        DataValue result = _brightnessHistogram.Execute([DataValue.FromImage(png)]);

        float[] histogram = result.AsVector();
        Assert.Equal(16f, histogram[0]); // 4×4 = 16 pixels all in bin 0
    }

    [Fact]
    public void BrightnessHistogram_NullInput_ReturnsNull()
    {
        DataValue result = _brightnessHistogram.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── DetectBlurFunction ─────────────────

    private readonly DetectBlurFunction _detectBlur = new();

    [Fact]
    public void DetectBlur_Name()
    {
        Assert.Equal("detect_blur", _detectBlur.Name);
    }

    [Fact]
    public void DetectBlur_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Float32, _detectBlur.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void DetectBlur_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _detectBlur.ValidateArguments([]));
    }

    [Fact]
    public void DetectBlur_SolidColor_ReturnsZero()
    {
        byte[] png = MakeTestPng(10, 10, SKColors.Red);
        DataValue result = _detectBlur.Execute([DataValue.FromImage(png)]);

        // Solid color → no edges → Laplacian variance = 0
        Assert.InRange(result.AsFloat32(), -0.1f, 0.1f);
    }

    [Fact]
    public void DetectBlur_Checkerboard_ReturnsHighValue()
    {
        byte[] png = MakeCheckerboardPng(16, 16);
        DataValue result = _detectBlur.Execute([DataValue.FromImage(png)]);

        // High-frequency pattern → high Laplacian variance
        Assert.True(result.AsFloat32() > 100f);
    }

    [Fact]
    public void DetectBlur_NullInput_ReturnsNull()
    {
        DataValue result = _detectBlur.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── CompressionArtifactScoreFunction ─────────────────

    private readonly CompressionArtifactScoreFunction _compressionArtifactScore = new();

    [Fact]
    public void CompressionArtifactScore_Name()
    {
        Assert.Equal("compression_artifact_score", _compressionArtifactScore.Name);
    }

    [Fact]
    public void CompressionArtifactScore_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Float32,
            _compressionArtifactScore.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void CompressionArtifactScore_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _compressionArtifactScore.ValidateArguments([]));
    }

    [Fact]
    public void CompressionArtifactScore_SolidColor_ReturnsLowScore()
    {
        byte[] png = MakeTestPng(32, 32, SKColors.Green);
        DataValue result = _compressionArtifactScore.Execute([DataValue.FromImage(png)]);

        // Solid color → no block artifacts → score near 0
        Assert.InRange(result.AsFloat32(), 0f, 0.1f);
    }

    [Fact]
    public void CompressionArtifactScore_SmallImage_ReturnsZero()
    {
        byte[] png = MakeTestPng(8, 8, SKColors.Red);
        DataValue result = _compressionArtifactScore.Execute([DataValue.FromImage(png)]);

        // Too small for block analysis
        Assert.InRange(result.AsFloat32(), 0f, 0.01f);
    }

    [Fact]
    public void CompressionArtifactScore_NullInput_ReturnsNull()
    {
        DataValue result = _compressionArtifactScore.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }
}
