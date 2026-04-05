using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Tests for <see cref="ImagePixelMeanFunction"/> and
/// <see cref="ImagePixelStdFunction"/>.
/// </summary>
public sealed class ImagePixelStatisticsFunctionsTests : ServiceTestBase
{
    [Fact]
    public async Task PixelMean_Scalar_AveragesRGBOnly()
    {
        // (100, 150, 200) → (100+150+200)/3 = 150. Alpha (255) excluded.
        ValueRef result = await new ImagePixelMeanFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 100, 150, 200) }, MakeFrame(), default);
        Assert.Equal(150f, result.AsFloat32(), 2);
    }

    [Fact]
    public async Task PixelMean_PerChannel_ReturnsRequestedIndicesInOrder()
    {
        // Channels [2, 0, 1] → [B=200, R=100, G=150].
        ValueRef channels = ValueRef.FromPrimitiveArray(new[] { 2, 0, 1 }, DataKind.Int32);
        ValueRef result = await new ImagePixelMeanFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 100, 150, 200), channels }, MakeFrame(), default);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);

        float[] means = (float[])result.Materialized!;
        Assert.Equal(3, means.Length);
        Assert.Equal(200f, means[0], 2);
        Assert.Equal(100f, means[1], 2);
        Assert.Equal(150f, means[2], 2);
    }

    [Fact]
    public async Task PixelMean_AlphaChannel_Returns255ForOpaqueImage()
    {
        ValueRef channels = ValueRef.FromPrimitiveArray(new[] { 3 }, DataKind.Int32);
        ValueRef result = await new ImagePixelMeanFunction().ExecuteAsync(
            new[] { MakeSolid(2, 2, 0, 0, 0), channels }, MakeFrame(), default);
        float[] means = (float[])result.Materialized!;
        Assert.Equal(255f, means[0], 2);
    }

    [Fact]
    public async Task PixelMean_BadChannelIndex_Throws()
    {
        ValueRef channels = ValueRef.FromPrimitiveArray(new[] { 4 }, DataKind.Int32);
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ImagePixelMeanFunction().ExecuteAsync(
                new[] { MakeSolid(2, 2, 0, 0, 0), channels }, MakeFrame(), default));
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public async Task PixelStd_SolidImage_IsZero()
    {
        ValueRef result = await new ImagePixelStdFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 128, 128, 128) }, MakeFrame(), default);
        Assert.Equal(0f, result.AsFloat32(), 3);
    }

    [Fact]
    public async Task PixelStd_PerChannel_SolidImage_IsZeroPerChannel()
    {
        ValueRef channels = ValueRef.FromPrimitiveArray(new[] { 0, 1, 2 }, DataKind.Int32);
        ValueRef result = await new ImagePixelStdFunction().ExecuteAsync(
            new[] { MakeSolid(4, 4, 50, 100, 200), channels }, MakeFrame(), default);
        float[] stds = (float[])result.Materialized!;
        Assert.Equal(3, stds.Length);
        Assert.Equal(0f, stds[0], 3);
        Assert.Equal(0f, stds[1], 3);
        Assert.Equal(0f, stds[2], 3);
    }

    [Fact]
    public async Task PixelMean_NullImage_ScalarForm_ReturnsNullFloat32()
    {
        ValueRef result = await new ImagePixelMeanFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image) }, MakeFrame(), default);
        Assert.True(result.IsNull);
        Assert.False(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task PixelMean_NullImage_PerChannelForm_ReturnsNullArray()
    {
        ValueRef channels = ValueRef.FromPrimitiveArray(new[] { 0, 1, 2 }, DataKind.Int32);
        ValueRef result = await new ImagePixelMeanFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Image), channels }, MakeFrame(), default);
        Assert.True(result.IsArray);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    private static ValueRef MakeSolid(int w, int h, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return ValueRef.FromImage(bmp);
    }

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }
}
