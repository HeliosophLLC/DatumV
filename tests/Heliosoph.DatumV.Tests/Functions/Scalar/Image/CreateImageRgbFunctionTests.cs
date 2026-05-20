using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="CreateImageRgbFunction"/> — solid-color Image
/// constructor.
/// </summary>
public sealed class CreateImageRgbFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task ReturnsImageWithRequestedDimensions()
    {
        ValueRef result = await new CreateImageRgbFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromInt32(64),
                ValueRef.FromInt32(32),
                ValueRef.FromInt32(100),
                ValueRef.FromInt32(150),
                ValueRef.FromInt32(200),
            },
            CreateEvaluationFrame(),
            default);

        Assert.Equal(DataKind.Image, result.Kind);
        SKBitmap bmp = result.AsImage();
        Assert.Equal(64, bmp.Width);
        Assert.Equal(32, bmp.Height);
    }

    [Fact]
    public async Task EveryPixelHasRequestedColor()
    {
        ValueRef result = await new CreateImageRgbFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromInt32(8),
                ValueRef.FromInt32(8),
                ValueRef.FromInt32(255),
                ValueRef.FromInt32(0),
                ValueRef.FromInt32(128),
            },
            CreateEvaluationFrame(),
            default);

        SKBitmap bmp = result.AsImage();
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor px = bmp.GetPixel(x, y);
                Assert.Equal(255, px.Red);
                Assert.Equal(0, px.Green);
                Assert.Equal(128, px.Blue);
                Assert.Equal(255, px.Alpha);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveWidth_Throws(int width)
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new CreateImageRgbFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromInt32(width),
                    ValueRef.FromInt32(16),
                    ValueRef.FromInt32(0),
                    ValueRef.FromInt32(0),
                    ValueRef.FromInt32(0),
                },
                CreateEvaluationFrame(),
                default));
        Assert.Contains("width", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task ColorOutOfRange_Throws(int badValue)
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new CreateImageRgbFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromInt32(4),
                    ValueRef.FromInt32(4),
                    ValueRef.FromInt32(badValue),
                    ValueRef.FromInt32(0),
                    ValueRef.FromInt32(0),
                },
                CreateEvaluationFrame(),
                default));
        Assert.Contains("[0, 255]", ex.Message);
    }

    [Fact]
    public async Task NullInput_PropagatesNull()
    {
        ValueRef result = await new CreateImageRgbFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromInt32(4),
                ValueRef.FromInt32(4),
                ValueRef.Null(DataKind.Int32),
                ValueRef.FromInt32(0),
                ValueRef.FromInt32(0),
            },
            CreateEvaluationFrame(),
            default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }
}
