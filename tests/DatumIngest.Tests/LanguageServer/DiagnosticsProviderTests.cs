namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;

/// <summary>
/// Tests for <see cref="DiagnosticsProvider"/> — SQL parse error detection.
/// </summary>
public sealed class DiagnosticsProviderTests : ServiceTestBase
{
    // ───────────────────── Valid SQL ─────────────────────

    [Fact]
    public void GetDiagnostics_ValidSql_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELECT x FROM t");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_ValidSqlWithJoin_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT a.x, b.y FROM alpha AS a JOIN beta AS b ON a.id = b.id");

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Empty / whitespace ─────────────────────

    [Fact]
    public void GetDiagnostics_EmptyString_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_WhitespaceOnly_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("   ");

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Trailing semicolons ─────────────────────

    [Fact]
    public void GetDiagnostics_TrailingSemicolon_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELECT x FROM t;");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_MultipleTrailingSemicolons_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELECT x FROM t;;");

        Assert.Empty(diagnostics);
    }

    // ───────────────────── DDL / DML statements ─────────────────────

    [Theory]
    [InlineData("CREATE TEMP TABLE #t (id INT, name TEXT)")]
    [InlineData("DROP TABLE #t")]
    [InlineData("DROP TABLE IF EXISTS #t")]
    [InlineData("INSERT INTO #t (id) VALUES (1)")]
    [InlineData("UPDATE #t SET name = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM #t WHERE id = 1")]
    [InlineData("ALTER TABLE #t ADD COLUMN score REAL")]
    [InlineData("ANALYZE orders")]
    public void GetDiagnostics_ValidDdlDml_ReturnsEmpty(string sql)
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(sql);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_DdlWithTrailingSemicolon_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("UPDATE #t SET name = 'x';");

        Assert.Empty(diagnostics);
    }

    // ───────────────────── Parse errors ─────────────────────

    [Fact]
    public void GetDiagnostics_InvalidSql_ReturnsErrorDiagnostic()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELEKT x FROM t");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics[0].Message));
    }

    [Fact]
    public void GetDiagnostics_IncompleteSql_ReturnsError()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELECT");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    [Fact]
    public void GetDiagnostics_ErrorPosition_IsZeroBased()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELEKT x FROM t");

        Assert.NotEmpty(diagnostics);
        // Superpower positions are 1-based; diagnostics should be 0-based.
        Assert.True(diagnostics[0].StartLine >= 0);
        Assert.True(diagnostics[0].StartColumn >= 0);
    }

    [Fact]
    public void GetDiagnostics_ErrorSpan_HasPositiveWidth()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics("SELEKT x");

        Assert.NotEmpty(diagnostics);
        // EndColumn should be at least StartColumn + 1 to highlight something.
        Assert.True(diagnostics[0].EndColumn > diagnostics[0].StartColumn ||
                     diagnostics[0].EndLine > diagnostics[0].StartLine);
    }

    // ───────────────────── Template strings ─────────────────────

    [Fact]
    public void GetDiagnostics_ValidTemplateString_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT `Hello ${name}` FROM t");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_TemplateStringWithoutSplices_ReturnsEmpty()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT `plain string` FROM t");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_UnterminatedTemplateString_ReturnsError()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT `unterminated");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    [Fact]
    public void GetDiagnostics_SpliceWithSyntaxError_ReturnsError()
    {
        // Splice expression is malformed: `1 +` is not a complete expression.
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT `text ${1 +} more`");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    [Fact]
    public void GetDiagnostics_EmptySplice_ReturnsError()
    {
        Diagnostic[] diagnostics = DiagnosticsProvider.GetDiagnostics(
            "SELECT `${}` FROM t");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }
}
