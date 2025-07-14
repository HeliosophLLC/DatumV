using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Cli.Shell;
using DatumIngest.Functions;

namespace DatumIngest.Tests.Cli;

/// <summary>
/// Tests for <see cref="ShellCompletionHandler"/> tab completion logic.
/// </summary>
public sealed class ShellCompletionHandlerTests
{
    private readonly ShellCompletionHandler _handler;

    /// <summary>
    /// Initializes the completion handler with a catalog containing test tables.
    /// </summary>
    public ShellCompletionHandlerTests()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        _handler = new ShellCompletionHandler(catalog, registry);
    }

    /// <summary>
    /// Empty word returns no completions.
    /// </summary>
    [Fact]
    public void GetCompletions_EmptyWord_ReturnsNull()
    {
        IEnumerable<string>? result = _handler.GetCompletions("", "", "");
        Assert.Null(result);
    }

    /// <summary>
    /// Dot-commands are completed at the start of input.
    /// </summary>
    [Fact]
    public void GetCompletions_DotPrefix_ReturnsDotCommands()
    {
        IEnumerable<string>? result = _handler.GetCompletions("", ".tab", "");
        Assert.NotNull(result);
        Assert.Contains(".tables", result);
    }

    /// <summary>
    /// Dot-e prefix matches .exit, .explain, .export.
    /// </summary>
    [Fact]
    public void GetCompletions_DotE_ReturnsMatchingCommands()
    {
        IEnumerable<string>? result = _handler.GetCompletions("", ".e", "");
        Assert.NotNull(result);
        List<string> matches = result.ToList();
        Assert.Contains(".exit", matches);
        Assert.Contains(".explain", matches);
        Assert.Contains(".export", matches);
    }

    /// <summary>
    /// SQL keywords are offered based on prefix.
    /// </summary>
    [Fact]
    public void GetCompletions_SqlKeyword_ReturnsMatches()
    {
        IEnumerable<string>? result = _handler.GetCompletions("", "SEL", "");
        Assert.NotNull(result);
        Assert.Contains("SELECT", result);
    }

    /// <summary>
    /// After FROM keyword, only table names are suggested (not SQL keywords).
    /// </summary>
    [Fact]
    public void GetCompletions_AfterFromKeyword_ReturnsOnlyTableNames()
    {
        // No tables registered starting with "s", so the result should be empty.
        IEnumerable<string>? result = _handler.GetCompletions("SELECT * FROM ", "s", "");
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    /// <summary>
    /// Function names are included in general completions.
    /// </summary>
    [Fact]
    public void GetCompletions_FunctionPrefix_IncludesFunctionNames()
    {
        // "abs" should match the abs function.
        IEnumerable<string>? result = _handler.GetCompletions("SELECT ", "abs", "(x)");
        Assert.NotNull(result);
        Assert.Contains("abs", result, StringComparer.OrdinalIgnoreCase);
    }
}
