using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Drawing;

/// <summary>
/// <c>render(drawing Drawing, size Point2D) → Image</c>. Universal
/// rasterizer for the procedural-drawing tree carried by
/// <see cref="DataKind.Drawing"/> values. Walks the
/// <see cref="DrawingPayload"/> recursively onto a fresh RGBA8888 bitmap
/// of the requested width × height, encoding the result as a PNG via the
/// standard image-output pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Background defaults to transparent (alpha = 0). Callers that want a
/// solid background composite a <see cref="ShapeKind.Rectangle"/>
/// covering the canvas as the first child of the top-level
/// <see cref="GroupDrawing"/>.
/// </para>
/// <para>
/// One render call = one Image. Animation drivers like <c>animate_gif</c>
/// call render once per frame; static renderings call it once. Either way
/// the heavy lifting is here, and the lambda-and-drawing substrate stays
/// free of any encoding or canvas concerns.
/// </para>
/// </remarks>
public sealed class RenderFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "render";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Rasterises a Drawing recipe onto an Image of the requested width × height. "
        + "Background is transparent unless the drawing itself fills it.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("drawing", DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("size",    DataKindMatcher.Exact(DataKind.Point2D)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RenderFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        DrawingPayload drawing = args[0].AsDrawing();
        Vector2 size = args[1].AsPoint2D();
        int width = (int)System.Math.Round(size.X);
        int height = (int)System.Math.Round(size.Y);
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"size must be positive in both dimensions; got {width}×{height}.");
        }

        SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            DrawingRenderer.Render(canvas, drawing);
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(bitmap));
    }
}

