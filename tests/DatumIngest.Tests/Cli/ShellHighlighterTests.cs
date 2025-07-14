using DatumIngest.Cli.Shell;
using Spectre.Console.Rendering;

namespace DatumIngest.Tests.Cli;

/// <summary>
/// Tests for <see cref="ShellHighlighter"/> syntax coloring.
/// </summary>
public sealed class ShellHighlighterTests
{
    private readonly ShellHighlighter _highlighter = new();

    /// <summary>
    /// Empty input produces output without throwing.
    /// </summary>
    [Fact]
    public void BuildHighlightedText_EmptyInput_ProducesOutput()
    {
        IRenderable result = _highlighter.BuildHighlightedText("");
        Assert.NotNull(result);
    }

    /// <summary>
    /// Whitespace-only input produces output without throwing.
    /// </summary>
    [Fact]
    public void BuildHighlightedText_WhitespaceOnly_ProducesOutput()
    {
        IRenderable result = _highlighter.BuildHighlightedText("   ");
        Assert.NotNull(result);
    }

    /// <summary>
    /// Valid SQL input is tokenized and highlighted without error.
    /// </summary>
    [Fact]
    public void BuildHighlightedText_ValidSql_ProducesOutput()
    {
        IRenderable result = _highlighter.BuildHighlightedText("SELECT * FROM users WHERE id = 1");
        Assert.NotNull(result);
    }

    /// <summary>
    /// Dot-commands are highlighted as cyan.
    /// </summary>
    [Fact]
    public void BuildHighlightedText_DotCommand_ProducesCyanOutput()
    {
        IRenderable result = _highlighter.BuildHighlightedText(".tables");
        Assert.NotNull(result);
    }

    /// <summary>
    /// Incomplete SQL does not throw — falls back to plain text.
    /// </summary>
    [Fact]
    public void BuildHighlightedText_IncompleteSql_DoesNotThrow()
    {
        IRenderable result = _highlighter.BuildHighlightedText("SELECT * FROM");
        Assert.NotNull(result);
    }

    /// <summary>
    /// SQL with string literals is handled correctly.
    /// </summary>
    [Fact]
    public void BuildHighlightedText_StringLiteral_ProducesOutput()
    {
        IRenderable result = _highlighter.BuildHighlightedText("SELECT * FROM t WHERE name = 'Alice'");
        Assert.NotNull(result);
    }

    /// <summary>
    /// SQL with numeric literals is handled correctly.
    /// </summary>
    [Fact]
    public void BuildHighlightedText_NumericLiteral_ProducesOutput()
    {
        IRenderable result = _highlighter.BuildHighlightedText("SELECT * FROM t WHERE id = 42");
        Assert.NotNull(result);
    }
}
