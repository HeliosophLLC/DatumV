using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ApplyColormapFunction"/>. Exercises each shipped
/// palette plus null-handling, dimension preservation, and the
/// unknown-palette error path.
/// </summary>
public sealed class ApplyColormapFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("apply_colormap", ApplyColormapFunction.Name);
        Assert.Equal(FunctionCategory.Image, ApplyColormapFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ApplyColormapFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsImageAndString()
    {
        DataKind kind = new ApplyColormapFunction()
            .ValidateArguments([DataKind.Image, DataKind.String]);
        Assert.Equal(DataKind.Image, kind);
    }

    [Fact]
    public void AvailablePalettes_IncludesTurboJetGray()
    {
        Assert.Contains("turbo", ApplyColormapFunction.AvailablePalettes);
        Assert.Contains("jet", ApplyColormapFunction.AvailablePalettes);
        Assert.Contains("gray", ApplyColormapFunction.AvailablePalettes);
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("jet")]
    [InlineData("gray")]
    [InlineData("TURBO")]   // case-insensitive
    public async Task Execute_ReturnsImageWithSameDimensions(string palette)
    {
        using SKBitmap source = MakeRamp(width: 64, height: 16);

        ValueRef result = await new ApplyColormapFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromString(palette) },
            CreateEvaluationFrame(),
            default);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);

        SKBitmap coloured = result.AsImage();
        Assert.Equal(64, coloured.Width);
        Assert.Equal(16, coloured.Height);
    }

    [Fact]
    public async Task Execute_GrayPalette_RoundTripsRedChannel()
    {
        // gray palette is the identity — for any input where R = G = B,
        // the output should match the source pixel-for-pixel.
        using SKBitmap source = MakeRamp(width: 256, height: 1);

        ValueRef result = await new ApplyColormapFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromString("gray") },
            CreateEvaluationFrame(),
            default);

        SKBitmap output = result.AsImage();
        for (int x = 0; x < 256; x++)
        {
            SKColor c = output.GetPixel(x, 0);
            Assert.Equal((byte)x, c.Red);
            Assert.Equal((byte)x, c.Green);
            Assert.Equal((byte)x, c.Blue);
            Assert.Equal((byte)255, c.Alpha);
        }
    }

    [Fact]
    public async Task Execute_TurboPalette_BlueAtLow_RedAtHigh()
    {
        // Turbo's defining property: low values are blue (R≪B), high values
        // are red (R≫B), the middle is green-ish. Mikhailov's degree-5
        // polynomial diverges slightly from the canonical LUT at the very
        // endpoints (t=0 is a dark-magenta mix in the polynomial vs pure
        // dark blue in the LUT), so we anchor the test slightly inboard
        // of the endpoints where the polynomial and LUT agree.
        using SKBitmap source = MakeRamp(width: 256, height: 1);

        ValueRef result = await new ApplyColormapFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromString("turbo") },
            CreateEvaluationFrame(),
            default);

        SKBitmap output = result.AsImage();
        SKColor low = output.GetPixel(51, 0);    // t ≈ 0.2 — clearly blue territory
        SKColor high = output.GetPixel(204, 0);  // t ≈ 0.8 — clearly red territory
        SKColor mid = output.GetPixel(128, 0);   // t = 0.5 — green-ish

        Assert.True(low.Blue > low.Red, $"low: blue={low.Blue} should exceed red={low.Red}");
        Assert.True(high.Red > high.Blue, $"high: red={high.Red} should exceed blue={high.Blue}");
        // Middle of turbo is green-ish — green should outpace both red and blue.
        Assert.True(mid.Green >= mid.Red && mid.Green >= mid.Blue,
            $"mid: green={mid.Green} should match or beat red={mid.Red}, blue={mid.Blue}");

        // Output is fully opaque regardless of input alpha.
        Assert.Equal((byte)255, low.Alpha);
        Assert.Equal((byte)255, high.Alpha);
    }

    [Fact]
    public async Task Execute_JetPalette_StandardRainbow()
    {
        // Jet's anchor properties: t=0 → blue, t=1 → red, t≈0.5 → green.
        using SKBitmap source = MakeRamp(width: 256, height: 1);

        ValueRef result = await new ApplyColormapFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromString("jet") },
            CreateEvaluationFrame(),
            default);

        SKBitmap output = result.AsImage();
        SKColor low = output.GetPixel(0, 0);
        SKColor high = output.GetPixel(255, 0);
        SKColor mid = output.GetPixel(128, 0);

        // Jet at t=0: pure blue (R=G=0, B≈128 — half-ramp).
        Assert.Equal((byte)0, low.Red);
        Assert.Equal((byte)0, low.Green);
        Assert.True(low.Blue > 0);

        // Jet at t=1: pure red (R≈128, G=B=0).
        Assert.True(high.Red > 0);
        Assert.Equal((byte)0, high.Green);
        Assert.Equal((byte)0, high.Blue);

        // Jet at t≈0.5: pure green (G≈255, R=B near 0).
        Assert.True(mid.Green > mid.Red);
        Assert.True(mid.Green > mid.Blue);
    }

    [Fact]
    public async Task Execute_UnknownPalette_Throws()
    {
        using SKBitmap source = MakeRamp(width: 4, height: 4);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ApplyColormapFunction().ExecuteAsync(
                new ValueRef[] { ValueRef.FromImage(source), ValueRef.FromString("not_a_palette") },
                CreateEvaluationFrame(),
                default));
        Assert.Contains("not_a_palette", ex.Message);
        Assert.Contains("turbo", ex.Message);
    }

    [Fact]
    public async Task Execute_NullImage_ReturnsNullImage()
    {
        ValueRef result = await new ApplyColormapFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromString("turbo") },
            CreateEvaluationFrame(),
            default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task Execute_NullPalette_ReturnsNullImage()
    {
        using SKBitmap source = MakeRamp(width: 4, height: 4);
        ValueRef result = await new ApplyColormapFunction().ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(source), ValueRef.Null(DataKind.String) },
            CreateEvaluationFrame(),
            default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    /// <summary>
    /// Builds a width-pixel-wide horizontal grayscale ramp:
    /// pixel (x, y).R = (byte)x mod 256. Mirrors what a depth/mask emitter
    /// produces — single-channel data with R = G = B.
    /// </summary>
    private static SKBitmap MakeRamp(int width, int height)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = (byte)(x % 256);
                bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        }
        return bmp;
    }
}