/// <summary>
/// Internal SkiaSharp-side walker that translates a
/// <see cref="DrawingPayload"/> tree into <see cref="SKCanvas"/> draw
/// calls. Kept separate from <see cref="RenderFunction"/> so the recursive
/// machinery is testable on its own and reusable by future consumers
/// (animation drivers, sub-renderers, layout previews).
/// </summary>
internal static class DrawingRenderer
{
    /// <summary>
    /// Walks <paramref name="payload"/>, issuing draw calls against
    /// <paramref name="canvas"/>. Caller controls canvas clipping,
    /// background fill, and final disposal.
    /// </summary>
    public static void Render(SKCanvas canvas, DrawingPayload payload)
    {
        switch (payload)
        {
            case GroupDrawing group:
                foreach (DrawingPayload child in group.Children)
                {
                    Render(canvas, child);
                }
                break;

            case TransformedDrawing transformed:
                {
                    // Capture the save count BEFORE any Save/SaveLayer, then
                    // RestoreToCount unwinds the whole stack — robust to
                    // ApplyTransform pushing an extra SaveLayer for opacity.
                    // Naïve Save+Restore would leak one state per opacity<1
                    // transform, accumulating translation offsets across
                    // sibling particles (visible as particles "drifting"
                    // toward the bottom-right after a few animation frames).
                    int saveCount = canvas.Save();
                    ApplyTransform(canvas, transformed);
                    Render(canvas, transformed.Inner);
                    canvas.RestoreToCount(saveCount);
                }
                break;

            case ShapeDrawing shape:
                DrawShape(canvas, shape);
                break;

            case TextDrawing text:
                DrawText(canvas, text);
                break;

            case ImageStampDrawing stamp:
                DrawImageStamp(canvas, stamp);
                break;

            case PathDrawing path:
                DrawPath(canvas, path);
                break;

            case PerspectiveDrawing persp:
                {
                    int saveCount = canvas.Save();
                    ApplyPerspective(canvas, persp);
                    Render(canvas, persp.Inner);
                    canvas.RestoreToCount(saveCount);
                }
                break;

            case BlendedDrawing blended:
                {
                    // Save the current canvas state; SaveLayer's blend paint
                    // controls how the layer composites onto the parent when
                    // we Restore. Same RestoreToCount discipline as
                    // TransformedDrawing — Save() + SaveLayer() push two
                    // states but only one Restore() would pop, leaking
                    // canvas state across sibling blends.
                    int saveCount = canvas.Save();
                    using SKPaint layerPaint = new() { BlendMode = blended.Mode };
                    canvas.SaveLayer(layerPaint);
                    Render(canvas, blended.Inner);
                    canvas.RestoreToCount(saveCount);
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unhandled DrawingPayload variant: {payload.GetType().Name}.");
        }
    }

    private static void ApplyTransform(SKCanvas canvas, TransformedDrawing t)
    {
        // Translate the transform anchor to the origin, apply scale + rotate,
        // then translate back plus the explicit translation. The order makes
        // anchor-centred scale and rotation behave intuitively.
        canvas.Translate(t.Anchor.X + t.Translate.X, t.Anchor.Y + t.Translate.Y);
        if (t.RotationDegrees != 0f)
        {
            canvas.RotateDegrees(t.RotationDegrees);
        }
        if (t.Scale != 1f)
        {
            canvas.Scale(t.Scale, t.Scale);
        }
        canvas.Translate(-t.Anchor.X, -t.Anchor.Y);
        // Opacity is applied via a layer with alpha when < 1.
        if (t.Opacity < 1f)
        {
            using SKPaint layerPaint = new() { Color = new SKColor(255, 255, 255, (byte)System.Math.Clamp(t.Opacity * 255, 0, 255)) };
            canvas.SaveLayer(layerPaint);
        }
    }

    private static void DrawShape(SKCanvas canvas, ShapeDrawing shape)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Rectangle:
                {
                    SKRect rect = new(
                        shape.Position.X, shape.Position.Y,
                        shape.Position.X + shape.Size.Width,
                        shape.Position.Y + shape.Size.Height);
                    if (shape.Fill is SKColor fill)
                    {
                        using SKPaint p = new() { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
                        canvas.DrawRect(rect, p);
                    }
                    if (shape.Stroke is SKColor stroke)
                    {
                        using SKPaint p = new() { Color = stroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = shape.StrokeWidth };
                        canvas.DrawRect(rect, p);
                    }
                }
                break;

            case ShapeKind.Ellipse:
                {
                    // Position = centre, Size.Width = X-radius, Size.Height = Y-radius.
                    SKRect bounds = new(
                        shape.Position.X - shape.Size.Width, shape.Position.Y - shape.Size.Height,
                        shape.Position.X + shape.Size.Width, shape.Position.Y + shape.Size.Height);
                    if (shape.Fill is SKColor fill)
                    {
                        using SKPaint p = new() { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
                        canvas.DrawOval(bounds, p);
                    }
                    if (shape.Stroke is SKColor stroke)
                    {
                        using SKPaint p = new() { Color = stroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = shape.StrokeWidth };
                        canvas.DrawOval(bounds, p);
                    }
                }
                break;

            case ShapeKind.Line:
                {
                    if (shape.Stroke is SKColor stroke)
                    {
                        using SKPaint p = new() { Color = stroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = shape.StrokeWidth };
                        canvas.DrawLine(shape.Position, shape.EndPoint, p);
                    }
                }
                break;

            case ShapeKind.Polygon:
            case ShapeKind.Path:
                {
                    if (shape.Points.IsDefaultOrEmpty)
                    {
                        return;
                    }
                    using SKPath path = new();
                    path.MoveTo(shape.Points[0]);
                    for (int i = 1; i < shape.Points.Length; i++)
                    {
                        path.LineTo(shape.Points[i]);
                    }
                    if (shape.Kind == ShapeKind.Polygon)
                    {
                        path.Close();
                    }
                    if (shape.Fill is SKColor fill)
                    {
                        using SKPaint p = new() { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
                        canvas.DrawPath(path, p);
                    }
                    if (shape.Stroke is SKColor stroke)
                    {
                        using SKPaint p = new() { Color = stroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = shape.StrokeWidth };
                        canvas.DrawPath(path, p);
                    }
                }
                break;
        }
    }

    private static void DrawText(SKCanvas canvas, TextDrawing text)
    {
        using SKFont font = text.FontFamily is null
            ? new SKFont { Size = text.FontSize }
            : new SKFont(SKTypeface.FromFamilyName(text.FontFamily), text.FontSize);
        using SKPaint paint = new() { Color = text.Color, IsAntialias = true };
        canvas.DrawText(text.Text, text.Position.X, text.Position.Y, SKTextAlign.Left, font, paint);
    }

    private static void DrawImageStamp(SKCanvas canvas, ImageStampDrawing stamp)
    {
        float anchorPxX = stamp.Anchor.X * stamp.Image.Width;
        float anchorPxY = stamp.Anchor.Y * stamp.Image.Height;
        canvas.DrawBitmap(stamp.Image, stamp.Position.X - anchorPxX, stamp.Position.Y - anchorPxY);
    }

    /// <summary>
    /// Applies the perspective-projected rotation matrix for a
    /// <see cref="PerspectiveDrawing"/>. Uses Skia's bottom-row
    /// perspective slots (<c>persp0</c>, <c>persp1</c>) so foreshortening
    /// is real — points further from the viewer compress, points closer
    /// stretch. Approximates a viewer at <c>z = ViewerDepthPixels</c>;
    /// the value is chosen empirically to give a noticeable but not
    /// extreme effect for typical 32–256 px drawings.
    /// </summary>
    private static void ApplyPerspective(SKCanvas canvas, PerspectiveDrawing p)
    {
        // Translate the anchor to the origin, project, translate back.
        // Concat order in Skia is `canvas-matrix = canvas-matrix × M`, so the
        // first concat applies last to point coordinates — we build matrices
        // in REVERSE of the intuitive order.
        const float ViewerDepthPixels = 220f;
        float rad = p.AngleDegrees * (MathF.PI / 180f);
        float cos = MathF.Cos(rad);
        float sin = MathF.Sin(rad);

        // Translate back to anchor (applied last to points).
        canvas.Translate(p.Anchor.X, p.Anchor.Y);

        // Perspective + rotation matrix. Build it from the 9 SKMatrix slots:
        //   | scaleX  skewX   transX |
        //   | skewY   scaleY  transY |
        //   | persp0  persp1  persp2 |
        SKMatrix m = p.Axis switch
        {
            PerspectiveAxis.Y => new SKMatrix
            {
                ScaleX = cos,  SkewX = 0,    TransX = 0,
                SkewY = 0,     ScaleY = 1,   TransY = 0,
                Persp0 = -sin / ViewerDepthPixels,
                Persp1 = 0,
                Persp2 = 1,
            },
            PerspectiveAxis.X => new SKMatrix
            {
                ScaleX = 1,    SkewX = 0,    TransX = 0,
                SkewY = 0,     ScaleY = cos, TransY = 0,
                Persp0 = 0,
                Persp1 = -sin / ViewerDepthPixels,
                Persp2 = 1,
            },
            _ => SKMatrix.Identity,
        };
        canvas.Concat(in m);

        // Translate anchor to origin (applied first to points).
        canvas.Translate(-p.Anchor.X, -p.Anchor.Y);
    }

    private static void DrawPath(SKCanvas canvas, PathDrawing path)
    {
        if (path.Commands.IsDefaultOrEmpty)
        {
            return;
        }
        using SKPath skPath = new();
        foreach (PathCommand cmd in path.Commands)
        {
            switch (cmd)
            {
                case PathMove m: skPath.MoveTo(m.Point); break;
                case PathLine l: skPath.LineTo(l.Point); break;
                case PathQuadratic q: skPath.QuadTo(q.Control, q.End); break;
                case PathCubic c: skPath.CubicTo(c.C1, c.C2, c.End); break;
                case PathClose: skPath.Close(); break;
            }
        }
        if (path.Fill is SKColor fill)
        {
            using SKPaint p = new() { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(skPath, p);
        }
        if (path.Stroke is SKColor stroke)
        {
            using SKPaint p = new() { Color = stroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = path.StrokeWidth };
            canvas.DrawPath(skPath, p);
        }
    }
}
