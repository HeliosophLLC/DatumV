using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Verifies that <see cref="SourceSpan"/> values are populated on AST nodes
/// that carry identifiers (tables, columns, functions) during parsing.
/// </summary>
public sealed class SourceSpanTests : ServiceTestBase
{
    private static SelectStatement Parse(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    // ───────────────────── Column references ─────────────────────

    [Fact]
    public void UnqualifiedColumn_HasSpan()
    {
        //                   0123456789...
        SelectStatement result = Parse("SELECT name FROM t");

        ColumnReference column = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.NotNull(column.Span);
        Assert.Equal(1, column.Span.Line);
        Assert.Equal(8, column.Span.Column);
        Assert.Equal(4, column.Span.Length);
    }

    [Fact]
    public void QualifiedColumn_HasSpanCoveringFullReference()
    {
        SelectStatement result = Parse("SELECT t.name FROM t");

        ColumnReference column = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.NotNull(column.Span);
        Assert.Equal(1, column.Span.Line);
        Assert.Equal(8, column.Span.Column);
        // "t.name" = 6 characters
        Assert.Equal(6, column.Span.Length);
    }

    [Fact]
    public void QualifiedStar_SelectTableColumns_InnerReferenceHasNoSpan()
    {
        // SELECT t.* is parsed as SelectTableColumns, which synthesizes
        // its own inner ColumnReference without a span. The span lives
        // on the SelectTableColumns node instead.
        SelectStatement result = Parse("SELECT t.* FROM t");

        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.NotNull(tableColumns.Span);
        Assert.Equal(3, tableColumns.Span.Length);

        // The synthesized inner ColumnReference has no span.
        ColumnReference inner = Assert.IsType<ColumnReference>(result.Columns[0].Expression);
        Assert.Null(inner.Span);
    }

    // ───────────────────── Table references ─────────────────────

    [Fact]
    public void TableReference_HasSpan()
    {
        SelectStatement result = Parse("SELECT x FROM users");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.NotNull(table.Span);
        Assert.Equal(1, table.Span.Line);
        Assert.Equal(15, table.Span.Column);
        Assert.Equal(5, table.Span.Length);
    }

    [Fact]
    public void TableReference_WithAlias_SpanCoversNameOnly()
    {
        SelectStatement result = Parse("SELECT x FROM users AS u");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.NotNull(table.Span);
        Assert.Equal("users", table.Name);
        // Span should cover "users", not "users AS u".
        Assert.Equal(5, table.Span.Length);
    }

    // ───────────────────── Function calls ─────────────────────

    [Fact]
    public void FunctionCallExpression_HasSpanOnName()
    {
        SelectStatement result = Parse("SELECT upper(x) FROM t");

        FunctionCallExpression functionCall = Assert.IsType<FunctionCallExpression>(
            result.Columns[0].Expression);
        Assert.NotNull(functionCall.Span);
        Assert.Equal(1, functionCall.Span.Line);
        Assert.Equal(8, functionCall.Span.Column);
        Assert.Equal(5, functionCall.Span.Length);
    }

    // ───────────────────── CAST expressions ─────────────────────

    [Fact]
    public void CastExpression_HasSpan()
    {
        SelectStatement result = Parse("SELECT CAST(x AS int) FROM t");

        CastExpression cast = Assert.IsType<CastExpression>(result.Columns[0].Expression);
        Assert.NotNull(cast.Span);
        Assert.Equal(1, cast.Span.Line);
        // CAST spans from 'CAST' to the closing ')'.
        Assert.Equal(8, cast.Span.Column);
    }

    // ───────────────────── FunctionSource ─────────────────────

    [Fact]
    public void FunctionSource_HasSpan()
    {
        SelectStatement result = Parse("SELECT x FROM read_csv('data.csv')");

        Assert.NotNull(result.From);
        FunctionSource functionSource = Assert.IsType<FunctionSource>(result.From.Source);
        Assert.NotNull(functionSource.Span);
        Assert.Equal(1, functionSource.Span.Line);
        Assert.Equal(15, functionSource.Span.Column);
        Assert.Equal(8, functionSource.Span.Length);
    }

    // ───────────────────── SelectTableColumns ─────────────────────

    [Fact]
    public void SelectTableColumns_HasSpan()
    {
        SelectStatement result = Parse("SELECT t.* FROM t");

        SelectTableColumns tableColumns = Assert.IsType<SelectTableColumns>(result.Columns[0]);
        Assert.NotNull(tableColumns.Span);
        Assert.Equal(1, tableColumns.Span.Line);
        Assert.Equal(8, tableColumns.Span.Column);
        // "t.*" = 3 characters
        Assert.Equal(3, tableColumns.Span.Length);
    }

    // ───────────────────── JOIN table reference ─────────────────────

    [Fact]
    public void JoinTableReference_HasSpan()
    {
        SelectStatement result = Parse(
            "SELECT a.x FROM alpha AS a JOIN beta AS b ON a.x = b.x");

        Assert.NotNull(result.Joins);
        TableReference joinTable = Assert.IsType<TableReference>(result.Joins[0].Source);
        Assert.NotNull(joinTable.Span);
        Assert.Equal("beta", joinTable.Name);
        Assert.Equal(4, joinTable.Span.Length);
    }

    // ───────────────────── Multiline positions ─────────────────────

    [Fact]
    public void MultilineQuery_SpanReflectsCorrectLine()
    {
        string sql = "SELECT x\nFROM users";
        SelectStatement result = Parse(sql);

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.NotNull(table.Span);
        Assert.Equal(2, table.Span.Line);
        Assert.Equal(6, table.Span.Column);
    }
}
