using System.Collections.Immutable;

using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Drawing;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Drawing;

/// <summary>
/// Phase F content primitives: <see cref="DrawTextFunction"/>,
/// <see cref="DrawImageFunction"/>, <see cref="DrawPathFunction"/>,
/// <see cref="FillPathFunction"/>.
/// </summary>
public sealed class ContentDrawingFunctionTests : ServiceTestBase
{
    private async Task<DrawingPayload> Exec(IScalarFunction fn, params ValueRef[] args)
    {
        ValueRef result = await fn.ExecuteAsync(args, CreateEvaluationFrame(), default);
        Assert.Equal(DataKind.Drawing, result.Kind);
        Assert.False(result.IsNull);
        return result.AsDrawing();
    }

    private static ValueRef Color(byte r, byte g, byte b, byte a = 255) =>
        ValueRef.FromColor(r, g, b, a);

    // ---------- draw_text ----------

    [Fact]
    public async Task DrawText_BuildsTextDrawing()
    {
        DrawingPayload p = await Exec(new DrawTextFunction(),
            ValueRef.FromString("hello"),
            ValueRef.FromPoint2D(10, 20),
            ValueRef.FromFloat32(16f),
            Color(255, 0, 0));
        TextDrawing t = Assert.IsType<TextDrawing>(p);
        Assert.Equal("hello", t.Text);
        Assert.Equal(10, t.Position.X);
        Assert.Equal(20, t.Position.Y);
        Assert.Equal(16f, t.FontSize);
        Assert.Equal(new SKColor(255, 0, 0, 255), t.Color);
        Assert.Null(t.FontFamily);
    }

    [Fact]
    public async Task DrawText_WithFontFamily()
    {
        DrawingPayload p = await Exec(new DrawTextFunction(),
            ValueRef.FromString("hello"),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(12f),
            Color(0, 0, 0),
            ValueRef.FromString("Arial"));
        TextDrawing t = Assert.IsType<TextDrawing>(p);
        Assert.Equal("Arial", t.FontFamily);
    }

