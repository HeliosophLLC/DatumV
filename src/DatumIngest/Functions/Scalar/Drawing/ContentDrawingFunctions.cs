using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Drawing;

// ---------- draw_text ----------

/// <summary>
/// <c>draw_text(text, at, size, fill)</c> → Drawing — renders a string of
/// text at the given <strong>baseline anchor</strong> in the requested
/// pixel size and colour. Variant adds a font-family name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Position is the baseline,</strong> not the top-left — text
/// ascends above the anchor and (for letters with descenders) drops below
/// it. To pin to a top-left coordinate add the font size to the y:
/// <c>draw_text('hi', point2d(8, top_y + size), size, fill)</c> is a
/// usable approximation; for exact metrics, use the rendered Image's
/// dimensions to verify.
/// </para>
/// <para>
/// The optional <c>font_family</c> variant maps to a Skia
/// <see cref="SKTypeface"/>. Names that aren't installed fall back to the
/// platform default — no error is raised so animations stay portable
/// across machines. Future revisions may bundle a pixel-bitmap font for
/// the procedural-graphics aesthetic.
/// </para>
/// </remarks>
public sealed class DrawTextFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_text";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Renders text at an anchor position. The 4-/5-argument forms anchor on the "
        + "baseline; the 6-/7-argument forms additionally accept horizontal "
        + "(left | center | right) and vertical (top | middle | baseline | bottom) "
        + "alignment strings. Optional trailing font-family argument falls back to "
        + "the platform default when the named family isn't installed.";

    /// <summary>
    /// Canonical horizontal alignment values surfaced as LS completions.
    /// </summary>
    private static readonly IReadOnlyList<string> HAlignNames = ["left", "center", "right"];

    /// <summary>
    /// Canonical vertical alignment values surfaced as LS completions.
    /// </summary>
    private static readonly IReadOnlyList<string> VAlignNames = ["top", "middle", "baseline", "bottom"];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("at",   DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("size", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("fill", DataKindMatcher.Exact(DataKind.Color)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",        DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("at",          DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("size",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("fill",        DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("font_family", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",    DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("at",      DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("size",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("fill",    DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("h_align", DataKindMatcher.StringEnum(HAlignNames)),
                new ParameterSpec("v_align", DataKindMatcher.StringEnum(VAlignNames)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",        DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("at",          DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("size",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("fill",        DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("h_align",     DataKindMatcher.StringEnum(HAlignNames)),
                new ParameterSpec("v_align",     DataKindMatcher.StringEnum(VAlignNames)),
                new ParameterSpec("font_family", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawTextFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        string text = args[0].AsString();
        Vector2 at = args[1].AsPoint2D();
        float size = args[2].ToFloat();
        SKColor color = DrawingHelpers.ToSKColor(args[3]);

        // Variants:
        //   4 args:                                                    baseline + Left, no font_family
        //   5 args (font_family):                                      baseline + Left, font_family
        //   6 args (h_align, v_align):                                 aligned, no font_family
        //   7 args (h_align, v_align, font_family):                    aligned, font_family
        TextHAlign hAlign = TextHAlign.Left;
        TextVAlign vAlign = TextVAlign.Baseline;
        string? family = null;
        if (args.Length == 5)
        {
            family = args[4].AsString();
        }
        else if (args.Length >= 6)
        {
            hAlign = ParseHAlign(args[4].AsString());
            vAlign = ParseVAlign(args[5].AsString());
            if (args.Length >= 7) family = args[6].AsString();
        }

        if (size <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"size must be positive; got {size}.");
        }

        return new ValueTask<ValueRef>(ValueRef.FromDrawing(
            new TextDrawing(text, new SKPoint(at.X, at.Y), size, color, hAlign, vAlign, family)));
    }

    private static TextHAlign ParseHAlign(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "left"   => TextHAlign.Left,
        "center" => TextHAlign.Center,
        "centre" => TextHAlign.Center,
        "right"  => TextHAlign.Right,
        _ => throw new FunctionArgumentException(Name,
            $"unknown horizontal alignment '{raw}'. Known values: left, center, right."),
    };

    private static TextVAlign ParseVAlign(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "top"      => TextVAlign.Top,
        "middle"   => TextVAlign.Middle,
        "center"   => TextVAlign.Middle,
        "centre"   => TextVAlign.Middle,
        "baseline" => TextVAlign.Baseline,
        "bottom"   => TextVAlign.Bottom,
        _ => throw new FunctionArgumentException(Name,
            $"unknown vertical alignment '{raw}'. Known values: top, middle, baseline, bottom."),
    };
}

// ---------- draw_image ----------

/// <summary>
/// <c>draw_image(image, at)</c> → Drawing — stamps an Image onto the
/// canvas at the given position, anchored at the top-left.
/// <c>draw_image(image, at, anchor)</c> places the image with a custom
/// anchor in <c>[0, 1]</c> coordinates relative to the image dimensions.
/// </summary>
/// <remarks>
/// The image is not re-encoded — its decoded bitmap is reused as the
/// stamp source. Useful for composing existing renders / photos into a
/// drawing tree without an Image → Drawing → Image round-trip.
/// </remarks>
public sealed class DrawImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_image";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Stamps an Image onto the canvas at a given position. Default anchor is "
        + "top-left (0, 0); a third argument overrides with a custom anchor in [0, 1].";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("at",    DataKindMatcher.Exact(DataKind.Point2D)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",  DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("at",     DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("anchor", DataKindMatcher.Exact(DataKind.Point2D)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        SKBitmap bitmap = args[0].AsImage();
        Vector2 at = args[1].AsPoint2D();
        Vector2 anchor = args.Length >= 3 ? args[2].AsPoint2D() : Vector2.Zero;

        return new ValueTask<ValueRef>(ValueRef.FromDrawing(
            new ImageStampDrawing(bitmap, new SKPoint(at.X, at.Y), new SKPoint(anchor.X, anchor.Y))));
    }
}

// ---------- path command parser (shared between draw_path and fill_path) ----------

/// <summary>
/// Parses the SVG-subset path-command string accepted by
/// <see cref="DrawPathFunction"/> and <see cref="FillPathFunction"/>.
/// </summary>
/// <remarks>
/// Supported commands (uppercase only — absolute coordinates):
/// <list type="bullet">
///   <item><c>M x y</c> — move to <c>(x, y)</c></item>
///   <item><c>L x y</c> — line to <c>(x, y)</c></item>
///   <item><c>Q cx cy x y</c> — quadratic bezier through control <c>(cx, cy)</c> to <c>(x, y)</c></item>
///   <item><c>C c1x c1y c2x c2y x y</c> — cubic bezier through two controls to <c>(x, y)</c></item>
///   <item><c>Z</c> — close the current sub-path</item>
/// </list>
/// Whitespace-separated; commas accepted as additional separators. No
/// implicit subsequent-command coordinates (SVG lets <c>M 0 0 10 10</c>
/// mean <c>M 0 0 L 10 10</c> — we require an explicit <c>L</c>).
/// </remarks>
internal static class PathCommandParser
{
    public static ImmutableArray<PathCommand> Parse(string source, string functionName)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new FunctionArgumentException(functionName,
                "path command string is empty.");
        }

        ImmutableArray<PathCommand>.Builder builder = ImmutableArray.CreateBuilder<PathCommand>();
        string[] tokens = source.Split(
            new[] { ' ', '\t', '\n', '\r', ',' },
            StringSplitOptions.RemoveEmptyEntries);

        int idx = 0;
        while (idx < tokens.Length)
        {
            string cmd = tokens[idx++];
            if (cmd.Length != 1)
            {
                throw new FunctionArgumentException(functionName,
                    $"expected single-character path command, got '{cmd}'.");
            }
            switch (cmd[0])
            {
                case 'M':
                    builder.Add(new PathMove(ReadPoint(tokens, ref idx, functionName, "M")));
                    break;
                case 'L':
                    builder.Add(new PathLine(ReadPoint(tokens, ref idx, functionName, "L")));
                    break;
                case 'Q':
                    {
                        SKPoint control = ReadPoint(tokens, ref idx, functionName, "Q control");
                        SKPoint end = ReadPoint(tokens, ref idx, functionName, "Q end");
                        builder.Add(new PathQuadratic(control, end));
                    }
                    break;
                case 'C':
                    {
                        SKPoint c1 = ReadPoint(tokens, ref idx, functionName, "C control 1");
                        SKPoint c2 = ReadPoint(tokens, ref idx, functionName, "C control 2");
                        SKPoint end = ReadPoint(tokens, ref idx, functionName, "C end");
                        builder.Add(new PathCubic(c1, c2, end));
                    }
                    break;
                case 'Z':
                case 'z':
                    builder.Add(new PathClose());
                    break;
                default:
                    throw new FunctionArgumentException(functionName,
                        $"unknown path command '{cmd}' — supported: M, L, Q, C, Z.");
            }
        }

        return builder.ToImmutable();
    }

    private static SKPoint ReadPoint(string[] tokens, ref int idx, string fn, string ctx)
    {
        if (idx + 1 >= tokens.Length)
        {
            throw new FunctionArgumentException(fn,
                $"path command '{ctx}' is missing coordinates.");
        }
        if (!float.TryParse(tokens[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(tokens[idx + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
        {
            throw new FunctionArgumentException(fn,
                $"path command '{ctx}' has non-numeric coordinates '{tokens[idx]} {tokens[idx + 1]}'.");
        }
        idx += 2;
        return new SKPoint(x, y);
    }
}

// ---------- draw_path ----------

/// <summary>
/// <c>draw_path(commands, stroke, width)</c> → Drawing — strokes the
/// supplied path with the given colour and width. Command syntax is
/// documented in <see cref="PathCommandParser"/>.
/// </summary>
/// <remarks>
/// For a filled path use <see cref="FillPathFunction"/>. Stroke-only
/// matches the natural use case (SVG line art, curves, custom outlines)
/// — a single function for both fill and stroke would need more
/// arguments, none of which are usually wanted together.
/// </remarks>
public sealed class DrawPathFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_path";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Strokes an SVG-subset path (M / L / Q / C / Z) with the given colour and width. "
        + "Pair with fill_path for filled variants.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("commands", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("stroke",   DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("width",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawPathFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        string commands = args[0].AsString();
        SKColor stroke = DrawingHelpers.ToSKColor(args[1]);
        float width = args[2].ToFloat();
        if (width < 0)
        {
            throw new FunctionArgumentException(Name,
                $"width must be non-negative; got {width}.");
        }
        ImmutableArray<PathCommand> parsed = PathCommandParser.Parse(commands, Name);
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(
            new PathDrawing(parsed, Fill: null, Stroke: stroke, StrokeWidth: width)));
    }
}

// ---------- spin_x / spin_y ----------

/// <summary>
/// <c>spin_y(content Drawing, anchor Point2D, angle_deg)</c> → Drawing —
/// 3D rotation around a vertical axis through the anchor, producing the
/// classic "Geocities marquee" spin where the left and right edges swing
/// toward and away from the viewer with real foreshortening (not just a
/// horizontal scale).
/// </summary>
/// <remarks>
/// <para>
/// Drive with <c>lerp(t, 0, 360)</c> inside an animation lambda for a
/// full revolution per cycle, or with <c>oscillate(t, -45, 45)</c> for a
/// gentle rocking motion.
/// </para>
/// </remarks>
public sealed class SpinYFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "spin_y";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "3D Y-axis spin: rotates the content around a vertical axis through the anchor, "
        + "with real foreshortening via Skia's perspective matrix slots. "
        + "0° = face-on, 90° = edge-on, 180° = mirrored.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("content",   DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("anchor",    DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("angle_deg", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SpinYFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        DrawingPayload content = args[0].AsDrawing();
        System.Numerics.Vector2 anchor = args[1].AsPoint2D();
        float angle = args[2].ToFloat();
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(
            new PerspectiveDrawing(content, new SKPoint(anchor.X, anchor.Y), PerspectiveAxis.Y, angle)));
    }
}

/// <summary>
/// <c>spin_x(content Drawing, anchor Point2D, angle_deg)</c> → Drawing —
/// 3D rotation around a horizontal axis through the anchor. Top edge
/// tilts toward / away from the viewer (vertical flip with real
/// foreshortening). Mirror of <see cref="SpinYFunction"/>.
/// </summary>
public sealed class SpinXFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "spin_x";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "3D X-axis spin: rotates the content around a horizontal axis through the anchor "
        + "(top/bottom edges tilt toward and away from the viewer). "
        + "0° = face-on, 90° = edge-on, 180° = vertically mirrored.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("content",   DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("anchor",    DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("angle_deg", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SpinXFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        DrawingPayload content = args[0].AsDrawing();
        System.Numerics.Vector2 anchor = args[1].AsPoint2D();
        float angle = args[2].ToFloat();
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(
            new PerspectiveDrawing(content, new SKPoint(anchor.X, anchor.Y), PerspectiveAxis.X, angle)));
    }
}

