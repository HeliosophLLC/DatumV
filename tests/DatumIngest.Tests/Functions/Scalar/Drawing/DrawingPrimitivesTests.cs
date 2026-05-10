using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Drawing;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Drawing;

/// <summary>
/// Phase C: color constructors, shape draw primitives, composition
/// wrappers (group / transformed). Each test verifies the constructed
/// <see cref="DrawingPayload"/> shape; render-pixel verification belongs
/// in <see cref="RenderFunctionTests"/>.
/// </summary>
public sealed class DrawingPrimitivesTests : ServiceTestBase
{
    // ----- color() -----

    [Fact]
    public async Task Color_ThreeArg_DefaultsAlpha255()
    {
        ValueRef result = await new ColorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(100), ValueRef.FromInt32(150), ValueRef.FromInt32(200) },
            CreateEvaluationFrame(), default);
        (byte r, byte g, byte b, byte a) = result.AsColor();
        Assert.Equal((byte)100, r);
        Assert.Equal((byte)150, g);
        Assert.Equal((byte)200, b);
        Assert.Equal((byte)255, a);
    }

    [Fact]
    public async Task Color_FourArg_TakesExplicitAlpha()
    {
        ValueRef result = await new ColorFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(10), ValueRef.FromInt32(20), ValueRef.FromInt32(30), ValueRef.FromInt32(128) },
            CreateEvaluationFrame(), default);
        (_, _, _, byte a) = result.AsColor();
        Assert.Equal((byte)128, a);
    }

    [Fact]
    public async Task Color_OutOfRange_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ColorFunction().ExecuteAsync(
                new[] { ValueRef.FromInt32(256), ValueRef.FromInt32(0), ValueRef.FromInt32(0) },
                CreateEvaluationFrame(), default));
        Assert.Contains("[0, 255]", ex.Message);
    }

    [Fact]
    public async Task Color_NullPropagates()
    {
        ValueRef result = await new ColorFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Int32), ValueRef.FromInt32(0), ValueRef.FromInt32(0) },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Color, result.Kind);
    }

    // ----- color_hex() -----

    [Theory]
    [InlineData("#ff8800", 0xFF, 0x88, 0x00, 0xFF)]
    [InlineData("ff8800", 0xFF, 0x88, 0x00, 0xFF)]   // no leading #
    [InlineData("#FF8800", 0xFF, 0x88, 0x00, 0xFF)]  // uppercase
    [InlineData("#f80", 0xFF, 0x88, 0x00, 0xFF)]     // 3-digit
    [InlineData("#f80c", 0xFF, 0x88, 0x00, 0xCC)]    // 4-digit with alpha
    [InlineData("#1234abcd", 0x12, 0x34, 0xAB, 0xCD)] // 8-digit
    public async Task ColorHex_KnownForms_ParseCorrectly(string hex, int r, int g, int b, int a)
    {
        ValueRef result = await new ColorHexFunction().ExecuteAsync(
            new[] { ValueRef.FromString(hex) }, CreateEvaluationFrame(), default);
        (byte rb, byte gb, byte bb, byte ab) = result.AsColor();
        Assert.Equal((byte)r, rb);
        Assert.Equal((byte)g, gb);
        Assert.Equal((byte)b, bb);
        Assert.Equal((byte)a, ab);
    }

    [Theory]
    [InlineData("#zzz")]
    [InlineData("not-a-colour")]
    [InlineData("#abcde")]   // 5 digits — invalid
    public async Task ColorHex_BadInput_Throws(string input)
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ColorHexFunction().ExecuteAsync(
                new[] { ValueRef.FromString(input) }, CreateEvaluationFrame(), default));
    }

    // ----- color_interpolate() -----

    [Fact]
    public async Task ColorInterpolate_TZero_ReturnsFromExactly()
    {
        ValueRef result = await new ColorInterpolateFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromColor(10, 20, 30, 40),
                ValueRef.FromColor(200, 100, 50, 255),
                ValueRef.FromFloat32(0f),
            },
            CreateEvaluationFrame(), default);
        (byte r, byte g, byte b, byte a) = result.AsColor();
        Assert.Equal((byte)10, r);
        Assert.Equal((byte)20, g);
        Assert.Equal((byte)30, b);
        Assert.Equal((byte)40, a);
    }

    [Fact]
    public async Task ColorInterpolate_TOne_ReturnsToExactly()
    {
        ValueRef result = await new ColorInterpolateFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromColor(10, 20, 30, 40),
                ValueRef.FromColor(200, 100, 50, 255),
                ValueRef.FromFloat32(1f),
            },
            CreateEvaluationFrame(), default);
        (byte r, byte g, byte b, byte a) = result.AsColor();
        Assert.Equal((byte)200, r);
        Assert.Equal((byte)100, g);
        Assert.Equal((byte)50, b);
        Assert.Equal((byte)255, a);
    }

    [Fact]
    public async Task ColorInterpolate_TMidpoint_BlendsHalfway()
    {
        // 0 + (200 - 0) * 0.5 = 100; round-half-up keeps symmetric midpoints stable.
        ValueRef result = await new ColorInterpolateFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromColor(0, 0, 0, 0),
                ValueRef.FromColor(200, 100, 50, 100),
                ValueRef.FromFloat32(0.5f),
            },
            CreateEvaluationFrame(), default);
        (byte r, byte g, byte b, byte a) = result.AsColor();
        Assert.Equal((byte)100, r);
        Assert.Equal((byte)50, g);
        Assert.Equal((byte)25, b);
        Assert.Equal((byte)50, a);
    }

    [Theory]
    [InlineData(-0.5f, 10, 20, 30, 40)]   // below 0 clamps to from
    [InlineData(1.5f, 200, 100, 50, 255)] // above 1 clamps to to
    public async Task ColorInterpolate_OutOfRangeT_ClampsToEndpoints(
        float t, int er, int eg, int eb, int ea)
    {
        ValueRef result = await new ColorInterpolateFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromColor(10, 20, 30, 40),
                ValueRef.FromColor(200, 100, 50, 255),
                ValueRef.FromFloat32(t),
            },
            CreateEvaluationFrame(), default);
        (byte r, byte g, byte b, byte a) = result.AsColor();
        Assert.Equal((byte)er, r);
        Assert.Equal((byte)eg, g);
        Assert.Equal((byte)eb, b);
        Assert.Equal((byte)ea, a);
    }

    [Fact]
    public async Task ColorInterpolate_NullPropagates()
    {
        ValueRef result = await new ColorInterpolateFunction().ExecuteAsync(
            new[]
            {
                ValueRef.Null(DataKind.Color),
                ValueRef.FromColor(200, 100, 50, 255),
                ValueRef.FromFloat32(0.5f),
            },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Color, result.Kind);
    }

    // ----- draw_rect / draw_ellipse / draw_circle -----

    [Fact]
    public async Task DrawRect_ProducesRectangleShape()
    {
        ValueRef result = await new DrawRectFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(2, 3),
                ValueRef.FromPoint2D(10, 20),
                ValueRef.FromColor(255, 0, 0),
            },
            CreateEvaluationFrame(), default);
        ShapeDrawing shape = (ShapeDrawing)result.AsDrawing();
        Assert.Equal(ShapeKind.Rectangle, shape.Kind);
        Assert.Equal(new SKPoint(2, 3), shape.Position);
        Assert.Equal(new SKSize(10, 20), shape.Size);
        Assert.Equal(SKColors.Red, shape.Fill);
        Assert.Null(shape.Stroke);
    }

    [Fact]
    public async Task StrokeRect_ProducesOutlinedRectangleShape()
    {
        ValueRef result = await new StrokeRectFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(2, 3),
                ValueRef.FromPoint2D(10, 20),
                ValueRef.FromColor(0, 255, 0),
                ValueRef.FromFloat32(2.5f),
            },
            CreateEvaluationFrame(), default);
        ShapeDrawing shape = (ShapeDrawing)result.AsDrawing();
        Assert.Equal(ShapeKind.Rectangle, shape.Kind);
        Assert.Equal(new SKPoint(2, 3), shape.Position);
        Assert.Equal(new SKSize(10, 20), shape.Size);
        Assert.Equal(SKColors.Lime, shape.Stroke);
        Assert.Equal(2.5f, shape.StrokeWidth);
        Assert.Null(shape.Fill);
    }

    [Fact]
    public async Task StrokeRect_NegativeWidth_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new StrokeRectFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromPoint2D(0, 0),
                    ValueRef.FromPoint2D(10, 10),
                    ValueRef.FromColor(0, 255, 0),
                    ValueRef.FromFloat32(-1f),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("non-negative", ex.Message);
    }

    [Fact]
    public async Task StrokeRect_NullPropagates()
    {
        ValueRef result = await new StrokeRectFunction().ExecuteAsync(
            new[]
            {
                ValueRef.Null(DataKind.Point2D),
                ValueRef.FromPoint2D(10, 10),
                ValueRef.FromColor(0, 255, 0),
                ValueRef.FromFloat32(1f),
            },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Drawing, result.Kind);
    }

    [Fact]
    public async Task DrawEllipse_ProducesEllipseShape()
    {
        ValueRef result = await new DrawEllipseFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(16, 16),
                ValueRef.FromPoint2D(8, 4),
                ValueRef.FromColor(0, 0, 255),
            },
            CreateEvaluationFrame(), default);
        ShapeDrawing shape = (ShapeDrawing)result.AsDrawing();
        Assert.Equal(ShapeKind.Ellipse, shape.Kind);
        Assert.Equal(new SKPoint(16, 16), shape.Position);
        Assert.Equal(new SKSize(8, 4), shape.Size);
    }

    [Fact]
    public async Task DrawCircle_DesugarsToEqualRadiiEllipse()
    {
        ValueRef result = await new DrawCircleFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(16, 16),
                ValueRef.FromFloat32(7),
                ValueRef.FromColor(0, 255, 0),
            },
            CreateEvaluationFrame(), default);
        ShapeDrawing shape = (ShapeDrawing)result.AsDrawing();
        Assert.Equal(ShapeKind.Ellipse, shape.Kind);
        Assert.Equal(7f, shape.Size.Width);
        Assert.Equal(7f, shape.Size.Height);
    }

    [Fact]
    public async Task DrawCircle_NegativeRadius_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new DrawCircleFunction().ExecuteAsync(
                new[]
                {
                    ValueRef.FromPoint2D(0, 0),
                    ValueRef.FromFloat32(-1),
                    ValueRef.FromColor(0, 0, 0),
                },
                CreateEvaluationFrame(), default));
    }

    // ----- draw_line -----

    [Fact]
    public async Task DrawLine_ProducesLineShape()
    {
        ValueRef result = await new DrawLineFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromPoint2D(10, 10),
                ValueRef.FromColor(128, 128, 128),
                ValueRef.FromFloat32(2.5f),
            },
            CreateEvaluationFrame(), default);
        ShapeDrawing shape = (ShapeDrawing)result.AsDrawing();
        Assert.Equal(ShapeKind.Line, shape.Kind);
        Assert.Equal(new SKPoint(0, 0), shape.Position);
        Assert.Equal(new SKPoint(10, 10), shape.EndPoint);
        Assert.NotNull(shape.Stroke);
        Assert.Equal(2.5f, shape.StrokeWidth);
        // Four-arg form leaves StrokeEnd unset — renderer draws solid colour.
        Assert.Null(shape.StrokeEnd);
    }

    [Fact]
    public async Task DrawLine_FiveArg_CarriesStartAndEndColors()
    {
        ValueRef result = await new DrawLineFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromPoint2D(100, 0),
                ValueRef.FromColor(10, 20, 30, 255),
                ValueRef.FromColor(200, 150, 100, 255),
                ValueRef.FromFloat32(3f),
            },
            CreateEvaluationFrame(), default);
        ShapeDrawing shape = (ShapeDrawing)result.AsDrawing();
        Assert.Equal(ShapeKind.Line, shape.Kind);
        Assert.NotNull(shape.Stroke);
        Assert.Equal((byte)10, shape.Stroke!.Value.Red);
        Assert.NotNull(shape.StrokeEnd);
        Assert.Equal((byte)200, shape.StrokeEnd!.Value.Red);
        Assert.Equal((byte)150, shape.StrokeEnd!.Value.Green);
        Assert.Equal(3f, shape.StrokeWidth);
    }

    [Fact]
    public async Task DrawLine_FiveArg_NullEndColor_ReturnsNullDrawing()
    {
        ValueRef result = await new DrawLineFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromPoint2D(10, 10),
                ValueRef.FromColor(0, 0, 0, 255),
                ValueRef.Null(DataKind.Color),
                ValueRef.FromFloat32(1f),
            },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Drawing, result.Kind);
    }

    [Fact]
    public async Task DrawLine_FiveArg_RendersAsLinearGradient()
    {
        // Horizontal red→blue line across the full canvas width. Pixels
        // near the left edge should be predominantly red, pixels near
        // the right edge should be predominantly blue. Use a thick
        // stroke so the rasteriser fills full pixel columns at the
        // sample points.
        ValueRef line = await new DrawLineFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPoint2D(0, 20),
                ValueRef.FromPoint2D(99, 20),
                ValueRef.FromColor(255, 0, 0, 255),
                ValueRef.FromColor(0, 0, 255, 255),
                ValueRef.FromFloat32(20f),
            },
            CreateEvaluationFrame(), default);

        ValueRef rendered = await new RenderFunction().ExecuteAsync(
            new[] { line, ValueRef.FromPoint2D(100, 40) },
            CreateEvaluationFrame(), default);

        SKBitmap bitmap = rendered.AsImage();
        SKColor leftPixel = bitmap.GetPixel(2, 20);
        SKColor rightPixel = bitmap.GetPixel(97, 20);
        // Allow significant tolerance for antialiasing + sRGB interp curve
        // — the assertion just needs to show the gradient is going the
        // right direction.
        Assert.True(leftPixel.Red > leftPixel.Blue,
            $"left side should be red-dominant; got R={leftPixel.Red}, B={leftPixel.Blue}.");
        Assert.True(rightPixel.Blue > rightPixel.Red,
            $"right side should be blue-dominant; got R={rightPixel.Red}, B={rightPixel.Blue}.");
    }

    // ----- draw_polygon -----

    [Fact]
    public async Task DrawPolygon_ProducesPolygonShapeWithVertices()
    {
        ValueRef[] pts =
        [
            ValueRef.FromPoint2D(0, 0),
            ValueRef.FromPoint2D(10, 0),
            ValueRef.FromPoint2D(5, 10),
        ];
        ValueRef polygonPoints = ValueRef.FromArray(DataKind.Point2D, pts);

        ValueRef result = await new DrawPolygonFunction().ExecuteAsync(
            new[] { polygonPoints, ValueRef.FromColor(0, 100, 200) },
            CreateEvaluationFrame(), default);
        ShapeDrawing shape = (ShapeDrawing)result.AsDrawing();
        Assert.Equal(ShapeKind.Polygon, shape.Kind);
        Assert.Equal(3, shape.Points.Length);
        Assert.Equal(new SKPoint(5, 10), shape.Points[2]);
    }

    [Fact]
    public async Task DrawPolygon_FewerThanThreeVertices_Throws()
    {
        ValueRef[] pts = [ValueRef.FromPoint2D(0, 0), ValueRef.FromPoint2D(5, 5)];
        ValueRef polygonPoints = ValueRef.FromArray(DataKind.Point2D, pts);

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new DrawPolygonFunction().ExecuteAsync(
                new[] { polygonPoints, ValueRef.FromColor(0, 0, 0) },
                CreateEvaluationFrame(), default));
    }

    // ----- group -----

    [Fact]
    public async Task Group_CollectsChildrenInOrder()
    {
        DrawingPayload a = new ShapeDrawing(ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red);
        DrawingPayload b = new ShapeDrawing(ShapeKind.Ellipse, new SKPoint(8, 8), new SKSize(2, 2), Fill: SKColors.Green);
        ValueRef[] children = [ValueRef.FromDrawing(a), ValueRef.FromDrawing(b)];

        ValueRef result = await new GroupFunction().ExecuteAsync(
            new[] { ValueRef.FromArray(DataKind.Drawing, children) },
            CreateEvaluationFrame(), default);
        GroupDrawing group = (GroupDrawing)result.AsDrawing();
        Assert.Equal(2, group.Children.Length);
        Assert.Same(a, group.Children[0]);
        Assert.Same(b, group.Children[1]);
    }

    [Fact]
    public async Task Group_NullChildIsSkipped()
    {
        DrawingPayload a = new ShapeDrawing(ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red);
        ValueRef[] children =
        [
            ValueRef.FromDrawing(a),
            ValueRef.Null(DataKind.Drawing),
        ];

        ValueRef result = await new GroupFunction().ExecuteAsync(
            new[] { ValueRef.FromArray(DataKind.Drawing, children) },
            CreateEvaluationFrame(), default);
        GroupDrawing group = (GroupDrawing)result.AsDrawing();
        Assert.Single(group.Children);
    }

    // ----- transformed -----

    [Fact]
    public async Task Transformed_WrapsInnerWithGivenTransformValues()
    {
        DrawingPayload inner = new ShapeDrawing(
            ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red);
        ValueRef result = await new TransformedFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromDrawing(inner),
                ValueRef.FromPoint2D(16, 16),
                ValueRef.FromFloat32(45f),
                ValueRef.FromFloat32(1.5f),
                ValueRef.FromFloat32(0.8f),
            },
            CreateEvaluationFrame(), default);
        TransformedDrawing t = (TransformedDrawing)result.AsDrawing();
        Assert.Same(inner, t.Inner);
        Assert.Equal(new SKPoint(16, 16), t.Anchor);
        Assert.Equal(45f, t.RotationDegrees);
        Assert.Equal(1.5f, t.Scale);
        Assert.Equal(0.8f, t.Opacity);
    }

    [Fact]
    public async Task Transformed_OpacityClampsToZeroOne()
    {
        DrawingPayload inner = new ShapeDrawing(
            ShapeKind.Rectangle, new SKPoint(0, 0), new SKSize(4, 4), Fill: SKColors.Red);
        ValueRef result = await new TransformedFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromDrawing(inner),
                ValueRef.FromPoint2D(0, 0),
                ValueRef.FromFloat32(0),
                ValueRef.FromFloat32(1),
                ValueRef.FromFloat32(5f),  // > 1
            },
            CreateEvaluationFrame(), default);
        TransformedDrawing t = (TransformedDrawing)result.AsDrawing();
        Assert.Equal(1f, t.Opacity);
    }
}
