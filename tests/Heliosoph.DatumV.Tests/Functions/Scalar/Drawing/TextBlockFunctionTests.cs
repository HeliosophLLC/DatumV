using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Drawing;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Drawing;

/// <summary>
/// Tests for <see cref="DrawTextBlockFunction"/> and <see cref="TextMeasureFunction"/> —
/// word-wrapped multiline text. Verifies wrap decisions (greedy word wrap,
/// explicit newlines, hard-breaking over-long words), top-left block
/// anchoring with uniform line advance, measure/draw agreement, rasterization
/// through <c>render()</c>, and null / validation behavior.
/// </summary>
/// <remarks>
/// Wrap widths depend on the platform default font, so tests never hardcode
/// pixel widths — they measure probe strings with the same
/// <see cref="SKFont"/> construction the renderer uses and derive
/// <c>max_width</c> from that.
/// </remarks>
public sealed class TextBlockFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_textblock_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // ───────────────────────── helpers ─────────────────────────

    private const float Size = 16f;

    private static float MeasureWithDefaultFont(string text, float size)
    {
        using SKFont font = new() { Size = size };
        return font.MeasureText(text);
    }

    private async Task<ValueRef> DrawBlock(
        string text, float x, float y, float size, float maxWidth,
        float? lineHeight = null, string? family = null)
    {
        DrawTextBlockFunction fn = new();
        List<ValueRef> args =
        [
            ValueRef.FromString(text),
            ValueRef.FromPoint2D(x, y),
            ValueRef.FromFloat32(size),
            ValueRef.FromColor(255, 255, 255),
            ValueRef.FromFloat32(maxWidth),
        ];
        if (lineHeight is { } lh) args.Add(ValueRef.FromFloat32(lh));
        if (family is { } f) args.Add(ValueRef.FromString(f));
        return await fn.ExecuteAsync(args.ToArray(), CreateEvaluationFrame(), default);
    }

    private static TextDrawing[] Lines(ValueRef drawing)
    {
        GroupDrawing group = Assert.IsType<GroupDrawing>(drawing.AsDrawing());
        return group.Children.Select(c => Assert.IsType<TextDrawing>(c)).ToArray();
    }

    // ───────────────────────── metadata ─────────────────────────

    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("draw_text_block", DrawTextBlockFunction.Name);
        Assert.Equal(FunctionCategory.Drawing, DrawTextBlockFunction.Category);
        Assert.Equal("text_measure", TextMeasureFunction.Name);
        Assert.Equal(FunctionCategory.Drawing, TextMeasureFunction.Category);
    }

    // ───────────────────────── wrapping ─────────────────────────

    [Fact]
    public async Task GreedyWrap_BreaksAtMeasuredWidth()
    {
        // max_width fits exactly one "word" (plus a sliver) — three words wrap
        // to three lines regardless of platform font metrics.
        float maxWidth = MeasureWithDefaultFont("word", Size) * 1.2f;
        ValueRef drawing = await DrawBlock("word word word", 10f, 20f, Size, maxWidth);

        TextDrawing[] lines = Lines(drawing);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Equal("word", l.Text));
    }

    [Fact]
    public async Task WideMaxWidth_SingleLine()
    {
        ValueRef drawing = await DrawBlock("a few words here", 0f, 0f, Size, 10_000f);

        TextDrawing[] lines = Lines(drawing);
        Assert.Single(lines);
        Assert.Equal("a few words here", lines[0].Text);
    }

    [Fact]
    public async Task ExplicitNewlines_AlwaysBreak()
    {
        ValueRef drawing = await DrawBlock("first\nsecond\n\nfourth", 0f, 0f, Size, 10_000f);

        TextDrawing[] lines = Lines(drawing);
        Assert.Equal(4, lines.Length);
        Assert.Equal("first", lines[0].Text);
        Assert.Equal("second", lines[1].Text);
        Assert.Equal("", lines[2].Text);
        Assert.Equal("fourth", lines[3].Text);
    }

    [Fact]
    public async Task OverlongWord_HardBreaksToFit()
    {
        float maxWidth = MeasureWithDefaultFont("abc", Size) * 1.1f;
        ValueRef drawing = await DrawBlock("abcdefghijkl", 0f, 0f, Size, maxWidth);

        TextDrawing[] lines = Lines(drawing);
        Assert.True(lines.Length >= 2, "an over-long word must hard-break across lines");
        Assert.Equal("abcdefghijkl", string.Concat(lines.Select(l => l.Text)));
        using SKFont font = new() { Size = Size };
        foreach (TextDrawing line in lines)
        {
            Assert.True(font.MeasureText(line.Text) <= maxWidth + 0.5f,
                $"hard-broken segment '{line.Text}' exceeds max_width");
        }
    }

    // ───────────────────────── layout ─────────────────────────

    [Fact]
    public async Task Lines_AnchorTopLeft_WithUniformAdvance()
    {
        float maxWidth = MeasureWithDefaultFont("word", Size) * 1.2f;
        float lineHeight = 1.5f;
        ValueRef drawing = await DrawBlock("word word word", 10f, 20f, Size, maxWidth, lineHeight);

        TextDrawing[] lines = Lines(drawing);
        float advance = Size * lineHeight;
        for (int i = 0; i < lines.Length; i++)
        {
            Assert.Equal(10f, lines[i].Position.X);
            Assert.Equal(20f + i * advance, lines[i].Position.Y, 3);
            Assert.Equal(TextHAlign.Left, lines[i].HAlign);
            Assert.Equal(TextVAlign.Top, lines[i].VAlign);
        }
    }

    [Fact]
    public async Task FontFamily_PropagatesToEveryLine()
    {
        ValueRef drawing = await DrawBlock("one two", 0f, 0f, Size, 10_000f, lineHeight: 1.3f, family: "monospace");

        TextDrawing[] lines = Lines(drawing);
        Assert.All(lines, l => Assert.Equal("monospace", l.FontFamily));
    }

    [Fact]
    public async Task EmptyText_EmptyGroup()
    {
        ValueRef drawing = await DrawBlock("", 0f, 0f, Size, 100f);
        GroupDrawing group = Assert.IsType<GroupDrawing>(drawing.AsDrawing());
        Assert.Empty(group.Children);
    }

    // ───────────────────────── validation / nulls ─────────────────────────

    [Fact]
    public async Task NullText_ReturnsNullDrawing()
    {
        DrawTextBlockFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(new[]
        {
            ValueRef.Null(DataKind.String),
            ValueRef.FromPoint2D(0f, 0f),
            ValueRef.FromFloat32(Size),
            ValueRef.FromColor(255, 255, 255),
            ValueRef.FromFloat32(100f),
        }, CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Drawing, result.Kind);
    }

    [Fact]
    public async Task NonPositiveSizeOrWidth_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await DrawBlock("x", 0f, 0f, 0f, 100f));
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await DrawBlock("x", 0f, 0f, Size, 0f));
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await DrawBlock("x", 0f, 0f, Size, 100f, lineHeight: 0f));
    }

    // ───────────────────────── text_measure ─────────────────────────

    [Fact]
    public async Task Measure_AgreesWithDraw()
    {
        float maxWidth = MeasureWithDefaultFont("word", Size) * 1.2f;
        float lineHeight = 1.4f;

        ValueRef drawing = await DrawBlock("word word word", 0f, 0f, Size, maxWidth, lineHeight);
        int drawnLines = Lines(drawing).Length;

        TextMeasureFunction fn = new();
        var context = CreateExecutionContext();
        EvaluationFrame frame = CreateEvaluationFrame(context);
        ValueRef result = await fn.ExecuteAsync(new[]
        {
            ValueRef.FromString("word word word"),
            ValueRef.FromFloat32(Size),
            ValueRef.FromFloat32(maxWidth),
            ValueRef.FromFloat32(lineHeight),
        }, frame, default);

        Assert.Equal(DataKind.Struct, result.Kind);
        TypeDescriptor? desc = context.Types.GetDescriptor(result.TypeId);
        Assert.NotNull(desc?.Fields);
        int widthIdx = desc!.FindFieldIndex("width");
        int heightIdx = desc.FindFieldIndex("height");
        int linesIdx = desc.FindFieldIndex("lines");
        Assert.True(widthIdx >= 0 && heightIdx >= 0 && linesIdx >= 0);

        ReadOnlySpan<ValueRef> fields = result.GetStructFields();
        Assert.Equal(drawnLines, fields[linesIdx].ToInt32());
        Assert.Equal(drawnLines * Size * lineHeight, fields[heightIdx].ToFloat(), 2);
        float width = fields[widthIdx].ToFloat();
        Assert.True(width > 0 && width <= maxWidth + 0.5f,
            $"width {width} should be positive and within max_width {maxWidth}");
    }

    // ───────────────────────── rasterization ─────────────────────────

    [Fact]
    public async Task Render_WrappedBlock_PaintsBelowFirstLine()
    {
        float maxWidth = MeasureWithDefaultFont("word", Size) * 1.2f;
        ValueRef drawing = await DrawBlock("word word word", 4f, 4f, Size, maxWidth, lineHeight: 1.3f);

        RenderFunction render = new();
        ValueRef image = await render.ExecuteAsync(new[]
        {
            drawing,
            ValueRef.FromPoint2D(120f, 120f),
        }, CreateEvaluationFrame(), default);

        using SKBitmap bitmap = image.AsImage();

        // Ink must appear in the second line's band (between one and two
        // line-advances below the block top) — proof the wrap drew there.
        float advance = Size * 1.3f;
        bool secondLineInk = false;
        for (int y = (int)(4 + advance); y < (int)(4 + 2 * advance) && !secondLineInk; y++)
        {
            for (int x = 0; x < bitmap.Width && !secondLineInk; x++)
            {
                secondLineInk = bitmap.GetPixel(x, y).Alpha > 0;
            }
        }
        Assert.True(secondLineInk, "wrapped second line should paint below the first line advance");
    }

    // ───────────────────────── end-to-end SQL ─────────────────────────

    [Fact]
    public async Task Sql_TerminalCard_RenderAndMeasure()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE prompts (txt String)");
        catalog.Plan("INSERT INTO prompts VALUES ('SELECT model, latency FROM runs ORDER BY latency LIMIT 3')");

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT render(draw_text_block(txt, point2d(12.0, 12.0), 14.0, color_hex('#e2e8f0'), 176.0, 1.4, 'monospace'), " +
            "              point2d(200.0, 160.0)) AS card, " +
            "       (text_measure(txt, 14.0, 176.0, 1.4, 'monospace'))['lines'] AS line_count " +
            "FROM prompts",
            catalog, store: arena);

        Assert.Single(rows);
        int lineCount = rows[0]["line_count"].AsInt32();
        Assert.True(lineCount >= 2, $"long prompt should wrap, got {lineCount} line(s)");

        byte[] png = rows[0]["card"].AsByteSpan(arena, catalog.SidecarRegistry).ToArray();
        using SKBitmap bitmap = SKBitmap.Decode(png)!;
        Assert.NotNull(bitmap);
        Assert.Equal(200, bitmap.Width);
        Assert.Equal(160, bitmap.Height);
    }
}
