using System.Collections.Immutable;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Drawing;

/// <summary>
/// Discriminated payload tree for a <see cref="DatumIngest.Model.DataKind.Drawing"/>
/// value. The base type is abstract; concrete variants
/// (<see cref="ShapeDrawing"/>, <see cref="TextDrawing"/>,
/// <see cref="ImageStampDrawing"/>, <see cref="GroupDrawing"/>,
/// <see cref="TransformedDrawing"/>) carry the per-node geometry, style,
/// and child references. The universal rasterizer (<c>RenderFunction</c>)
/// walks this tree onto an <see cref="SKCanvas"/>; an animation driver
/// invokes the rasterizer once per frame after the user's lambda has
/// built a fresh tree.
/// </summary>
/// <remarks>
/// <para>
/// Records are immutable and structurally equal, so two Drawings with the
/// same payload tree compare equal (useful for caching and for plan-time
/// constant-folding of static drawings). Construction is cheap — a single
/// record allocation per node — and the tree shape mirrors what the user
/// types: <c>group([draw_rect(...), draw_ellipse(...)])</c> becomes a
/// <see cref="GroupDrawing"/> with two <see cref="ShapeDrawing"/> children.
/// </para>
/// <para>
/// SkiaSharp types in the payload (<see cref="SKPoint"/>,
/// <see cref="SKColor"/>) are 32-bit value structs, so the carrier stays
/// small. <see cref="ImageStampDrawing"/> holds an <see cref="SKBitmap"/>
/// reference, which is GC-managed; the tree's lifetime determines the
/// bitmap's reachability.
/// </para>
/// </remarks>
public abstract record DrawingPayload;

/// <summary>
/// A primitive 2D shape — rectangle, ellipse, line, polygon, or arbitrary
/// path. Geometry is in pixel coordinates relative to the eventual render
/// target; the rasterizer doesn't apply implicit scaling. Fill and stroke
/// are both optional but at least one is normally present (a shape with
/// neither renders nothing — valid but pointless).
/// </summary>
/// <param name="Kind">Which geometric shape this node represents.</param>
/// <param name="Position">Anchor position (semantics depend on <paramref name="Kind"/> — rectangle top-left, ellipse centre, line start, polygon first vertex, path origin).</param>
/// <param name="Size">Width/height for rectangle and ellipse (where size has axis-aligned meaning). Ignored for line/polygon/path.</param>
/// <param name="EndPoint">Line end-point. Only used when <paramref name="Kind"/> is <see cref="ShapeKind.Line"/>.</param>
/// <param name="Points">Polygon vertices in draw order, or path command sequence. Used when <paramref name="Kind"/> is <see cref="ShapeKind.Polygon"/> or <see cref="ShapeKind.Path"/>.</param>
/// <param name="Fill">Optional fill color. <see langword="null"/> = no fill (outline only when stroke is present).</param>
/// <param name="Stroke">Optional stroke color. <see langword="null"/> = no stroke.</param>
/// <param name="StrokeWidth">Stroke width in pixels. Ignored when <paramref name="Stroke"/> is <see langword="null"/>.</param>
public sealed record ShapeDrawing(
    ShapeKind Kind,
    SKPoint Position,
    SKSize Size,
    SKPoint EndPoint = default,
    ImmutableArray<SKPoint> Points = default,
    SKColor? Fill = null,
    SKColor? Stroke = null,
    float StrokeWidth = 1f) : DrawingPayload;

/// <summary>Shape variant selector for <see cref="ShapeDrawing"/>.</summary>
public enum ShapeKind
{
    /// <summary>Axis-aligned rectangle. Position = top-left, Size = width × height.</summary>
    Rectangle,

    /// <summary>Ellipse. Position = centre, Size = X-radius × Y-radius (so X-radius = Size.Width).</summary>
    Ellipse,

    /// <summary>Line segment. Position = start, EndPoint = end.</summary>
    Line,

    /// <summary>Closed polygon. Points lists the vertices in draw order.</summary>
    Polygon,

    /// <summary>Arbitrary path (move / line / curve / arc commands).</summary>
    Path,
}

