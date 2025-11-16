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
public sealed class ImageCostAwareTests : ServiceTestBase
{
    // ───────────────── Helpers ─────────────────

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
    [InlineData(typeof(ImageToBytesFunction))]
    [InlineData(typeof(ImageToTensorHwcFunction))]
    [InlineData(typeof(ImageToTensorChwFunction))]
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

    private static (DataValue value, Arena arena) MakeArenaImage(int width, int height)
    {
        Arena arena = new();
        DataValue value = DataValue.FromImage(MakeTestPng(width, height), arena);
        return (value, arena);
    }

    /// <summary>
    /// A small image (below 100,000 pixels) incurs zero supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_SmallImage_ReturnsZero()
    {
        // 224 × 224 = 50,176 pixels < 100,000 → supplemental = 0
        (DataValue image, Arena arena) = MakeArenaImage(224, 224);
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        ReadOnlySpan<DataValue> arguments = [image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments, in frame);

        Assert.Equal(0, cost);
    }

    /// <summary>
    /// A Full HD image (1920 × 1080 = 2,073,600 pixels) incurs the expected supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_FullHdImage_ReturnsExpectedCost()
    {
        // 1920 × 1080 = 2,073,600 / 100,000 = 20 (integer division)
        (DataValue image, Arena arena) = MakeArenaImage(1920, 1080);
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        ReadOnlySpan<DataValue> arguments = [image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments, in frame);

        Assert.Equal(20, cost);
    }

    /// <summary>
    /// A 4K image (3840 × 2160 = 8,294,400 pixels) incurs the expected supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_4KImage_ReturnsExpectedCost()
    {
        // 3840 × 2160 = 8,294,400 / 100,000 = 82
        (DataValue image, Arena arena) = MakeArenaImage(3840, 2160);
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        ReadOnlySpan<DataValue> arguments = [image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments, in frame);

        Assert.Equal(82, cost);
    }

    /// <summary>
    /// When no image argument is present, supplemental cost is zero.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_NoImageArgument_ReturnsZero()
    {
        InvocationFrame frame = InvocationFrame.Symmetric(new Arena());
        ReadOnlySpan<DataValue> arguments = [DataValue.FromFloat32(42f)];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments, in frame);

        Assert.Equal(0, cost);
    }

    /// <summary>
    /// A null image argument is skipped, yielding zero supplemental cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_NullImage_ReturnsZero()
    {
        InvocationFrame frame = InvocationFrame.Symmetric(new Arena());
        ReadOnlySpan<DataValue> arguments = [DataValue.Null(DataKind.Image)];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments, in frame);

        Assert.Equal(0, cost);
    }

    /// <summary>
    /// When multiple arguments are present, the first image argument is used for cost.
    /// </summary>
    [Fact]
    public void ComputeSupplementalCost_MultipleArguments_UsesFirstImage()
    {
        DataValue scalar = DataValue.FromFloat32(0.5f);
        (DataValue image, Arena arena) = MakeArenaImage(1000, 200);
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        // 1000 × 200 = 200,000 / 100,000 = 2
        ReadOnlySpan<DataValue> arguments = [scalar, image];

        long cost = ImageCostHelper.ComputeSupplementalCost(arguments, in frame);

        Assert.Equal(2, cost);
    }

    // ───────────────── End-to-end metering ─────────────────

    // Evaluator_ImageFunction_AccumulatesBasePlusSupplementalCost and
    // Evaluator_SmallImage_OnlyBaseCost removed: they evaluated a bare
    // image_brightness_mean(...) call through ExpressionEvaluator without going
    // through the planner's ImagePipelineLowerer. With the legacy Execute path
    // gone, image function calls must be fused at plan time before execution;
    // the e2e cost-metering coverage now lives alongside the lowerer tests.
}
