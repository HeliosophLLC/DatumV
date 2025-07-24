namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Image;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

using SkiaSharp;

/// <summary>
/// Tests for resolution-aware supplemental Query Unit costing on image functions.
/// Verifies the <see cref="ICostAwareFunction"/> interface, <see cref="ImageCostHelper"/>
/// formula, and end-to-end metering through <see cref="ExpressionEvaluator"/>.
/// </summary>
public sealed class ImageCostAwareTests
{
    // ───────────────── Helpers ─────────────────

    /// <summary>Creates an <see cref="ImageHandle"/>-backed <see cref="DataValue"/> with a decoded bitmap.</summary>
    private static DataValue MakeImageValue(int width, int height)
    {
        SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);
        return DataValue.FromImageHandle(handle);
    }

    /// <summary>Creates a PNG-encoded byte array for a solid red image.</summary>
    private static byte[] MakeTestPng(int width, int height)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    // ───────────────── ICostAwareFunction implementation ─────────────────

    /// <summary>
    /// All pixel-analysis image functions must implement <see cref="ICostAwareFunction"/>.
    /// </summary>
    [Theory]
    [InlineData(typeof(ImageBrightnessMeanFunction))]
    [InlineData(typeof(ImageBrightnessStandardDeviationFunction))]
    [InlineData(typeof(ImageBrightnessHistogramFunction))]
    [InlineData(typeof(ImagePixelMeanFunction))]
    [InlineData(typeof(ImagePixelStandardDeviationFunction))]
    [InlineData(typeof(DetectBlurFunction))]
    [InlineData(typeof(CompressionArtifactScoreFunction))]
    [InlineData(typeof(PerceptualHashFunction))]
    [InlineData(typeof(DecodeImageFunction))]
    public void PixelAnalysisFunction_ImplementsICostAwareFunction(Type functionType)
    {
        object instance = Activator.CreateInstance(functionType)!;

        Assert.IsAssignableFrom<ICostAwareFunction>(instance);
    }

    /// <summary>
    /// All transform image functions must implement <see cref="ICostAwareFunction"/>.
    /// </summary>
    [Theory]
    [InlineData(typeof(ResizeImageFunction))]
    [InlineData(typeof(CropImageFunction))]
    [InlineData(typeof(GrayscaleImageFunction))]
    [InlineData(typeof(RotateImageFunction))]
    [InlineData(typeof(NoiseImageFunction))]
    [InlineData(typeof(BlurImageFunction))]
    [InlineData(typeof(BrightenImageFunction))]
    [InlineData(typeof(DarkenImageFunction))]
    [InlineData(typeof(SobelImageFunction))]
    [InlineData(typeof(ResizeAndCropImageFunction))]
    [InlineData(typeof(AffineTransformFunction))]
    [InlineData(typeof(ElasticDeformFunction))]
    [InlineData(typeof(PerspectiveWarpFunction))]
    public void TransformFunction_ImplementsICostAwareFunction(Type functionType)
    {
        object instance = Activator.CreateInstance(functionType)!;

        Assert.IsAssignableFrom<ICostAwareFunction>(instance);
    }

    /// <summary>
    /// Metadata-only image functions must NOT implement <see cref="ICostAwareFunction"/>
    /// because they read only image headers at fixed cost.
    /// </summary>
    [Theory]
    [InlineData(typeof(ImageWidthFunction))]
    [InlineData(typeof(ImageHeightFunction))]
    [InlineData(typeof(ImageChannelsFunction))]
    [InlineData(typeof(ImagePixelCountFunction))]
    [InlineData(typeof(ImageDimensionsFunction))]
    [InlineData(typeof(LoadImageFunction))]
    public void MetadataFunction_DoesNotImplementICostAwareFunction(Type functionType)
    {
        object instance = Activator.CreateInstance(functionType)!;

        Assert.IsNotAssignableFrom<ICostAwareFunction>(instance);
    }

    // ───────────────── ImageCostHelper formula ─────────────────

    /// <summary>
    /// A small image (below 100,000 pixels) incurs zero supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_SmallImage_ReturnsZero()
    {
        // 224 × 224 = 50,176 pixels < 100,000 → supplemental = 0
        DataValue image = MakeImageValue(224, 224);
        ReadOnlySpan<DataValue> arguments = [image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments);

        Assert.Equal(0, cost);
    }

    /// <summary>
    /// A Full HD image (1920 × 1080 = 2,073,600 pixels) incurs the expected supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_FullHdImage_ReturnsExpectedCost()
    {
        // 1920 × 1080 = 2,073,600 / 100,000 = 20 (integer division)
        DataValue image = MakeImageValue(1920, 1080);
        ReadOnlySpan<DataValue> arguments = [image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments);

        Assert.Equal(20, cost);
    }

    /// <summary>
    /// A 4K image (3840 × 2160 = 8,294,400 pixels) incurs the expected supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_4KImage_ReturnsExpectedCost()
    {
        // 3840 × 2160 = 8,294,400 / 100,000 = 82
        DataValue image = MakeImageValue(3840, 2160);
        ReadOnlySpan<DataValue> arguments = [image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments);

        Assert.Equal(82, cost);
    }

    /// <summary>
    /// When no image argument is present, supplemental cost is zero.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_NoImageArgument_ReturnsZero()
    {
        ReadOnlySpan<DataValue> arguments = [DataValue.FromScalar(42f)];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments);

        Assert.Equal(0, cost);
    }

    /// <summary>
    /// A null image argument is skipped, yielding zero supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_NullImage_ReturnsZero()
    {
        ReadOnlySpan<DataValue> arguments = [DataValue.Null(DataKind.Image)];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments);

        Assert.Equal(0, cost);
    }

    /// <summary>
    /// When multiple arguments are present, the first image argument is used for cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_MultipleArguments_UsesFirstImage()
    {
        DataValue scalar = DataValue.FromScalar(0.5f);
        DataValue image = MakeImageValue(1000, 200);
        // 1000 × 200 = 200,000 / 100,000 = 2
        ReadOnlySpan<DataValue> arguments = [scalar, image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments);

        Assert.Equal(2, cost);
    }

    // ───────────────── End-to-end metering ─────────────────

    /// <summary>
    /// When an image function with <see cref="ICostAwareFunction"/> executes through
    /// <see cref="ExpressionEvaluator"/>, the meter accumulates both the base cost
    /// and the supplemental cost.
    /// </summary>
    [Fact]
    public void Evaluator_ImageFunction_AccumulatesBasePlusSupplementalCost()
    {
        QueryMeter meter = new();
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), meter);

        // Create a 1920×1080 PNG. image_brightness_mean has base cost 10.
        // Supplemental: 2,073,600 / 100,000 = 20. Total expected: 30.
        byte[] png = MakeTestPng(1920, 1080);
        Row row = new(["img"], [DataValue.FromImage(png)]);

        evaluator.Evaluate(
            new FunctionCallExpression("image_brightness_mean", [new ColumnReference("img")]),
            row);

        Assert.Equal(30, meter.FunctionQueryUnits);
    }

    /// <summary>
    /// A small image that yields zero supplemental cost still accumulates the base cost.
    /// </summary>
    [Fact]
    public void Evaluator_SmallImage_OnlyBaseCost()
    {
        QueryMeter meter = new();
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), meter);

        // 32×32 image: 1,024 pixels → supplemental 0. Base cost = 10.
        byte[] png = MakeTestPng(32, 32);
        Row row = new(["img"], [DataValue.FromImage(png)]);

        evaluator.Evaluate(
            new FunctionCallExpression("image_brightness_mean", [new ColumnReference("img")]),
            row);

        Assert.Equal(10, meter.FunctionQueryUnits);
    }
}
