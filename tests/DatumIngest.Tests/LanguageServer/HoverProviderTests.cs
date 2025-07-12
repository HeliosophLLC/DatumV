namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="HoverProvider"/> — hover information for SQL tokens.
/// </summary>
public sealed class HoverProviderTests
{
    private static LanguageServerManifest CreateTestManifest()
    {
        return new LanguageServerManifest
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "users",
                    Columns =
                    [
                        new TableColumnEntry { Name = "id", Kind = "Scalar", Nullable = false },
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = true },
                    ],
                },
            ],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Scalar" }],
                    ReturnType = "Scalar",
                    Description = "Absolute value.",
                },
            ],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };
    }

    private static HoverProvider CreateProvider()
    {
        return new HoverProvider(CreateTestManifest());
    }

    // ───────────────────── Null / empty ─────────────────────

    [Fact]
    public void GetHover_EmptyString_ReturnsNull()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("", 0);

        Assert.Null(result);
    }

    [Fact]
    public void GetHover_CursorOutOfRange_ReturnsNull()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT", 99);

        Assert.Null(result);
    }

    // ───────────────────── Keyword hover ─────────────────────

    [Fact]
    public void GetHover_SelectKeyword_ReturnsKeywordDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT x FROM t", 0);

        Assert.NotNull(result);
        Assert.Contains("SELECT", result.Contents);
    }

    [Fact]
    public void GetHover_WhereKeyword_ReturnsKeywordDocumentation()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT x FROM t WHERE x = 1", 16);

        Assert.NotNull(result);
        Assert.Contains("WHERE", result.Contents);
    }

    // ───────────────────── Function hover ─────────────────────

    [Fact]
    public void GetHover_FunctionName_ReturnsSignature()
    {
        HoverProvider provider = CreateProvider();

        // "abs(" — cursor on "abs" at offset 7
        HoverResult? result = provider.GetHover("SELECT abs(x) FROM t", 7);

        Assert.NotNull(result);
        Assert.Contains("abs", result.Contents);
        Assert.Contains("Absolute value", result.Contents);
    }

    // ───────────────────── Table hover ─────────────────────

    [Fact]
    public void GetHover_TableName_ReturnsColumnList()
    {
        HoverProvider provider = CreateProvider();

        // "users" starts at offset 14
        HoverResult? result = provider.GetHover("SELECT * FROM users", 14);

        Assert.NotNull(result);
        Assert.Contains("users", result.Contents);
        Assert.Contains("id", result.Contents);
        Assert.Contains("name", result.Contents);
    }

    // ───────────────────── Column hover ─────────────────────

    [Fact]
    public void GetHover_ColumnName_ReturnsTypeInfo()
    {
        HoverProvider provider = CreateProvider();

        // "id" at offset 7—8 in "SELECT id FROM users"
        HoverResult? result = provider.GetHover("SELECT id FROM users", 7);

        Assert.NotNull(result);
        Assert.Contains("id", result.Contents);
        Assert.Contains("Scalar", result.Contents);
    }

    // ───────────────────── Hover span ─────────────────────

    [Fact]
    public void GetHover_ReturnedResult_HasValidSpan()
    {
        HoverProvider provider = CreateProvider();

        HoverResult? result = provider.GetHover("SELECT x FROM t", 0);

        Assert.NotNull(result);
        Assert.True(result.StartLine >= 0);
        Assert.True(result.StartColumn >= 0);
        Assert.True(result.EndColumn > result.StartColumn || result.EndLine > result.StartLine);
    }
}
