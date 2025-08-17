namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="SemanticAnalyzer"/> — semantic diagnostics for unknown
/// tables, columns, and functions against a <see cref="LanguageServerManifest"/>.
/// </summary>
public sealed class SemanticAnalyzerTests
{
    /// <summary>Builds a minimal manifest with the specified tables and functions.</summary>
    private static LanguageServerManifest CreateManifest(
        IReadOnlyList<TableSchemaEntry>? tables = null,
        IReadOnlyList<FunctionSignature>? functions = null)
    {
        return new LanguageServerManifest
        {
            Tables = tables ?? [],
            Functions = functions ?? [],
            Keywords = ["SELECT", "FROM", "WHERE"],
        };
    }

    private static TableSchemaEntry Table(string name, params string[] columns)
    {
        List<TableColumnEntry> columnEntries = new();
        foreach (string column in columns)
        {
            columnEntries.Add(new TableColumnEntry { Name = column, Kind = "Float32", Nullable = false });
        }

        return new TableSchemaEntry { Name = name, Columns = columnEntries };
    }

    private static FunctionSignature Function(string name, params string[] parameterNames)
    {
        List<ParameterSignature> parameters = new();
        foreach (string parameterName in parameterNames)
        {
            parameters.Add(new ParameterSignature { Name = parameterName, Kind = "Any" });
        }

        return new FunctionSignature { Name = name, Parameters = parameters };
    }

    // ───────────────────── Valid SQL ─────────────────────

    [Fact]
    public void Analyze_ValidQuery_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id, name FROM users", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_ValidQualifiedColumn_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT users.id FROM users", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_ValidJoin_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name"), Table("orders", "id", "user_id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT u.name, o.user_id FROM users AS u JOIN orders AS o ON u.id = o.user_id",
            manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_ValidFunction_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")],
            functions: [Function("upper", "value")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT upper(x) FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Unknown table ─────────────────────

    [Fact]
    public void Analyze_UnknownTable_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM unknown_table", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("unknown_table"));
    }

    [Fact]
    public void Analyze_UnknownTable_HasAccurateSpan()
    {
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM bad_table", manifest);

        Diagnostic tableWarning = Assert.Single(diagnostics,
            diagnostic => diagnostic.Message.Contains("bad_table"));

        // "bad_table" starts at column 15 (0-based: 14), length 9.
        Assert.Equal(0, tableWarning.StartLine);
        Assert.Equal(14, tableWarning.StartColumn);
        Assert.Equal(14 + 9, tableWarning.EndColumn);
    }

    [Fact]
    public void Analyze_UnknownJoinTable_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT u.id FROM users AS u JOIN ghost AS g ON u.id = g.id",
            manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    // ───────────────────── Unknown column ─────────────────────

    [Fact]
    public void Analyze_UnknownColumn_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT missing_col FROM users", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("missing_col"));
    }

    [Fact]
    public void Analyze_UnknownQualifiedColumn_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT users.bogus FROM users", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("bogus") &&
            diagnostic.Message.Contains("users"));
    }

    [Fact]
    public void Analyze_UnknownColumnInWhere_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id FROM users WHERE phantom = 1", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("phantom"));
    }

    [Fact]
    public void Analyze_UnknownColumnInOrderBy_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id FROM users ORDER BY nope", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("nope"));
    }

    // ───────────────────── Unknown function ─────────────────────

    [Fact]
    public void Analyze_UnknownFunction_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT bogus_fn(x) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("bogus_fn"));
    }

    [Fact]
    public void Analyze_UnknownFunctionSource_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest();

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM missing_tvf('path')", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("missing_tvf"));
    }

    // ───────────────────── Case insensitivity ─────────────────────

    [Fact]
    public void Analyze_CaseInsensitiveTable_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("Users", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT id FROM users", manifest);

        // Should not warn about "users" even though manifest has "Users".
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("users", StringComparison.OrdinalIgnoreCase) &&
            diagnostic.Message.Contains("Unknown table"));
    }

    [Fact]
    public void Analyze_CaseInsensitiveColumn_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "Name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT name FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_CaseInsensitiveFunction_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")],
            functions: [Function("UPPER", "value")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT upper(x) FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Aliases ─────────────────────

    [Fact]
    public void Analyze_AliasedTable_AcceptsAliasQualifiedColumns()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT u.id FROM users AS u", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_UnknownAliasQualifier_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT z.id FROM users AS u", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("z"));
    }

    // ───────────────────── Opaque sources ─────────────────────

    [Fact]
    public void Analyze_SubquerySource_SkipsColumnValidation()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("users", "id", "name")]);

        // Subquery columns are opaque — we should not warn about "sub.x".
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT sub.x FROM (SELECT id FROM users) AS sub", manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column"));
    }

    [Fact]
    public void Analyze_FunctionSource_SkipsColumnValidation()
    {
        LanguageServerManifest manifest = CreateManifest(
            functions: [Function("read_csv", "path")]);

        // Function source columns are opaque.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT f.col FROM read_csv('data.csv') AS f", manifest);

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unknown column"));
    }

    // ───────────────────── SELECT * / table.* ─────────────────────

    [Fact]
    public void Analyze_SelectAll_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT * FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_SelectTableStar_KnownTable_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT t.* FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_SelectTableStar_UnknownTable_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT z.* FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("z"));
    }

    // ───────────────────── No manifest ─────────────────────

    [Fact]
    public void Analyze_NoManifest_ValidSql_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM unknown_table");

        // Without a manifest, no semantic validation occurs.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_NoManifest_InvalidSql_ReturnsParseError()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELEKT x");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    // ───────────────────── Multiple diagnostics ─────────────────────

    [Fact]
    public void Analyze_MultipleIssues_ReturnsMultipleDiagnostics()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT bogus_fn(missing_col) FROM t", manifest);

        Assert.True(diagnostics.Length >= 2,
            $"Expected at least 2 diagnostics but got {diagnostics.Length}.");
    }

    // ───────────────────── Expression nesting ─────────────────────

    [Fact]
    public void Analyze_UnknownColumnInBinaryExpression_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x + missing FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("missing"));
    }

    [Fact]
    public void Analyze_UnknownColumnInCastExpression_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CAST(ghost AS int) FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    [Fact]
    public void Analyze_SubqueryExpression_RecursesIntoSubquery()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x"), Table("inner_t", "y")]);

        // Use a scalar subquery (parenthesized SELECT) rather than IN (SELECT ...)
        // because the parser's IN postfix expects an expression list, not a subquery.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT x FROM t WHERE x = (SELECT phantom FROM inner_t)", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("phantom"));
    }

    // ───────────────────── JOIN ON condition ─────────────────────

    [Fact]
    public void Analyze_UnknownColumnInJoinCondition_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("a", "id"), Table("b", "id")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT a.id FROM a JOIN b ON a.id = b.ghost", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    // ───────────────────── CASE expression ─────────────────────

    [Fact]
    public void Analyze_ValidCaseExpression_ReturnsEmpty()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x", "label")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CASE WHEN x > 0 THEN label ELSE 'none' END FROM t", manifest);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Analyze_UnknownColumnInCaseCondition_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CASE WHEN ghost > 0 THEN 'yes' ELSE 'no' END FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("ghost"));
    }

    [Fact]
    public void Analyze_UnknownColumnInCaseResult_ReturnsWarning()
    {
        LanguageServerManifest manifest = CreateManifest(
            tables: [Table("t", "x")]);

        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT CASE WHEN x > 0 THEN phantom ELSE 'no' END FROM t", manifest);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("phantom"));
    }
}
