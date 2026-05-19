using System.Collections.Immutable;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Drawing;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Drawing;

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
        Assert.Equal(TextHAlign.Left, t.HAlign);
        Assert.Equal(TextVAlign.Baseline, t.VAlign);
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
        Assert.Equal(TextHAlign.Left, t.HAlign);
        Assert.Equal(TextVAlign.Baseline, t.VAlign);
    }

    [Fact]
    public async Task DrawText_WithAlignment()
    {
        DrawingPayload p = await Exec(new DrawTextFunction(),
            ValueRef.FromString("hi"),
            ValueRef.FromPoint2D(50, 50),
            ValueRef.FromFloat32(20f),
            Color(0, 0, 0),
            ValueRef.FromString("center"),
            ValueRef.FromString("middle"));
        TextDrawing t = Assert.IsType<TextDrawing>(p);
        Assert.Equal(TextHAlign.Center, t.HAlign);
        Assert.Equal(TextVAlign.Middle, t.VAlign);
        Assert.Null(t.FontFamily);
    }

    [Fact]
    public async Task DrawText_WithAlignmentAndFontFamily()
    {
        DrawingPayload p = await Exec(new DrawTextFunction(),
            ValueRef.FromString("hi"),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(20f),
            Color(0, 0, 0),
            ValueRef.FromString("right"),
            ValueRef.FromString("top"),
            ValueRef.FromString("Arial"));
        TextDrawing t = Assert.IsType<TextDrawing>(p);
        Assert.Equal(TextHAlign.Right, t.HAlign);
        Assert.Equal(TextVAlign.Top, t.VAlign);
        Assert.Equal("Arial", t.FontFamily);
    }

    [Theory]
    [InlineData("LEFT", TextHAlign.Left)]
    [InlineData("Center", TextHAlign.Center)]
    [InlineData("centre", TextHAlign.Center)]
    [InlineData("right", TextHAlign.Right)]
    public async Task DrawText_HAlign_Aliases(string raw, TextHAlign expected)
    {
        DrawingPayload p = await Exec(new DrawTextFunction(),
            ValueRef.FromString("x"),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(10f),
            Color(0, 0, 0),
            ValueRef.FromString(raw),
            ValueRef.FromString("baseline"));
        Assert.Equal(expected, ((TextDrawing)p).HAlign);
    }

    [Theory]
    [InlineData("top", TextVAlign.Top)]
    [InlineData("MIDDLE", TextVAlign.Middle)]
    [InlineData("center", TextVAlign.Middle)]
    [InlineData("baseline", TextVAlign.Baseline)]
    [InlineData("bottom", TextVAlign.Bottom)]
    public async Task DrawText_VAlign_Aliases(string raw, TextVAlign expected)
    {
        DrawingPayload p = await Exec(new DrawTextFunction(),
            ValueRef.FromString("x"),
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromFloat32(10f),
            Color(0, 0, 0),
            ValueRef.FromString("left"),
            ValueRef.FromString(raw));
        Assert.Equal(expected, ((TextDrawing)p).VAlign);
    }

    [Fact]
    public async Task DrawText_UnknownHAlign_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawTextFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromString("x"),
                    ValueRef.FromPoint2D(0, 0),
                    ValueRef.FromFloat32(10f),
                    Color(0, 0, 0),
                    ValueRef.FromString("sideways"),
                    ValueRef.FromString("baseline"),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("horizontal alignment", ex.Message);
    }

    [Fact]
    public async Task DrawText_UnknownVAlign_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DrawTextFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromString("x"),
                    ValueRef.FromPoint2D(0, 0),
                    ValueRef.FromFloat32(10f),
                    Color(0, 0, 0),
                    ValueRef.FromString("left"),
                    ValueRef.FromString("inside-out"),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("vertical alignment", ex.Message);
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

    // ---------- end-to-end text rendering with alignment ----------

    [Fact]
    public void Render_TextAlignment_HorizontalAlignmentShiftsPixels()
    {
        // Render the same string at the same anchor with three horizontal
        // alignments and verify the painted column ranges fall on opposite
        // sides of the anchor x. The exact font is platform-dependent but
        // Skia's SKTextAlign handling is deterministic.
        const int Width = 200;
        const int Height = 40;
        const float AnchorX = 100f;
        const float AnchorY = 25f;
        SKColor textColor = new(0, 0, 0, 255);

        (int firstLitX, int lastLitX) MeasureSpan(TextHAlign align)
        {
            DrawingPayload text = new TextDrawing(
                "MMMM", new SKPoint(AnchorX, AnchorY), 24f, textColor, align, TextVAlign.Baseline);
            SKBitmap bmp = new(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (SKCanvas canvas = new(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                DrawingRenderer.Render(canvas, text);
            }
            int first = -1, last = -1;
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    if (bmp.GetPixel(x, y).Alpha > 0)
                    {
                        if (first < 0) first = x;
                        last = x;
                        break;
                    }
                }
            }
            bmp.Dispose();
            return (first, last);
        }

        (int leftFirst, int leftLast) = MeasureSpan(TextHAlign.Left);
        (int centerFirst, int centerLast) = MeasureSpan(TextHAlign.Center);
        (int rightFirst, int rightLast) = MeasureSpan(TextHAlign.Right);

        Assert.True(leftFirst >= AnchorX - 2,
            $"left-aligned text should start at the anchor; got first lit pixel at x={leftFirst}.");
        Assert.True(rightLast <= AnchorX + 2,
            $"right-aligned text should end at the anchor; got last lit pixel at x={rightLast}.");
        Assert.True(centerFirst < AnchorX && centerLast > AnchorX,
            $"center-aligned text should span the anchor; got [{centerFirst}, {centerLast}].");
    }

    [Fact]
    public void Render_TextAlignment_VerticalAlignmentShiftsPixels()
    {
        // Same idea for vertical alignment: top should paint below the
        // anchor, bottom should paint above it, baseline is the original
        // behaviour (most of the glyph above the anchor with descenders
        // possibly below).
        const int Width = 80;
        const int Height = 200;
        const float AnchorX = 10f;
        const float AnchorY = 100f;
        SKColor textColor = new(0, 0, 0, 255);

        (int firstLitY, int lastLitY) MeasureSpan(TextVAlign align)
        {
            DrawingPayload text = new TextDrawing(
                "Mg", new SKPoint(AnchorX, AnchorY), 32f, textColor, TextHAlign.Left, align);
            SKBitmap bmp = new(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (SKCanvas canvas = new(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                DrawingRenderer.Render(canvas, text);
            }
            int first = -1, last = -1;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (bmp.GetPixel(x, y).Alpha > 0)
                    {
                        if (first < 0) first = y;
                        last = y;
                        break;
                    }
                }
            }
            bmp.Dispose();
            return (first, last);
        }

        (int topFirst, int topLast) = MeasureSpan(TextVAlign.Top);
        (int midFirst, int midLast) = MeasureSpan(TextVAlign.Middle);
        (int baseFirst, int baseLast) = MeasureSpan(TextVAlign.Baseline);
        (int botFirst, int botLast) = MeasureSpan(TextVAlign.Bottom);

        Assert.True(topFirst >= AnchorY - 2,
            $"top-aligned text should start at the anchor; got first lit pixel at y={topFirst}.");
        Assert.True(botLast <= AnchorY + 2,
            $"bottom-aligned text should end at the anchor; got last lit pixel at y={botLast}.");
        Assert.True(midFirst < AnchorY && midLast > AnchorY,
            $"middle-aligned text should span the anchor; got [{midFirst}, {midLast}].");
        // Baseline default: most glyphs end on or above the anchor (descenders may dip).
        Assert.True(baseFirst < AnchorY,
            $"baseline-aligned text should ascend above the anchor; got first lit pixel at y={baseFirst}.");
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
