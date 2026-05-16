using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="ImageStackAggregateFunction"/> — aggregate-shape image
/// concatenation along a horizontal or vertical axis.
/// </summary>
public sealed class ImageStackAggregateFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task Horizontal_SumsWidthsTakesMaxHeight()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageStackAggregateFunction().CreateAccumulator();

        acc.Accumulate([SolidPng(arena, 10, 8, SKColors.Red), Str(arena, "horizontal")], frame);
        acc.Accumulate([SolidPng(arena, 20, 4, SKColors.Green), Str(arena, "horizontal")], frame);
        acc.Accumulate([SolidPng(arena, 5, 12, SKColors.Blue), Str(arena, "horizontal")], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(35, decoded.Width);
        Assert.Equal(12, decoded.Height);
    }

    [Fact]
    public async Task Vertical_TakesMaxWidthSumsHeights()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageStackAggregateFunction().CreateAccumulator();

        acc.Accumulate([SolidPng(arena, 10, 8, SKColors.Red), Str(arena, "vertical")], frame);
        acc.Accumulate([SolidPng(arena, 4, 20, SKColors.Green), Str(arena, "vertical")], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(10, decoded.Width);
        Assert.Equal(28, decoded.Height);
    }

    [Fact]
    public async Task Horizontal_BlitsFirstImageAtOrigin()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageStackAggregateFunction().CreateAccumulator();

        acc.Accumulate([SolidPng(arena, 4, 4, SKColors.Red), Str(arena, "horizontal")], frame);
        acc.Accumulate([SolidPng(arena, 4, 4, SKColors.Green), Str(arena, "horizontal")], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(SKColors.Red, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, decoded.GetPixel(4, 0));
    }

    [Fact]
    public void Axis_Invalid_Throws()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageStackAggregateFunction().CreateAccumulator();

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            acc.Accumulate(
                [SolidPng(arena, 4, 4, SKColors.Red), Str(arena, "diagonal")],
                frame));
        Assert.Contains("axis", ex.Message);
    }

    [Fact]
    public async Task Empty_ReturnsNullImage()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageStackAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task NullImages_AreSkipped()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageStackAggregateFunction().CreateAccumulator();

        acc.Accumulate([DataValue.Null(DataKind.Image), Str(arena, "horizontal")], frame);
        acc.Accumulate([SolidPng(arena, 6, 6, SKColors.Red), Str(arena, "horizontal")], frame);
        acc.Accumulate([DataValue.Null(DataKind.Image), Str(arena, "horizontal")], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(6, decoded.Width);
        Assert.Equal(6, decoded.Height);
    }

    [Fact]
    public async Task Merge_ConcatenatesBothSides()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        ImageStackAggregateFunction fn = new();

        IAggregateAccumulator a = fn.CreateAccumulator();
        IAggregateAccumulator b = fn.CreateAccumulator();
        a.Accumulate([SolidPng(arena, 4, 4, SKColors.Red), Str(arena, "horizontal")], frame);
        b.Accumulate([SolidPng(arena, 5, 4, SKColors.Green), Str(arena, "horizontal")], frame);
        b.Accumulate([SolidPng(arena, 3, 4, SKColors.Blue), Str(arena, "horizontal")], frame);

        await a.MergeAsync(b, frame);
        DataValue result = await a.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(12, decoded.Width);
        Assert.Equal(4, decoded.Height);
    }

    [Fact]
    public async Task Reset_AllowsReuseAcrossGroups()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageStackAggregateFunction().CreateAccumulator();

        acc.Accumulate([SolidPng(arena, 10, 4, SKColors.Red), Str(arena, "horizontal")], frame);
        _ = await acc.ResultAsync(frame);
        acc.Reset();

        acc.Accumulate([SolidPng(arena, 3, 3, SKColors.Green), Str(arena, "vertical")], frame);
        acc.Accumulate([SolidPng(arena, 3, 7, SKColors.Blue), Str(arena, "vertical")], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(3, decoded.Width);
        Assert.Equal(10, decoded.Height);
    }

    private static DataValue SolidPng(IValueStore store, int width, int height, SKColor color)
    {
        using SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bmp.Erase(color);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return DataValue.FromImage(data.ToArray(), store);
    }

    private static DataValue Str(IValueStore store, string s) =>
        DataValue.FromString(s, store);
}
