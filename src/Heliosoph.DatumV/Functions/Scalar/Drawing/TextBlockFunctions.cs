using System.Collections.Immutable;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Drawing;

// ---------- draw_text_block ----------

/// <summary>
/// <c>draw_text_block(text, at, size, fill, max_width [, line_height] [, font_family])</c>
/// → Drawing — word-wraps a string into lines no wider than <c>max_width</c>
/// pixels and lays them out top-to-bottom from a <strong>top-left anchor</strong>.
/// The multiline counterpart to <c>draw_text</c> for captions, cards, and
/// terminal-style panels.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Anchor is the block's top-left,</strong> unlike <c>draw_text</c>'s
/// baseline anchor — block layout is easier to reason about from a corner.
/// Each line advances by <c>size * line_height</c> (default 1.3).
/// </para>
/// <para>
/// <strong>Wrapping</strong> is greedy word wrap measured with the same font
/// the renderer uses, so wrap decisions always agree with the rasterized
/// output. Explicit <c>\n</c> characters force breaks (blank lines are
/// preserved); a single word wider than <c>max_width</c> hard-breaks at the
/// character level. <c>text_measure</c> runs the identical engine, so a card
/// sized from its output always fits the drawn block.
/// </para>
/// </remarks>
public sealed class DrawTextBlockFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "draw_text_block";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Word-wraps text into lines no wider than max_width and draws them from a top-left "
        + "anchor with size * line_height (default 1.3) advance per line. Explicit \\n forces "
        + "breaks; over-long words hard-break. Pair with text_measure to size backing cards.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",      DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("at",        DataKindMatcher.Exact(DataKind.Point2D)),
                new ParameterSpec("size",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("fill",      DataKindMatcher.Exact(DataKind.Color)),
                new ParameterSpec("max_width", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
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
                new ParameterSpec("max_width",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("line_height", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
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
                new ParameterSpec("max_width",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("line_height", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("font_family", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Drawing)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DrawTextBlockFunction>(argumentKinds);

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
        float maxWidth = args[4].ToFloat();
        float lineHeight = args.Length >= 6 ? args[5].ToFloat() : TextBlockLayout.DefaultLineHeight;
        string? family = args.Length >= 7 ? args[6].AsString() : null;

        TextBlockLayout.ValidateGeometry(Name, size, maxWidth, lineHeight);

        IReadOnlyList<string> lines = TextBlockLayout.Wrap(text, size, maxWidth, family);
        float advance = size * lineHeight;
        ImmutableArray<DrawingPayload>.Builder children =
            ImmutableArray.CreateBuilder<DrawingPayload>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            children.Add(new TextDrawing(
                lines[i],
                new SKPoint(at.X, at.Y + i * advance),
                size,
                color,
                TextHAlign.Left,
                TextVAlign.Top,
                family));
        }

        return new ValueTask<ValueRef>(ValueRef.FromDrawing(new GroupDrawing(children.ToImmutable())));
    }
}

// ---------- text_measure ----------

/// <summary>
/// <c>text_measure(text, size, max_width [, line_height] [, font_family])</c>
/// → <c>Struct{width Float32, height Float32, lines Int32}</c> — measures the
/// block <c>draw_text_block</c> would produce for the same arguments: the
/// widest wrapped line's pixel width, the total block height
/// (<c>lines * size * line_height</c>), and the line count.
/// </summary>
/// <remarks>
/// Runs the identical wrap engine as <c>draw_text_block</c>, so a background
/// card sized from this struct always fits the drawn text exactly.
/// </remarks>
public sealed class TextMeasureFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "text_measure";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Drawing;
    /// <inheritdoc />
    public static string Description =>
        "Measures the wrapped block draw_text_block would draw: "
        + "text_measure(text, size, max_width [, line_height] [, font_family]) → "
        + "Struct{width, height, lines}. Use it to size backing cards to content.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",      DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("size",      DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max_width", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",        DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("size",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max_width",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("line_height", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text",        DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("size",        DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("max_width",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("line_height", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("font_family", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TextMeasureFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) return new ValueTask<ValueRef>(ValueRef.NullStruct(0));
        }

        string text = args[0].AsString();
        float size = args[1].ToFloat();
        float maxWidth = args[2].ToFloat();
        float lineHeight = args.Length >= 4 ? args[3].ToFloat() : TextBlockLayout.DefaultLineHeight;
        string? family = args.Length >= 5 ? args[4].AsString() : null;

        TextBlockLayout.ValidateGeometry(Name, size, maxWidth, lineHeight);

        IReadOnlyList<string> lines = TextBlockLayout.Wrap(text, size, maxWidth, family);
        float widest = 0f;
        using (SKFont font = TextBlockLayout.CreateFont(size, family))
        {
            foreach (string line in lines)
            {
                widest = System.Math.Max(widest, font.MeasureText(line));
            }
        }

        ValueRef[] fields =
        [
            ValueRef.FromFloat32(widest),
            ValueRef.FromFloat32(lines.Count * size * lineHeight),
            ValueRef.FromInt32(lines.Count),
        ];

        ushort typeId = 0;
        if (frame.Types is { } types)
        {
            int float32ScalarTypeId = types.InternScalarType(DataKind.Float32);
            int int32ScalarTypeId = types.InternScalarType(DataKind.Int32);
            typeId = (ushort)types.InternStructType(
            [
                new StructFieldDescriptor("width", float32ScalarTypeId),
                new StructFieldDescriptor("height", float32ScalarTypeId),
                new StructFieldDescriptor("lines", int32ScalarTypeId),
            ]);
        }

        return new ValueTask<ValueRef>(ValueRef.FromStruct(fields, typeId));
    }
}

// ---------- shared wrap engine ----------

/// <summary>
/// The wrap engine shared by <see cref="DrawTextBlockFunction"/> and
/// <see cref="TextMeasureFunction"/>: font construction mirrors the
/// renderer's <c>DrawText</c> (null family = platform default, named family
/// via <see cref="SKTypeface.FromFamilyName(string)"/> with silent fallback),
/// so wrap decisions, measurements, and rasterized output always agree.
/// </summary>
internal static class TextBlockLayout
{
    internal const float DefaultLineHeight = 1.3f;

    internal static SKFont CreateFont(float size, string? family) => family is null
        ? new SKFont { Size = size }
        : new SKFont(SKTypeface.FromFamilyName(family), size);

    internal static void ValidateGeometry(string functionName, float size, float maxWidth, float lineHeight)
    {
        if (size <= 0)
        {
            throw new FunctionArgumentException(functionName, $"size must be positive; got {size}.");
        }
        if (maxWidth <= 0)
        {
            throw new FunctionArgumentException(functionName, $"max_width must be positive; got {maxWidth}.");
        }
        if (lineHeight <= 0)
        {
            throw new FunctionArgumentException(functionName, $"line_height must be positive; got {lineHeight}.");
        }
    }

    /// <summary>
    /// Greedy word wrap: explicit <c>\n</c> forces breaks (blank lines
    /// preserved); words join with single spaces while the measured line fits
    /// <paramref name="maxWidth"/>; a word too wide for an empty line
    /// hard-breaks at the character level.
    /// </summary>
    internal static IReadOnlyList<string> Wrap(string text, float size, float maxWidth, string? family)
    {
        if (text.Length == 0) return [];

        using SKFont font = CreateFont(size, family);
        List<string> lines = [];

        foreach (string paragraph in text.Split('\n'))
        {
            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add("");
                continue;
            }

            string current = "";
            foreach (string word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(candidate) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = "";
                }

                // Word alone fits on a fresh line?
                if (font.MeasureText(word) <= maxWidth)
                {
                    current = word;
                    continue;
                }

                // Hard-break an over-long word at the character level.
                string remaining = word;
                while (remaining.Length > 0)
                {
                    int take = 1;
                    while (take < remaining.Length
                        && font.MeasureText(remaining[..(take + 1)]) <= maxWidth)
                    {
                        take++;
                    }
                    if (take < remaining.Length)
                    {
                        lines.Add(remaining[..take]);
                        remaining = remaining[take..];
                    }
                    else
                    {
                        current = remaining;
                        remaining = "";
                    }
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }
        }

        return lines;
    }
}
