using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="ImageTileAggregateFunction"/> — packs images into a
/// fixed canvas with left-to-right flow and row wrap.
/// </summary>
public sealed class ImageTileAggregateFunctionTests : ServiceTestBase
{
    [Fact]
    public async Task Tiles_FlowLeftToRightSameRow_WhenRoomAvailable()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Red), I32(arena, 40), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Green), I32(arena, 40), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Blue), I32(arena, 40), I32(arena, 20)], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(40, decoded.Width);
        Assert.Equal(20, decoded.Height);
        Assert.Equal(SKColors.Red, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, decoded.GetPixel(10, 0));
        Assert.Equal(SKColors.Blue, decoded.GetPixel(20, 0));
        Assert.Equal(0, decoded.GetPixel(35, 0).Alpha);
    }

    [Fact]
    public async Task Wraps_ToNextRow_WhenRowFull()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        // canvas 20x20, each image 10x10 → row 1 = [red, green], row 2 = [blue]
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Red), I32(arena, 20), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Green), I32(arena, 20), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Blue), I32(arena, 20), I32(arena, 20)], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(20, decoded.Width);
        Assert.Equal(20, decoded.Height);
        Assert.Equal(SKColors.Red, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, decoded.GetPixel(10, 0));
        Assert.Equal(SKColors.Blue, decoded.GetPixel(0, 10));
        Assert.Equal(0, decoded.GetPixel(10, 10).Alpha);
    }

    [Fact]
    public async Task Stops_WhenCanvasVerticallyExhausted()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        // canvas 10x20 — fits two 10x10 images stacked; subsequent images dropped.
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Red), I32(arena, 10), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Green), I32(arena, 10), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Blue), I32(arena, 10), I32(arena, 20)], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(SKColors.Red, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, decoded.GetPixel(0, 10));
        // Blue dropped — top-left of its would-be position is transparent? No — it'd go
        // into a third row, but row 3 doesn't exist; nothing is drawn there.
        // The Green image fully covers y=10..19, so we sample y=19 to confirm Green.
        Assert.Equal(SKColors.Green, decoded.GetPixel(0, 19));
    }

    [Fact]
    public async Task SkipsOversizedImages_NarrowerThanCanvasContinue()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        // canvas 20×20; first image is 30 wide → skipped; subsequent 10×10s are placed.
        acc.Accumulate([SolidPng(arena, 30, 10, SKColors.Red), I32(arena, 20), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Green), I32(arena, 20), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Blue), I32(arena, 20), I32(arena, 20)], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(SKColors.Green, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, decoded.GetPixel(10, 0));
    }

    [Fact]
    public async Task CentersShorterImagesWithinRowBand()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        // Row 1 contains a 10×10 red and a 10×4 green; row height = 10, green centred at y=3.
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Red), I32(arena, 20), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 4, SKColors.Green), I32(arena, 20), I32(arena, 20)], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        // Green is 4 tall, row band is 10 tall → top = (10 - 4) / 2 = 3
        Assert.Equal(0, decoded.GetPixel(15, 2).Alpha);
        Assert.Equal(SKColors.Green, decoded.GetPixel(15, 3));
        Assert.Equal(SKColors.Green, decoded.GetPixel(15, 6));
        Assert.Equal(0, decoded.GetPixel(15, 7).Alpha);
    }

    [Fact]
    public async Task Empty_ReturnsNullImage()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task NullImages_AreSkipped()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        acc.Accumulate([DataValue.Null(DataKind.Image), I32(arena, 20), I32(arena, 20)], frame);
        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Red), I32(arena, 20), I32(arena, 20)], frame);
        acc.Accumulate([DataValue.Null(DataKind.Image), I32(arena, 20), I32(arena, 20)], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(20, decoded.Width);
        Assert.Equal(20, decoded.Height);
        Assert.Equal(SKColors.Red, decoded.GetPixel(0, 0));
    }

    [Fact]
    public async Task Merge_ConcatenatesBothSides()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        ImageTileAggregateFunction fn = new();

        IAggregateAccumulator a = fn.CreateAccumulator();
        IAggregateAccumulator b = fn.CreateAccumulator();
        a.Accumulate([SolidPng(arena, 10, 10, SKColors.Red), I32(arena, 20), I32(arena, 20)], frame);
        b.Accumulate([SolidPng(arena, 10, 10, SKColors.Green), I32(arena, 20), I32(arena, 20)], frame);
        b.Accumulate([SolidPng(arena, 10, 10, SKColors.Blue), I32(arena, 20), I32(arena, 20)], frame);

        await a.MergeAsync(b, frame);
        DataValue result = await a.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(SKColors.Red, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.Green, decoded.GetPixel(10, 0));
        Assert.Equal(SKColors.Blue, decoded.GetPixel(0, 10));
    }

    [Fact]
    public async Task Reset_AllowsReuseAcrossGroups()
    {
        Arena arena = CreateArena();
        InvocationFrame frame = InvocationFrame.Symmetric(arena);
        IAggregateAccumulator acc = new ImageTileAggregateFunction().CreateAccumulator();

        acc.Accumulate([SolidPng(arena, 10, 10, SKColors.Red), I32(arena, 40), I32(arena, 20)], frame);
        _ = await acc.ResultAsync(frame);
        acc.Reset();

        acc.Accumulate([SolidPng(arena, 5, 5, SKColors.Green), I32(arena, 10), I32(arena, 10)], frame);
        acc.Accumulate([SolidPng(arena, 5, 5, SKColors.Blue), I32(arena, 10), I32(arena, 10)], frame);

        DataValue result = await acc.ResultAsync(frame);
        using SKBitmap decoded = SKBitmap.Decode(result.AsImage(frame.Target));
        Assert.Equal(10, decoded.Width);
        Assert.Equal(10, decoded.Height);
        Assert.Equal(SKColors.Green, decoded.GetPixel(0, 0));
        Assert.Equal(SKColors.Blue, decoded.GetPixel(5, 0));
    }

    private static DataValue SolidPng(IValueStore store, int width, int height, SKColor color)
    {
        using SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bmp.Erase(color);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return DataValue.FromImage(data.ToArray(), store);
    }

    private static DataValue I32(IValueStore store, int v) => DataValue.FromInt32(v);
}