/// <summary>
/// Text rendering at a specific position. The font defaults to SkiaSharp's
/// platform default when <paramref name="FontFamily"/> is
/// <see langword="null"/>; future revisions can introduce a pixel-bitmap
/// font for the GeoCities aesthetic without changing this carrier.
/// </summary>
/// <param name="Text">The string to render. May be empty (renders nothing).</param>
/// <param name="Position">Baseline anchor — typically the left edge of the first character's baseline.</param>
/// <param name="FontSize">Font size in pixels.</param>
/// <param name="Color">Text color.</param>
/// <param name="FontFamily">Optional font family name. <see langword="null"/> = platform default.</param>
public sealed record TextDrawing(
    string Text,
    SKPoint Position,
    float FontSize,
    SKColor Color,
    string? FontFamily = null) : DrawingPayload;

/// <summary>
/// Stamps an existing bitmap onto the canvas at a given position and
/// anchor. Anchor is expressed in fractions of the bitmap's dimensions:
/// <c>(0, 0)</c> = top-left, <c>(0.5, 0.5)</c> = centre, <c>(1, 1)</c> =
/// bottom-right. Useful for composing photos / sprites into a drawing
/// tree without re-encoding.
/// </summary>
/// <param name="Image">The bitmap to stamp.</param>
/// <param name="Position">Where to place the anchor in canvas pixels.</param>
/// <param name="Anchor">Anchor in [0, 1] coordinates relative to the bitmap.</param>
public sealed record ImageStampDrawing(
    SKBitmap Image,
    SKPoint Position,
    SKPoint Anchor) : DrawingPayload;

/// <summary>
/// A list of child drawings, rendered in declared order (later children
/// composite on top of earlier ones). The natural result of a
/// <c>group([...])</c> call or an array-literal lambda body.
/// </summary>
/// <param name="Children">Child drawings in back-to-front draw order.</param>
public sealed record GroupDrawing(
    ImmutableArray<DrawingPayload> Children) : DrawingPayload;

/// <summary>
/// Wraps a child drawing with a 2D affine transform applied at render
/// time. Each transform is independent — translate, scale, rotate,
/// opacity — and composes naturally with the inner content.
/// </summary>
/// <param name="Inner">The wrapped drawing.</param>
/// <param name="Anchor">Reference point for translate/scale/rotate (in inner-content pixels).</param>
/// <param name="Translate">Additive translation in canvas pixels applied after anchor placement.</param>
/// <param name="Scale">Uniform scale factor applied around <paramref name="Anchor"/>.</param>
/// <param name="RotationDegrees">Rotation in degrees (clockwise) around <paramref name="Anchor"/>.</param>
/// <param name="Opacity">Multiplicative opacity in <c>[0, 1]</c>.</param>
public sealed record TransformedDrawing(
    DrawingPayload Inner,
    SKPoint Anchor = default,
    SKPoint Translate = default,
    float Scale = 1f,
    float RotationDegrees = 0f,
    float Opacity = 1f) : DrawingPayload;

/// <summary>
/// Arbitrary path with cubic / quadratic bezier curve support. Distinct from
/// <see cref="ShapeDrawing"/>'s line-only <see cref="ShapeKind.Path"/> kind —
/// the latter is kept around for the trivial polyline case (where
/// <see cref="ShapeDrawing.Points"/> is enough), this carries the richer
/// command stream that <c>draw_path</c> / <c>fill_path</c> need.
/// </summary>
/// <param name="Commands">Sequence of move / line / quadratic / cubic / close commands. Coordinates are absolute (no relative-coord support in v1).</param>
/// <param name="Fill">Optional fill colour. <see langword="null"/> = no fill.</param>
/// <param name="Stroke">Optional stroke colour. <see langword="null"/> = no stroke.</param>
/// <param name="StrokeWidth">Stroke width in pixels. Ignored when <paramref name="Stroke"/> is <see langword="null"/>.</param>
public sealed record PathDrawing(
    ImmutableArray<PathCommand> Commands,
    SKColor? Fill = null,
    SKColor? Stroke = null,
    float StrokeWidth = 1f) : DrawingPayload;