// ---------- blend ----------

/// <summary>
/// <c>blend(content Drawing, mode String)</c> → Drawing — wraps a Drawing
/// with a blend-mode composition rule. The inner drawing renders into a
/// fresh transparent layer; when the layer composites back onto its
/// parent canvas, the supplied the supplied <c>mode</c> determines how
/// its pixels combine with what's already there.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Supported modes</strong> (case-insensitive; hyphens and
/// underscores both work):
/// </para>
/// <list type="bullet">
///   <item><c>normal</c> / <c>source-over</c> / <c>src-over</c> — default alpha-over</item>
///   <item><c>multiply</c>, <c>screen</c>, <c>overlay</c>, <c>darken</c>, <c>lighten</c></item>
///   <item><c>add</c> / <c>plus</c> / <c>additive</c> — additive blending (canonical "glow" mode)</item>
///   <item><c>difference</c>, <c>exclusion</c></item>
///   <item><c>soft-light</c>, <c>hard-light</c></item>
///   <item><c>color-dodge</c>, <c>color-burn</c></item>
///   <item><c>hue</c>, <c>saturation</c>, <c>color</c>, <c>luminosity</c> — HSL component blends</item>
/// </list>
/// <para>
/// <strong>Layer semantics.</strong> The blend mode applies to the layer
/// boundary, not per child. If the <c>content</c> argument is a
/// <c>draw_group([...])</c>, its children blend with each other under
/// normal alpha-over inside the layer; only the final layer composites
/// with the requested mode. To get per-particle additive blending,
/// wrap the particle sprite (or the particles call) in <c>blend</c>
/// rather than wrapping each particle separately.
/// </para>
/// </remarks>
public sealed class BlendFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "blend";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Wraps a Drawing with a Porter-Duff / photographer blend mode (multiply, screen, "
        + "add, difference, …). The inner drawing renders into a fresh layer; the layer "
        + "composites onto the parent canvas using the supplied mode.";

    /// <summary>
    /// Canonical blend-mode names surfaced as LS completions when the
    /// cursor sits inside the <c>mode</c> string literal. Aliases
    /// (<c>plus</c>, <c>additive</c>, <c>source-over</c>, <c>src-over</c>)
    /// still parse at runtime but aren't suggested — the canonical form
    /// is what the popup should nudge users toward.
    /// </summary>
    private static readonly IReadOnlyList<string> BlendModeNames =
    [
        "normal",
        "multiply",
        "screen",
        "overlay",
        "darken",
        "lighten",
        "add",
        "difference",
        "exclusion",
        "soft-light",
        "hard-light",
        "color-dodge",
        "color-burn",
        "hue",
        "saturation",
        "color",
        "luminosity",
    ];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("content", DataKindMatcher.Exact(DataKind.Drawing)),
                new ParameterSpec("mode",    DataKindMatcher.StringEnum(BlendModeNames)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<BlendFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        DrawingPayload content = args[0].AsDrawing();
        string modeName = args[1].AsString();
        if (!TryParseBlendMode(modeName, out SKBlendMode mode))
        {
            throw new FunctionArgumentException(Name,
                $"unknown blend mode '{modeName}'. Known modes: normal, multiply, "
                + "screen, overlay, darken, lighten, add, difference, exclusion, "
                + "soft-light, hard-light, color-dodge, color-burn, hue, saturation, "
                + "color, luminosity.");
        }
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new BlendedDrawing(content, mode)));
    }

    /// <summary>
    /// Resolves a user-friendly mode string to <see cref="SKBlendMode"/>.
    /// Accepts the CSS / photographer names users expect, plus a few
    /// aliases — <c>add</c>/<c>plus</c>/<c>additive</c> all map to
    /// <see cref="SKBlendMode.Plus"/> since "additive" is what artists
    /// say but "plus" is the Skia label.
    /// </summary>
    private static bool TryParseBlendMode(string raw, out SKBlendMode mode)
    {
        string normalised = raw.Trim().ToLowerInvariant().Replace('-', '_');
        switch (normalised)
        {
            case "normal":
            case "source_over":
            case "src_over":   mode = SKBlendMode.SrcOver; return true;
            case "multiply":   mode = SKBlendMode.Multiply; return true;
            case "screen":     mode = SKBlendMode.Screen; return true;
            case "overlay":    mode = SKBlendMode.Overlay; return true;
            case "darken":     mode = SKBlendMode.Darken; return true;
            case "lighten":    mode = SKBlendMode.Lighten; return true;
            case "add":
            case "plus":
            case "additive":   mode = SKBlendMode.Plus; return true;
            case "difference": mode = SKBlendMode.Difference; return true;
            case "exclusion":  mode = SKBlendMode.Exclusion; return true;
            case "soft_light": mode = SKBlendMode.SoftLight; return true;
            case "hard_light": mode = SKBlendMode.HardLight; return true;
            case "color_dodge": mode = SKBlendMode.ColorDodge; return true;
            case "color_burn":  mode = SKBlendMode.ColorBurn; return true;
            case "hue":        mode = SKBlendMode.Hue; return true;
            case "saturation": mode = SKBlendMode.Saturation; return true;
            case "color":      mode = SKBlendMode.Color; return true;
            case "luminosity": mode = SKBlendMode.Luminosity; return true;
            default:           mode = SKBlendMode.SrcOver; return false;
        }
    }
}

// ---------- fill_path ----------

/// <summary>
/// <c>fill_path(commands, fill)</c> → Drawing — fills the closed region
/// of the supplied path with the given colour. Command syntax matches
/// <see cref="DrawPathFunction"/>.
/// </summary>
/// <remarks>
/// Unclosed sub-paths are auto-closed for fill purposes (Skia's default).
/// To get crisp outlines without the auto-close artifact, prefer
/// <c>draw_path</c> or a closing <c>Z</c> command.
/// </remarks>
public sealed class FillPathFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "fill_path";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Fills an SVG-subset path (M / L / Q / C / Z) with the given colour. "
        + "Open sub-paths are auto-closed for fill purposes.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("commands", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("fill",     DataKindMatcher.Exact(DataKind.Color)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<FillPathFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Drawing));
        }
        string commands = args[0].AsString();
        SKColor fill = DrawingHelpers.ToSKColor(args[1]);
        ImmutableArray<PathCommand> parsed = PathCommandParser.Parse(commands, Name);
        return new ValueTask<ValueRef>(ValueRef.FromDrawing(
            new PathDrawing(parsed, Fill: fill, Stroke: null, StrokeWidth: 0f)));
    }
}
