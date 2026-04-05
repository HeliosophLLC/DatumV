using System.Collections.Immutable;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Drawing;

/// <summary>
/// <c>draw_rect(at Point2D, size Point2D, fill Color)</c> → Drawing.
/// Axis-aligned filled rectangle at the given top-left position with the
/// given width × height. Fill is required; stroke variants will follow in
/// a later phase.
/// </summary>
public sealed class DrawRectFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_rect";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a filled axis-aligned rectangle Drawing. "
        + "'at' is the top-left corner; 'size' is (width, height) in pixels.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("size", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("fill", DataKindMatcher.Exact(DataKind.Color)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawRectFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        Vector2 at = args[0].AsPoint2D();
        Vector2 size = args[1].AsPoint2D();
        SKColor fill = DrawingHelpers.ToSKColor(args[2]);
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new ShapeDrawing(
            ShapeKind.Rectangle,
            Position: new SKPoint(at.X, at.Y),
            Size: new SKSize(size.X, size.Y),
            Fill: fill)));
    }
}

/// <summary>
/// <c>draw_ellipse(at Point2D, radii Point2D, fill Color)</c> → Drawing.
/// Ellipse centred at <c>at</c>, with X-radius and Y-radius taken from
/// <c>radii</c>.
/// </summary>
public sealed class DrawEllipseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_ellipse";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a filled ellipse Drawing centred at 'at' with the given (X-radius, Y-radius).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("at",    DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("radii", DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("fill",  DataKindMatcher.Exact(DataKind.Color)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawEllipseFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        Vector2 at = args[0].AsPoint2D();
        Vector2 radii = args[1].AsPoint2D();
        SKColor fill = DrawingHelpers.ToSKColor(args[2]);
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new ShapeDrawing(
            ShapeKind.Ellipse,
            Position: new SKPoint(at.X, at.Y),
            Size: new SKSize(radii.X, radii.Y),
            Fill: fill)));
    }
}

/// <summary>
/// <c>draw_circle(at Point2D, radius, fill Color)</c> → Drawing.
/// Sugar over <see cref="DrawEllipseFunction"/> with equal X and Y radii.
/// </summary>
public sealed class DrawCircleFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_circle";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a filled circle Drawing centred at 'at' with the given radius. "
        + "Sugar over draw_ellipse with equal X and Y radii.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("at",     DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("radius", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("fill",   DataKindMatcher.Exact(DataKind.Color)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawCircleFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        Vector2 at = args[0].AsPoint2D();
        float radius = args[1].ToFloat();
        if (radius < 0)
        {
            throw new FunctionArgumentException(Name, $"radius must be non-negative; got {radius}.");
        }
        SKColor fill = DrawingHelpers.ToSKColor(args[2]);
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new ShapeDrawing(
            ShapeKind.Ellipse,
            Position: new SKPoint(at.X, at.Y),
            Size: new SKSize(radius, radius),
            Fill: fill)));
    }
}

/// <summary>
/// <c>draw_line(start Point2D, end Point2D, stroke Color, width Float32)</c> → Drawing.
/// Stroked line segment. The stroke argument is required (a line with no
/// stroke would render nothing).
/// </summary>
public sealed class DrawLineFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_line";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a stroked line segment Drawing from 'start' to 'end' with the given color and width.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start",  DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("end",    DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("stroke", DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawLineFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        Vector2 start = args[0].AsPoint2D();
        Vector2 end = args[1].AsPoint2D();
        SKColor stroke = DrawingHelpers.ToSKColor(args[2]);
        float width = args[3].ToFloat();
        if (width < 0)
        {
            throw new FunctionArgumentException(Name, $"width must be non-negative; got {width}.");
        }
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new ShapeDrawing(
            ShapeKind.Line,
            Position: new SKPoint(start.X, start.Y),
            Size: default,
            EndPoint: new SKPoint(end.X, end.Y),
            Stroke: stroke,
            StrokeWidth: width)));
    }
}

/// <summary>
/// <c>draw_polygon(points Array&lt;Point2D&gt;, fill Color)</c> → Drawing.
/// Closed polygon with vertices in draw order. The polygon auto-closes
/// (the last vertex connects to the first).
/// </summary>
public sealed class DrawPolygonFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_polygon";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;

    /// <inheritdoc />
    public static string Description =>
        "Constructs a filled closed polygon Drawing from the supplied vertex list "
        + "(in draw order). The polygon auto-closes between the last and first vertex.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("points", DataKindMatcher.Exact(DataKind.Point2D), IsArray: ArrayMatch.Array),
                new ParameterSpec("fill",   DataKindMatcher.Exact(DataKind.Color)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawPolygonFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        ReadOnlySpan<ValueRef> elements = args[0].GetArrayElements();
        if (elements.Length < 3)
        {
            throw new FunctionArgumentException(Name,
                $"polygon requires at least 3 vertices; got {elements.Length}.");
        }
        ImmutableArray<SKPoint>.Builder pts = ImmutableArray.CreateBuilder<SKPoint>(elements.Length);
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].IsNull)
            {
                throw new FunctionArgumentException(Name, $"polygon vertex [{i}] is null.");
            }
            Vector2 p = elements[i].AsPoint2D();
            pts.Add(new SKPoint(p.X, p.Y));
        }
        SKColor fill = DrawingHelpers.ToSKColor(args[1]);
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new ShapeDrawing(
            ShapeKind.Polygon,
            Position: default,
            Size: default,
            Points: pts.ToImmutable(),
            Fill: fill)));
    }
}

internal static class DrawingHelpers
{
    /// <summary>
    /// Converts a <see cref="DataKind.Color"/> <see cref="ValueRef"/> into an
    /// <see cref="SKColor"/> for the Skia drawing path.
    /// </summary>
    public static SKColor ToSKColor(ValueRef color)
    {
        (byte r, byte g, byte b, byte a) = color.AsColor();
        return new SKColor(r, g, b, a);
    }
}