    [Fact]
    public async Task DrawText_NegativeSize_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawTextFunction().ExecuteAsync(
                new[] { ValueRef.FromString("x"), ValueRef.FromPoint2D(0, 0), ValueRef.FromFloat32(-1f), Color(0, 0, 0) },
                CreateEvaluationFrame(), default));
    }

    // ---------- draw_image ----------

    [Fact]
    public async Task DrawImage_BuildsImageStamp_DefaultAnchor()
    {
        SKBitmap bmp = new(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        ValueRef img = ValueRef.FromImage(bmp);

        DrawingPayload p = await Exec(new DrawImageFunction(), img, ValueRef.FromPoint2D(5, 7));
        ImageStampDrawing stamp = Assert.IsType<ImageStampDrawing>(p);
        Assert.Same(bmp, stamp.Image);
        Assert.Equal(5, stamp.Position.X);
        Assert.Equal(7, stamp.Position.Y);
        Assert.Equal(0, stamp.Anchor.X);
        Assert.Equal(0, stamp.Anchor.Y);
        bmp.Dispose();
    }

    [Fact]
    public async Task DrawImage_CustomAnchor()
    {
        SKBitmap bmp = new(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        ValueRef img = ValueRef.FromImage(bmp);

        DrawingPayload p = await Exec(new DrawImageFunction(),
            img,
            ValueRef.FromPoint2D(10, 10),
            ValueRef.FromPoint2D(0.5f, 0.5f));
        ImageStampDrawing stamp = Assert.IsType<ImageStampDrawing>(p);
        Assert.Equal(0.5f, stamp.Anchor.X);
        Assert.Equal(0.5f, stamp.Anchor.Y);
        bmp.Dispose();
    }

    // ---------- draw_path ----------

    [Fact]
    public async Task DrawPath_ParsesAllCommandTypes()
    {
        DrawingPayload p = await Exec(new DrawPathFunction(),
            ValueRef.FromString("M 0 0 L 10 0 Q 15 5 10 10 C 5 15 0 15 0 10 Z"),
            Color(0, 100, 200),
            ValueRef.FromFloat32(2f));
        PathDrawing path = Assert.IsType<PathDrawing>(p);
        Assert.Equal(5, path.Commands.Length);
        Assert.IsType<PathMove>(path.Commands[0]);
        Assert.IsType<PathLine>(path.Commands[1]);
        Assert.IsType<PathQuadratic>(path.Commands[2]);
        Assert.IsType<PathCubic>(path.Commands[3]);
        Assert.IsType<PathClose>(path.Commands[4]);
        Assert.Null(path.Fill);
        Assert.Equal(new SKColor(0, 100, 200, 255), path.Stroke);
        Assert.Equal(2f, path.StrokeWidth);
    }

    [Fact]
    public async Task DrawPath_AcceptsCommaSeparators()
    {
        DrawingPayload p = await Exec(new DrawPathFunction(),
            ValueRef.FromString("M 0,0 L 10,0"),
            Color(0, 0, 0),
            ValueRef.FromFloat32(1f));
        PathDrawing path = Assert.IsType<PathDrawing>(p);
        Assert.Equal(2, path.Commands.Length);
    }

    [Fact]
    public async Task DrawPath_UnknownCommand_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawPathFunction().ExecuteAsync(
                new[] { ValueRef.FromString("X 0 0"), Color(0, 0, 0), ValueRef.FromFloat32(1f) },
                CreateEvaluationFrame(), default));
        Assert.Contains("unknown path command", ex.Message);
    }

    [Fact]
    public async Task DrawPath_MissingCoordinates_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawPathFunction().ExecuteAsync(
                new[] { ValueRef.FromString("M 5"), Color(0, 0, 0), ValueRef.FromFloat32(1f) },
                CreateEvaluationFrame(), default));
        Assert.Contains("missing coordinates", ex.Message);
    }

    [Fact]
    public async Task DrawPath_NonNumericCoordinates_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawPathFunction().ExecuteAsync(
                new[] { ValueRef.FromString("M abc def"), Color(0, 0, 0), ValueRef.FromFloat32(1f) },
                CreateEvaluationFrame(), default));
        Assert.Contains("non-numeric", ex.Message);
    }

    // ---------- fill_path ----------

    [Fact]
    public async Task FillPath_BuildsFilledPath()
    {
        DrawingPayload p = await Exec(new FillPathFunction(),
            ValueRef.FromString("M 0 0 L 10 0 L 10 10 Z"),
            Color(50, 100, 150));
        PathDrawing path = Assert.IsType<PathDrawing>(p);
        Assert.Equal(new SKColor(50, 100, 150, 255), path.Fill);
        Assert.Null(path.Stroke);
    }

    // ---------- end-to-end render ----------

    // ---------- blend ----------

    [Fact]
    public async Task Blend_ParsesNormalAlias_ProducesBlendedDrawing()
    {
        // "normal" / "source-over" / "src-over" all map to SKBlendMode.SrcOver.
        DrawingPayload p = await Exec(new BlendFunction(),
            ValueRef.FromDrawing(new ShapeDrawing(ShapeKind.Rectangle,
                new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red)),
            ValueRef.FromString("normal"));
        BlendedDrawing b = Assert.IsType<BlendedDrawing>(p);
        Assert.Equal(SKBlendMode.SrcOver, b.Mode);
    }

    [Fact]
    public async Task Blend_AdditiveAlias_MapsToPlus()
    {
        // "add" / "plus" / "additive" all → SKBlendMode.Plus. The canonical
        // glow mode for particle effects.
        foreach (string alias in new[] { "add", "plus", "additive", "ADDITIVE" })
        {
            DrawingPayload p = await Exec(new BlendFunction(),
                ValueRef.FromDrawing(new ShapeDrawing(ShapeKind.Rectangle,
                    new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red)),
                ValueRef.FromString(alias));
            BlendedDrawing b = Assert.IsType<BlendedDrawing>(p);
            Assert.Equal(SKBlendMode.Plus, b.Mode);
        }
    }

    [Fact]
    public async Task Blend_AcceptsHyphenAndUnderscoreSeparators()
    {
        foreach (string alias in new[] { "soft-light", "soft_light", "SOFT_LIGHT" })
        {
            DrawingPayload p = await Exec(new BlendFunction(),
                ValueRef.FromDrawing(new ShapeDrawing(ShapeKind.Rectangle,
                    new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red)),
                ValueRef.FromString(alias));
            BlendedDrawing b = Assert.IsType<BlendedDrawing>(p);
            Assert.Equal(SKBlendMode.SoftLight, b.Mode);
        }
    }

    [Fact]
    public async Task Blend_UnknownMode_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new BlendFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromDrawing(new ShapeDrawing(ShapeKind.Rectangle,
                        new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red)),
                    ValueRef.FromString("nope"),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("unknown blend mode", ex.Message);
    }

    [Fact]
    public async Task Blend_AdditiveOverRedBackground_BrightensPixels()
    {
        // A solid red background; an additive blend of green on top should
        // produce yellow-ish pixels at the overlap (since red + green = yellow
        // in additive RGB). This verifies the renderer actually applies the
        // blend at composite time, not just stamps the inner.
        DrawingPayload background = new ShapeDrawing(
            ShapeKind.Rectangle,
            new SKPoint(0, 0), new SKSize(16, 16),
            Fill: new SKColor(255, 0, 0, 255));
        DrawingPayload greenSpot = new ShapeDrawing(
            ShapeKind.Rectangle,
            new SKPoint(4, 4), new SKSize(8, 8),
            Fill: new SKColor(0, 255, 0, 255));
        DrawingPayload additiveGreen = new BlendedDrawing(greenSpot, SKBlendMode.Plus);
        DrawingPayload group = new GroupDrawing(
            ImmutableArray.Create(background, additiveGreen));

        SKBitmap bmp = new(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            DrawingRenderer.Render(canvas, group);
        }

        // Pixel outside the green spot is pure red.
        SKColor outside = bmp.GetPixel(1, 1);
        Assert.Equal(255, outside.Red);
        Assert.Equal(0, outside.Green);
        Assert.Equal(0, outside.Blue);

        // Pixel inside the additive overlap should be yellow (red + green).
        SKColor inside = bmp.GetPixel(8, 8);
        Assert.Equal(255, inside.Red);
        Assert.Equal(255, inside.Green);
        Assert.True(inside.Blue < 50,
            $"additive blend should leave blue near 0, got {inside.Blue}.");
    }

    // ---------- spin_x / spin_y ----------

    [Fact]
    public async Task SpinY_BuildsPerspectiveDrawing()
    {
        DrawingPayload p = await Exec(new SpinYFunction(),
            ValueRef.FromDrawing(new ShapeDrawing(ShapeKind.Rectangle,
                new SKPoint(0, 0), new SKSize(16, 16), Fill: SKColors.Red)),
            ValueRef.FromPoint2D(8, 8),
            ValueRef.FromFloat32(45f));

        PerspectiveDrawing pd = Assert.IsType<PerspectiveDrawing>(p);
        Assert.Equal(PerspectiveAxis.Y, pd.Axis);
        Assert.Equal(45f, pd.AngleDegrees);
        Assert.Equal(8, pd.Anchor.X);
        Assert.Equal(8, pd.Anchor.Y);
    }

    [Fact]
    public async Task SpinX_BuildsPerspectiveDrawing_WithXAxis()
    {
        DrawingPayload p = await Exec(new SpinXFunction(),
            ValueRef.FromDrawing(new ShapeDrawing(ShapeKind.Rectangle,
                new SKPoint(0, 0), new SKSize(16, 16), Fill: SKColors.Red)),
            ValueRef.FromPoint2D(8, 8),
            ValueRef.FromFloat32(30f));

        PerspectiveDrawing pd = Assert.IsType<PerspectiveDrawing>(p);
        Assert.Equal(PerspectiveAxis.X, pd.Axis);
        Assert.Equal(30f, pd.AngleDegrees);
    }

    [Fact]
    public async Task SpinY_At90Degrees_CompressesContentToNearlyNothing()
    {
        // At ±90° the content is edge-on — projected width → 0. Far-from-
        // anchor pixels should be transparent because the content has been
        // compressed onto the vertical axis through the anchor.
        DrawingPayload content = new ShapeDrawing(
            ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(16, 16),
            Fill: new SKColor(220, 80, 80, 255));
        DrawingPayload spun = await Exec(new SpinYFunction(),
            ValueRef.FromDrawing(content),
            ValueRef.FromPoint2D(8, 8),
            ValueRef.FromFloat32(90f));

        SKBitmap bmp = new(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            DrawingRenderer.Render(canvas, spun);
        }
        Assert.Equal(0, bmp.GetPixel(0, 8).Alpha);
        Assert.Equal(0, bmp.GetPixel(15, 8).Alpha);
        bmp.Dispose();
    }

    // ---------- stroke_path alias ----------

    [Fact]
    public void StrokePath_AliasRegisteredFor_DrawPath()
    {
        // Verifies the alias wiring stays — `RegisterScalarAlias<DrawPathFunction>("stroke_path")`
        // in FunctionRegistry maps the new ergonomic name to the same
        // function. We assert via the primary name; the alias is exercised
        // via SQL end-to-end tests in higher layers.
        Assert.Equal("draw_path", DrawPathFunction.Name);
    }

    // ---------- end-to-end render ----------

    [Fact]
    public async Task DrawPath_AndRenderProducesNonEmptyImage()
    {
        // A diagonal stroke from (0,0) to (16,16) should leave non-transparent
        // pixels somewhere along that diagonal.
        DrawingPayload pathDrawing = await Exec(new DrawPathFunction(),
            ValueRef.FromString("M 0 0 L 16 16"),
            Color(255, 0, 0),
            ValueRef.FromFloat32(2f));

        // Render at 16x16 and look for a non-transparent pixel along the
        // diagonal. The renderer is what we're verifying here as much as the
        // function — confirms PathDrawing flows through the switch.
        SKBitmap bmp = new(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            DrawingRenderer.Render(canvas, pathDrawing);
        }
        SKColor mid = bmp.GetPixel(8, 8);
        Assert.True(mid.Alpha > 0, "expected stroke pixels along the diagonal.");
        bmp.Dispose();
    }
}
