using System.Collections.Immutable;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Drawing;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Drawing;

/// <summary>
/// Phase B substrate: Drawing DataKind round-trip + universal rasterizer
/// (<see cref="RenderFunction"/>) for shape, group, transformed, and
/// image-stamp payload variants.
/// </summary>
public sealed class RenderFunctionTests : ServiceTestBase
{
    // ----- ValueRef / boundary -----

    [Fact]
    public void ValueRef_FromDrawing_RoundsTripThroughAsDrawing()
    {
        DrawingPayload payload = new ShapeDrawing(
            ShapeKind.Rectangle,
            Position: new SKPoint(0, 0),
            Size: new SKSize(4, 4),
            Fill: SKColors.Red);
        ValueRef v = ValueRef.FromDrawing(payload);
        Assert.Equal(DataKind.Drawing, v.Kind);
        Assert.Same(payload, v.AsDrawing());
    }

    [Fact]
    public void ValueRef_FromDrawing_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ValueRef.FromDrawing(null!));
    }

    [Fact]
    public void ValueRef_AsDrawing_WrongKind_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ValueRef.FromInt32(0).AsDrawing());
        Assert.Contains("expected Drawing", ex.Message);
    }

    [Fact]
    public void ValueRef_ToDataValue_OnDrawing_Throws()
    {
        ValueRef v = ValueRef.FromDrawing(new ShapeDrawing(
            ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red));
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => v.ToDataValue(arena));
        Assert.Contains("Drawing values cannot be persisted", ex.Message);
    }

    // ----- render: solid shape -----

    [Fact]
    public async Task Render_SolidRectangle_FillsRequestedPixels()
    {
        ValueRef result = await Render(
            new ShapeDrawing(
                ShapeKind.Rectangle,
                Position: new SKPoint(0, 0),
                Size: new SKSize(8, 8),
                Fill: SKColors.Red),
            width: 8, height: 8);

        SKBitmap bmp = result.AsImage();
        Assert.Equal(8, bmp.Width);
        Assert.Equal(8, bmp.Height);
        SKColor px = bmp.GetPixel(4, 4);
        Assert.Equal(255, px.Red);
        Assert.Equal(0, px.Green);
        Assert.Equal(0, px.Blue);
        Assert.Equal(255, px.Alpha);
    }

    [Fact]
    public async Task Render_BackgroundIsTransparent()
    {
        // 8×8 canvas with a 4×4 red square at top-left. The right half should
        // be transparent (alpha = 0).
        ValueRef result = await Render(
            new ShapeDrawing(
                ShapeKind.Rectangle,
                Position: new SKPoint(0, 0),
                Size: new SKSize(4, 4),
                Fill: SKColors.Red),
            width: 8, height: 8);

        SKBitmap bmp = result.AsImage();
        SKColor outsidePixel = bmp.GetPixel(6, 6);
        Assert.Equal(0, outsidePixel.Alpha);
    }

    // ----- render: group / multiple children -----

    [Fact]
    public async Task Render_GroupCompositesChildrenInOrder()
    {
        // Red full-canvas rect, then a green rect on top covering the left half.
        // After rendering, left half should be green, right half red.
        DrawingPayload group = new GroupDrawing(ImmutableArray.Create<DrawingPayload>(
            new ShapeDrawing(ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(8, 8), Fill: SKColors.Red),
            new ShapeDrawing(ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(4, 8), Fill: SKColors.Green)
        ));

        ValueRef result = await Render(group, 8, 8);
        SKBitmap bmp = result.AsImage();

        SKColor leftPx = bmp.GetPixel(1, 4);
        SKColor rightPx = bmp.GetPixel(6, 4);
        Assert.True(leftPx.Green > 100, $"Expected left half green, got {leftPx}");
        Assert.True(rightPx.Red > 100, $"Expected right half red, got {rightPx}");
    }

    // ----- render: transformed -----

    [Fact]
    public async Task Render_TransformedTranslate_MovesGeometry()
    {
        // 2×2 red square at origin, translated by (4, 4). Expect colour at (5, 5)
        // (centre of translated square) and transparency at (1, 1).
        DrawingPayload payload = new TransformedDrawing(
            Inner: new ShapeDrawing(
                ShapeKind.Rectangle,
                Position: new SKPoint(0, 0),
                Size: new SKSize(2, 2),
                Fill: SKColors.Red),
            Translate: new SKPoint(4, 4));

        ValueRef result = await Render(payload, 8, 8);
        SKBitmap bmp = result.AsImage();

        Assert.True(bmp.GetPixel(5, 5).Red > 200);
        Assert.Equal(0, bmp.GetPixel(1, 1).Alpha);
    }

    // ----- render: ellipse -----

    [Fact]
    public async Task Render_Ellipse_CentredAtPosition()
    {
        // 8×8 canvas, ellipse centred at (4, 4) with X-radius 3 and Y-radius 3.
        DrawingPayload payload = new ShapeDrawing(
            ShapeKind.Ellipse,
            Position: new SKPoint(4, 4),
            Size: new SKSize(3, 3),
            Fill: SKColors.Blue);

        ValueRef result = await Render(payload, 8, 8);
        SKBitmap bmp = result.AsImage();

        // Centre of the ellipse is solid blue.
        Assert.True(bmp.GetPixel(4, 4).Blue > 200);
        // Corner of the canvas (outside the ellipse's enclosing circle) is transparent.
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);
    }

    // ----- render: validation -----

    [Fact]
    public async Task Render_NonPositiveSize_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Render(
                new ShapeDrawing(ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(1, 1), Fill: SKColors.Red),
                width: 0, height: 8));
        Assert.Contains("positive", ex.Message);
    }

    [Fact]
    public async Task Render_NullDrawing_ReturnsNullImage()
    {
        var (frame, _) = MakeFrame();
        ValueRef result = await new RenderFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Drawing), MakeSize(4, 4) },
            frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task Render_SiblingTransformsWithOpacity_DoNotLeakState()
    {
        // Regression for a save/restore mismatch: TransformedDrawing's
        // opacity-layer path was pushing two canvas states but only popping
        // one, so sibling transformed drawings inside a group accumulated
        // translation offsets. Visible failure: in draw_particles, fading
        // particles drifted toward the bottom-right after a few frames.
        //
        // Three small dots at x = 4, 16, 28 on a 32×32 canvas, each in its
        // own TransformedDrawing with opacity 0.5. If state leaks, the
        // second and third dots end up to the right of where they should be.
        DrawingPayload Dot(int x)
        {
            return new TransformedDrawing(
                Inner: new ShapeDrawing(
                    ShapeKind.Rectangle,
                    Position: new SKPoint(-1, -1),
                    Size: new SKSize(2, 2),
                    Fill: SKColors.Red),
                Anchor: SKPoint.Empty,
                Translate: new SKPoint(x, 16),
                Opacity: 0.5f);
        }
        DrawingPayload group = new GroupDrawing(
            ImmutableArray.Create(Dot(4), Dot(16), Dot(28)));

        ValueRef result = await Render(group, 32, 32);
        SKBitmap bmp = result.AsImage();

        // Each declared centre should have a non-transparent pixel.
        Assert.True(bmp.GetPixel(4, 16).Alpha > 0, "left dot missing at x=4.");
        Assert.True(bmp.GetPixel(16, 16).Alpha > 0, "middle dot missing at x=16.");
        Assert.True(bmp.GetPixel(28, 16).Alpha > 0, "right dot missing at x=28.");

        // Conversely, nothing should appear between the declared centres
        // (would indicate accumulated offset). Check a few sentinels.
        Assert.Equal(0, bmp.GetPixel(10, 16).Alpha);
        Assert.Equal(0, bmp.GetPixel(22, 16).Alpha);
    }

    // ----- helpers -----

    private async Task<ValueRef> Render(DrawingPayload payload, int width, int height)
    {
        var (frame, _) = MakeFrame();
        return await new RenderFunction().ExecuteAsync(
            new[] { ValueRef.FromDrawing(payload), MakeSize(width, height) },
            frame, default);
    }

    private static ValueRef MakeSize(int w, int h) => ValueRef.FromPoint2D(w, h);

    private (EvaluationFrame Frame, Arena Arena) MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        EvaluationFrame frame = new(
            Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
        return (frame, arena);
    }
}
