namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="CompletionProvider"/> — context-aware SQL completion generation.
/// </summary>
public sealed class CompletionProviderTests
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
                        new TableColumnEntry { Name = "id", Kind = "Float32", Nullable = false },
                        new TableColumnEntry { Name = "name", Kind = "String", Nullable = false },
                        new TableColumnEntry { Name = "email", Kind = "String", Nullable = true },
                    ],
                },
                new TableSchemaEntry
                {
                    Name = "orders",
                    Columns =
                    [
                        new TableColumnEntry { Name = "order_id", Kind = "Float32", Nullable = false },
                        new TableColumnEntry { Name = "user_id", Kind = "Float32", Nullable = false },
                        new TableColumnEntry { Name = "total", Kind = "Float32", Nullable = false },
                    ],
                },
            ],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "abs",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "Float32" }],
                    ReturnType = "Float32",
                    Description = "Absolute value.",
                },
                new FunctionSignature
                {
                    Name = "unnest",
                    Parameters = [new ParameterSignature { Name = "array_column", Kind = "Vector" }],
                    ReturnType = "Float32",
                    Description = "Expands a vector column.",
                    IsTableValued = true,
                },
            ],
            Keywords = ["SELECT", "FROM", "WHERE", "JOIN", "ON", "ORDER", "BY", "AS", "INTO"],
        };
    }

    private static CompletionProvider CreateProvider()
    {
        return new CompletionProvider(CreateTestManifest());
    }

    // ───────────────────── Statement start ─────────────────────

    [Fact]
    public void GetCompletions_EmptyInput_OffersSelectKeyword()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("", 0);

        Assert.Contains(items, item => item.Label == "SELECT" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── After SELECT ─────────────────────

    [Fact]
    public void GetCompletions_AfterSelect_OffersColumnsAndFunctions()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT ", 7);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "name" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "abs" && item.Kind == CompletionItemKind.Function);
        Assert.Contains(items, item => item.Label == "FROM" && item.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public void GetCompletions_AfterSelectWithPrefix_FiltersByPrefix()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT na", 9);

        Assert.Contains(items, item => item.Label == "name");
        Assert.DoesNotContain(items, item => item.Label == "id");
        Assert.DoesNotContain(items, item => item.Label == "abs");
    }

    // ───────────────────── After FROM ─────────────────────

    [Fact]
    public void GetCompletions_AfterFrom_OffersTablesAndTableValuedFunctions()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        Assert.Contains(items, item => item.Label == "users" && item.Kind == CompletionItemKind.Table);
        Assert.Contains(items, item => item.Label == "orders" && item.Kind == CompletionItemKind.Table);
        Assert.Contains(items, item => item.Label == "unnest" && item.Kind == CompletionItemKind.Function);
        // Should NOT include scalar functions in FROM context.
        Assert.DoesNotContain(items, item => item.Label == "abs");
    }

    // ───────────────────── After JOIN ─────────────────────

    [Fact]
    public void GetCompletions_AfterJoin_OffersTables()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users JOIN ", 25);

        Assert.Contains(items, item => item.Label == "orders" && item.Kind == CompletionItemKind.Table);
    }

    // ───────────────────── After WHERE ─────────────────────

    [Fact]
    public void GetCompletions_AfterWhere_OffersColumnsAndExpressionKeywords()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users WHERE ", 26);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "AND" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "OR" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── After ORDER BY ─────────────────────

    [Fact]
    public void GetCompletions_AfterOrderBy_OffersColumnsAndSortDirections()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM users ORDER BY ", 29);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "ASC" && item.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, item => item.Label == "DESC" && item.Kind == CompletionItemKind.Keyword);
    }

    // ───────────────────── Dot-qualified ─────────────────────

    [Fact]
    public void GetCompletions_AfterDot_OffersColumnsFromQualifiedTable()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT users.", 13);

        Assert.Contains(items, item => item.Label == "id");
        Assert.Contains(items, item => item.Label == "name");
        Assert.Contains(items, item => item.Label == "email");
        // Columns from other tables should NOT appear.
        Assert.DoesNotContain(items, item => item.Label == "order_id");
    }

    [Fact]
    public void GetCompletions_AfterDotWithPrefix_FiltersQualifiedColumns()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT users.na", 15);

        Assert.Contains(items, item => item.Label == "name");
        Assert.DoesNotContain(items, item => item.Label == "id");
    }

    // ───────────────────── INTO / AS — no completions ─────────────────────

    [Fact]
    public void GetCompletions_AfterInto_ReturnsEmpty()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM t INTO ", 21);

        Assert.Empty(items);
    }

    [Fact]
    public void GetCompletions_AfterAs_ReturnsEmpty()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT x AS ", 12);

        Assert.Empty(items);
    }

    // ───────────────────── Function arguments ─────────────────────

    [Fact]
    public void GetCompletions_InsideFunctionArgs_OffersColumnsAndFunctions()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT abs(", 11);

        Assert.Contains(items, item => item.Label == "id" && item.Kind == CompletionItemKind.Column);
        Assert.Contains(items, item => item.Label == "abs" && item.Kind == CompletionItemKind.Function);
    }

    // ───────────────────── Sort order ─────────────────────

    [Fact]
    public void GetCompletions_ResultsSortedBySortOrderThenLabel()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        // Tables should appear before TVFs (both SortOrder 0 or 1), and within a group, alphabetical.
        int usersIndex = Array.FindIndex(items, item => item.Label == "users");
        int ordersIndex = Array.FindIndex(items, item => item.Label == "orders");
        Assert.True(ordersIndex < usersIndex, "Items should be sorted alphabetically within their sort order.");
    }

    // ───────────────────── Function insert text ─────────────────────

    [Fact]
    public void GetCompletions_FunctionItem_HasParenthesisInInsertText()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT ", 7);

        CompletionItem? absItem = Array.Find(items, item => item.Label == "abs");
        Assert.NotNull(absItem);
        Assert.Equal("abs(", absItem.InsertText);
    }

    // ───────────────────── Quoted table name insert text ─────────────────────

    [Fact]
    public void GetCompletions_TableWithDot_HasBracketQuotedInsertText()
    {
        LanguageServerManifest manifest = new()
        {
            Tables =
            [
                new TableSchemaEntry
                {
                    Name = "adult.data",
                    Columns = [new TableColumnEntry { Name = "age", Kind = "Float32", Nullable = false }],
                },
            ],
            Functions = [],
            Keywords = ["SELECT", "FROM"],
        };
        CompletionProvider provider = new(manifest);

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        CompletionItem? tableItem = Array.Find(items, item => item.Label == "adult.data");
        Assert.NotNull(tableItem);
        Assert.Equal("\"adult.data\"", tableItem.InsertText);
    }

    [Fact]
    public void GetCompletions_SafeTableName_HasNullInsertText()
    {
        CompletionProvider provider = CreateProvider();

        CompletionItem[] items = provider.GetCompletions("SELECT * FROM ", 14);

        CompletionItem? usersItem = Array.Find(items, item => item.Label == "users");
        Assert.NotNull(usersItem);
        Assert.Null(usersItem.InsertText);
    }
}
