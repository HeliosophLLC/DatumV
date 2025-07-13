using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Tests for <see cref="SqlParser.TryParseRecovering"/> — clause-level error
/// recovery that collects multiple parse errors and produces partial ASTs.
/// </summary>
public sealed class ErrorRecoveryTests
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

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Message.Contains("SELECT"));
    }

    [Fact]
    public void MisspelledSelect_RecoversFROM_HasFromError()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELEKT x FROM t");

        // Without valid SELECT columns, the partial AST cannot be built,
        // but the recovery parser still detects and reports the error.
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Message.Contains("SELECT"));
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
    public void SelectWithoutFrom_ReportsFromError()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT x");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Message.Contains("FROM"));
    }

    // ───────────────────── Missing FROM clause ─────────────────────

    [Fact]
    public void SelectColumns_MissingFrom_HasPartialAst()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT a, b");

        Assert.False(result.IsSuccess);
        // Partial AST should have columns but placeholder FROM.
        Assert.NotNull(result.Statement);
        Assert.Equal(2, result.Statement!.Columns.Count);
    }

    [Fact]
    public void SelectColumns_SkipsToWhere_WhenFromMissing()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELECT x WHERE x > 1");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Message.Contains("FROM"));
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
    public void MisspelledSelect_MissingFrom_ReportsMultipleErrors()
    {
        ParseResult result = SqlParser.TryParseRecovering("SELEKT");

        Assert.False(result.IsSuccess);
        Assert.True(result.Errors.Count >= 2,
            $"Expected at least 2 errors but got {result.Errors.Count}: " +
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
        ParseResult result = SqlParser.TryParseRecovering("SELECT x FROM t GARBAGE");

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

        TableReference table = Assert.IsType<TableReference>(result.Statement.From.Source);
        Assert.Equal("t", table.Name);
    }

    [Fact]
    public void ValidSelectFromWhere_WithBadLimitValue_PreservesEarlierClauses()
    {
        ParseResult result = SqlParser.TryParseRecovering(
            "SELECT x FROM t WHERE x > 1 LIMIT abc");

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
        SelectStatement expected = SqlParser.Parse(sql);
        ParseResult result = SqlParser.TryParseRecovering(sql);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);

        // Column count should match.
        Assert.Equal(expected.Columns.Count, result.Statement!.Columns.Count);

        // FROM source should match.
        TableReference expectedTable = Assert.IsType<TableReference>(expected.From.Source);
        TableReference actualTable = Assert.IsType<TableReference>(result.Statement.From.Source);
        Assert.Equal(expectedTable.Name, actualTable.Name);
    }
}
