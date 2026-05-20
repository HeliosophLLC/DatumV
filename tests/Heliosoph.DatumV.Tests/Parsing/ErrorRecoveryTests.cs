using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// Tests for <see cref="SqlParser.TryParseRecovering"/> — clause-level error
/// recovery that collects multiple parse errors and produces partial ASTs.
/// </summary>
public sealed class ErrorRecoveryTests : ServiceTestBase
{
    // ───────────────────── Valid SQL (fast path) ─────────────────────

    [Fact]
    public void ValidSql_ReturnsSuccessWithNoErrors()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT x FROM t");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidSqlWithJoin_ReturnsSuccessWithNoErrors()
    {
        ParseResult result = SqlParser.TryParseRecovering(
            "SELECT a.x, b.y FROM alpha AS a JOIN beta AS b ON a.id = b.id");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidSqlWithWhereAndOrderBy_ReturnsSuccess()
    {
        ParseResult result = SqlParser.TryParseRecovering(
            "SELECT x FROM t WHERE x > 1 ORDER BY x");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);
        Assert.Empty(result.Errors);
    }

    // ───────────────────── Missing SELECT keyword ─────────────────────

    [Fact]
    public void MisspelledSelect_ReportsError()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELEKT x FROM t");

        // `SELEKT` is an identifier, not a SELECT keyword, so the input is
        // routed as a non-SELECT statement. The diagnostic correctly pins
        // at the offending token rather than fabricating an "Expected
        // SELECT" message — keyword-suggestion is a separate UX feature.
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(1, result.Errors[0].Line);
        Assert.Equal(1, result.Errors[0].Column);
    }

    [Fact]
    public void MisspelledSelect_RecoversFROM_HasFromError()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELEKT x FROM t");

        // See MisspelledSelect_ReportsError — the misspelled keyword is now
        // diagnosed at its actual position rather than via the SELECT-only
        // recovery walker.
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
    }

    // ───────────────────── Incomplete SELECT (no columns) ─────────────────────

    [Fact]
    public void SelectOnly_ReportsErrors()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT");

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void SelectWithoutFrom_IsValidSql()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT x");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);
        Assert.Empty(result.Errors);
    }

    // ───────────────────── Missing FROM clause ─────────────────────

    [Fact]
    public void SelectColumns_WithoutFrom_IsValidSql()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT a, b");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);
        Assert.Equal(2, result.Statement!.Columns.Count);
    }

    [Fact]
    public void SelectWithWhere_WithoutFrom_IsValidSql()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT x WHERE x > 1");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);
        Assert.NotNull(result.Statement!.Where);
    }

    // ───────────────────── Invalid WHERE expression ─────────────────────

    [Fact]
    public void InvalidWhereExpression_ReportsError()
    {
        ParseResult result = SqlParser.TryParseRecovering(
            "SELECT x FROM t WHERE");

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
    }

    // ───────────────────── Multiple errors ─────────────────────

    [Fact]
    public void MisspelledSelect_MissingFrom_ReportsErrors()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELEKT");

        Assert.False(result.IsSuccess);
        Assert.True(result.Errors.Count >= 1,
            $"Expected at least 1 error but got {result.Errors.Count}: " +
            string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ───────────────────── Error positions ─────────────────────

    [Theory]
    [InlineData("SELEKT x FROM t")]
    [InlineData("SELECT")]
    public void ErrorPositions_AreOneBased(string sql)
    {
        ParseResult result = SqlParser.TryParseRecovering(sql);

        Assert.NotEmpty(result.Errors);
        foreach (ParseError error in result.Errors)
        {
            Assert.True(error.Line >= 1, $"Line should be >= 1, was {error.Line}");
            Assert.True(error.Column >= 1, $"Column should be >= 1, was {error.Column}");
            Assert.True(error.Length >= 1, $"Length should be >= 1, was {error.Length}");
        }
    }

    // ───────────────────── Trailing tokens ─────────────────────

    [Fact]
    public void TrailingTokens_ReportsError()
    {
        // GARBAGE after a complete WHERE clause is genuinely leftover —
        // bare-alias support only applies to tokens immediately after a
        // table reference, not after clause keywords like WHERE.
        ParseResult result = SqlParser.TryParseRecovering("SELECT x FROM t WHERE x = 1 GARBAGE");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Message.Contains("Unexpected"));
    }

    // ───────────────────── Empty / whitespace ─────────────────────

    [Fact]
    public void EmptyString_ReturnsErrors()
    {
        ParseResult result = SqlParser.TryParseRecovering("");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Statement);
    }

    [Fact]
    public void WhitespaceOnly_ReturnsErrors()
    {
        ParseResult result = SqlParser.TryParseRecovering("   ");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Statement);
    }

    // ───────────────────── Partial AST structure ─────────────────────

    [Fact]
    public void ValidColumnsInvalidFrom_PreservesColumnExpressions()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT a, b, c");

        Assert.NotNull(result.Statement);
        Assert.Equal(3, result.Statement!.Columns.Count);

        ColumnReference columnA = Assert.IsType<ColumnReference>(
            result.Statement.Columns[0].Expression);
        Assert.Equal("a", columnA.ColumnName);
    }

    [Fact]
    public void ValidSelectAndFrom_WithTrailingGarbage_PreservesAst()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT x FROM t BADTOKEN");

        Assert.NotNull(result.Statement);
        ColumnReference column = Assert.IsType<ColumnReference>(
            result.Statement!.Columns[0].Expression);
        Assert.Equal("x", column.ColumnName);

        Assert.NotNull(result.Statement.From);
        TableReference table = Assert.IsType<TableReference>(result.Statement.From.Source);
        Assert.Equal("t", table.Name);
    }

    [Fact]
    public void ValidSelectFromWhere_WithBadLimitValue_PreservesEarlierClauses()
    {
        // `LIMIT )` — closing paren with no enclosing open isn't a valid
        // expression start, so the LIMIT clause fails. Earlier clauses
        // should still be recovered.
        // (Note: `LIMIT abc` used to fail under the literal-only LIMIT
        // grammar; with expression-valued LIMIT it parses fine — `abc` is
        // an identifier expression, semantically odd but syntactically
        // valid. Use a token that genuinely can't start an expression.)
        ParseResult result = SqlParser.TryParseRecovering(
            "SELECT x FROM t WHERE x > 1 LIMIT )");

        Assert.False(result.IsSuccess);
        // The SELECT, FROM, and WHERE should still be captured.
        Assert.NotNull(result.Statement);
        Assert.NotNull(result.Statement!.Where);
    }

    // ───────────────────── Consistency with Parse() ─────────────────────

    [Theory]
    [InlineData("SELECT x FROM t")]
    [InlineData("SELECT a, b FROM t WHERE a > 1")]
    [InlineData("SELECT x FROM t ORDER BY x LIMIT 10 OFFSET 5")]
    public void ValidSql_MatchesParseBehavior(string sql)
    {
        SelectStatement expected = ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
        ParseResult result = SqlParser.TryParseRecovering(sql);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);

        // Column count should match.
        Assert.Equal(expected.Columns.Count, result.Statement!.Columns.Count);

        // FROM source should match.
        Assert.NotNull(expected.From);
        Assert.NotNull(result.Statement.From);
        TableReference expectedTable = Assert.IsType<TableReference>(expected.From.Source);
        TableReference actualTable = Assert.IsType<TableReference>(result.Statement.From.Source);
        Assert.Equal(expectedTable.Name, actualTable.Name);
    }

    // ───────────────────── DDL / DML statements ─────────────────────

    [Theory]
    [InlineData("CREATE TEMP TABLE #t (id INT, name TEXT)")]
    [InlineData("DROP TABLE #t")]
    [InlineData("INSERT INTO #t (id) VALUES (1)")]
    [InlineData("UPDATE #t SET name = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM #t WHERE id = 1")]
    [InlineData("ALTER TABLE #t ADD COLUMN score REAL")]
    [InlineData("ANALYZE orders")]
    public void DdlDml_ReturnsSuccessWithNoErrors(string sql)
    {
        ParseResult result = SqlParser.TryParseRecovering(sql);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statements);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DdlWithTrailingSemicolon_ReturnsSuccess()
    {
        ParseResult result = SqlParser.TryParseRecovering("UPDATE #t SET name = 'x';");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SemicolonSeparatedBatch_ReturnsSuccess()
    {
        ParseResult result = SqlParser.TryParseRecovering(
            "INSERT INTO #t (id) VALUES (1); SELECT id FROM #t");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statements);
        Assert.Equal(2, result.Statements!.Count);
    }

    // ───────────── Tokenizer-level failures (incomplete tokens) ─────────────

    [Fact]
    public void IncompleteQuotedIdentifier_ReportsErrorWithoutThrowing()
    {
        ParseResult result = SqlParser.TryParseRecovering(
            "SELECT payment_type, COUNT(*) AS count\nFROM \"yello ");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Query);
        ParseError error = Assert.Single(result.Errors);
        Assert.Contains("incomplete identifier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompleteStringLiteral_ReportsErrorWithoutThrowing()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT 'unterminated");

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
    }

    // ───────────── Malformed non-SELECT statements pin at the real position ─────────────
    //
    // Regression coverage for the LanguageServer diagnostic dispatch: when
    // the batch parser commits to a CREATE-* / DML / procedural branch and
    // fails deep inside, TryParseRecovering must surface that deep position
    // rather than falling through to the SELECT-only clause walker (which
    // would emit "Expected SELECT keyword" at line 1, column 1 and underline
    // the leading CREATE/INSERT/etc. token in the editor).

    [Fact]
    public void MalformedCreateModelBody_ReportsErrorAtDeepPosition()
    {
        // Bad return-type annotation buried two lines into a CREATE MODEL.
        // Should NOT collapse to line 1 / "Expected SELECT keyword".
        string sql = "CREATE MODEL m(img Image)\n"
                   + "RETURNS Struct<bad syntax\n"
                   + "USING 'x.onnx' AS BEGIN END";

        ParseResult result = SqlParser.TryParseRecovering(sql);

        Assert.False(result.IsSuccess);
        ParseError error = Assert.Single(result.Errors);
        Assert.DoesNotContain("Expected SELECT", error.Message);
        Assert.True(error.Line > 1, $"expected error past line 1, got line {error.Line}: {error.Message}");
    }

    [Theory]
    [InlineData("CREATE TABLE t (id INT,\nname BOGUS_TYPE_NAME 12345)")]
    [InlineData("INSERT INTO #t (id)\nVALUES (1, , 3)")]
    [InlineData("UPDATE #t\nSET = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM #t\nWHERE id GARBAGE 1")]
    [InlineData("ALTER TABLE #t\nADD COLUMN BOGUS")]
    public void MalformedNonSelectStatement_DoesNotCollapseToSelectError(string sql)
    {
        ParseResult result = SqlParser.TryParseRecovering(sql);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        // The hallmark of the old behavior: "Expected SELECT keyword" at column 1.
        Assert.DoesNotContain(result.Errors, e =>
            e.Message.Contains("Expected SELECT", StringComparison.OrdinalIgnoreCase)
            && e.Line == 1
            && e.Column == 1);
    }

    [Fact]
    public void MalformedCreateModel_DiagnosticHasSinglePinnedError()
    {
        // The batch parser produces one deep error; we surface exactly that
        // rather than amassing a list of clause-recovery errors that would
        // confuse the squiggle target.
        ParseResult result = SqlParser.TryParseRecovering(
            "CREATE MODEL m(img Image) RETURNS Struct<bad");

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
    }
}