/// <summary>
/// One step in a <see cref="PathDrawing"/>'s command stream. Concrete
/// variants encode an SVG-path-like subset: <c>M</c>oveTo, <c>L</c>ineTo,
/// <c>Q</c>uadratic bezier, <c>C</c>ubic bezier, and <c>Z</c> (close).
/// All coordinates are absolute pixels relative to the canvas origin.
/// </summary>
public abstract record PathCommand;

/// <summary>Starts a new sub-path at <paramref name="Point"/>.</summary>
public sealed record PathMove(SKPoint Point) : PathCommand;

/// <summary>Straight segment from the current point to <paramref name="Point"/>.</summary>
public sealed record PathLine(SKPoint Point) : PathCommand;

/// <summary>Quadratic bezier from the current point through <paramref name="Control"/> to <paramref name="End"/>.</summary>
public sealed record PathQuadratic(SKPoint Control, SKPoint End) : PathCommand;

/// <summary>Cubic bezier from the current point through <paramref name="C1"/> and <paramref name="C2"/> to <paramref name="End"/>.</summary>
public sealed record PathCubic(SKPoint C1, SKPoint C2, SKPoint End) : PathCommand;

/// <summary>Closes the current sub-path (straight segment back to the most recent <see cref="PathMove"/>).</summary>
public sealed record PathClose : PathCommand;

/// <summary>
/// Wraps a child drawing with a foreshortened-3D rotation around either
/// the X or Y axis at the supplied anchor. The renderer applies a Skia
/// <see cref="SKMatrix"/> with the perspective slots populated so the
/// projection has real foreshortening — the side rotating away from the
/// viewer compresses, the side rotating toward the viewer doesn't grow
/// (unlike a pure scale, which can't simulate depth).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Geocities aesthetic.</strong> Pairs with animation lambdas to
/// produce the classic "text/image spinning around its vertical axis"
/// effect — drive <paramref name="AngleDegrees"/> with
/// <c>lerp(t, 0, 360)</c> for a full revolution per animation cycle.
/// </para>
/// <para>
/// <strong>Axis convention.</strong> X-axis spin rotates top toward / away
/// from viewer (vertical flip); Y-axis spin rotates left / right edges
/// toward / away (horizontal flip). The
/// <see cref="PerspectiveAxis.Y"/> case is the typical "marquee spin".
/// </para>
/// </remarks>
/// <param name="Inner">The wrapped drawing.</param>
/// <param name="Anchor">Pivot point in inner-content pixel coordinates — the spin's centre of rotation.</param>
/// <param name="Axis">Which axis to rotate around (X = vertical flip, Y = horizontal spin).</param>
/// <param name="AngleDegrees">Rotation in degrees. <c>0</c> = face-on, <c>90</c> = edge-on, <c>180</c> = mirrored, etc.</param>
public sealed record PerspectiveDrawing(
    DrawingPayload Inner,
    SKPoint Anchor,
    PerspectiveAxis Axis,
    float AngleDegrees) : DrawingPayload;

/// <summary>Axis selector for <see cref="PerspectiveDrawing"/>.</summary>
public enum PerspectiveAxis
{
    /// <summary>Rotate around a horizontal axis through the anchor — top edge tilts toward / away from viewer.</summary>
    X,

    /// <summary>Rotate around a vertical axis through the anchor — left/right edges swing toward / away from viewer (Geocities marquee spin).</summary>
    Y,
}

/// <summary>
/// Wraps a child drawing with a Porter–Duff / blend-mode compositing rule.
/// The inner drawing renders into a fresh transparent layer; when that
/// layer is composited back onto the parent canvas, the supplied
/// <see cref="SKBlendMode"/> determines how its pixels mix with what's
/// already there. Layer-based semantics: child siblings draw additively
/// among themselves with normal alpha-over inside the layer, then the
/// whole layer combines with the parent using <paramref name="Mode"/>.
/// </summary>
/// <param name="Inner">The wrapped drawing.</param>
/// <param name="Mode">Porter–Duff or photographer-style blend mode applied when the layer composites onto its parent.</param>
public sealed record BlendedDrawing(
    DrawingPayload Inner,
    SKBlendMode Mode) : DrawingPayload;
