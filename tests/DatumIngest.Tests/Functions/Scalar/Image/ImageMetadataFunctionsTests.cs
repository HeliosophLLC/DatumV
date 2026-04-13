using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Direct execution-path tests for the metadata trio
/// (<see cref="ImageChannelsFunction"/>,
/// <see cref="ImagePixelCountFunction"/>,
/// <see cref="ImageDimensionsFunction"/>). Lowering / elision integration
/// is exercised separately in <see cref="ImageMetadataLowererTests"/>.
/// </summary>
public sealed class ImageMetadataFunctionsTests : ServiceTestBase
{
    // ----- image_channels -----

    [Fact]
    public async Task ImageChannels_DecodesBitmapWhenInlineMissing()
    {
        // ValueRef.FromImage(SKBitmap) doesn't stamp inline metadata — the
        // ImageDataValueFactory stamping only happens when crossing the arena
        // boundary. So the fallback decode path fires here.
        ValueRef result = await new ImageChannelsFunction().ExecuteAsync(
            new[] { MakeSolid(8, 8, 100, 100, 100) }, CreateEvaluationFrame(), default);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(4, result.AsInt32()); // RGBA8888 = 4 bytes per pixel
    }

    [Fact]
    public async Task ImageChannels_DeclaresInlineAccessorField()
    {
        Assert.Equal(InlineAccessorField.ImageChannels, new ImageChannelsFunction().Field);
    }

    [Fact]
    public async Task ImageChannels_NullPropagates()
    {
        ValueRef result = await new ImageChannelsFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    // ----- pixel_count (runtime fallback) -----

    [Fact]
    public async Task PixelCount_DirectExecution_ReturnsWidthTimesHeight()
    {
        ValueRef result = await new ImagePixelCountFunction().ExecuteAsync(
            new[] { MakeSolid(32, 24, 0, 0, 0) }, CreateEvaluationFrame(), default);
        Assert.Equal(32 * 24, result.AsInt32());
    }

    [Fact]
    public async Task PixelCount_NullPropagates()
    {
        ValueRef result = await new ImagePixelCountFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    // ----- dimensions (runtime fallback) -----

    [Theory]
    [InlineData("WH",  new[] { 32, 24 })]
    [InlineData("wh",  new[] { 32, 24 })] // case-insensitive
    [InlineData("WHC", new[] { 32, 24, 4 })]
    [InlineData("HWC", new[] { 24, 32, 4 })]
    [InlineData("CHW", new[] { 4, 24, 32 })]
    public async Task Dimensions_KnownFormat_ReturnsOrderedAxisArray(string fmt, int[] expected)
    {
        ValueRef result = await new ImageDimensionsFunction().ExecuteAsync(
            new[] { MakeSolid(32, 24, 0, 0, 0), ValueRef.FromString(fmt) },
            CreateEvaluationFrame(), default);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.True(result.IsArray);
        int[] actual = (int[])result.Materialized!;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Dimensions_UnknownFormat_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ImageDimensionsFunction().ExecuteAsync(
                new[] { MakeSolid(8, 8, 0, 0, 0), ValueRef.FromString("XYZ") },
                CreateEvaluationFrame(), default));
        Assert.Contains("unknown format", ex.Message);
    }

    [Fact]
    public async Task Dimensions_NullImage_ReturnsNullArray()
    {
        ValueRef result = await new ImageDimensionsFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image), ValueRef.FromString("HWC") },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsArray);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    // ----- helpers -----

    private static ValueRef MakeSolid(int w, int h, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return ValueRef.FromImage(bmp);
    }
}
