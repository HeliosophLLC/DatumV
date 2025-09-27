using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for the PostgreSQL EXTRACT(field FROM source) syntax,
/// which desugars to <c>date_part('field', source)</c> at parse time.
/// </summary>
public class ExtractFunctionTests
{
    private static SelectStatement Parse(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    // ───────────────────── Parsing / desugaring ─────────────────────

    [Fact]
    public void Extract_DesugarsToDatePart()
    {
        SelectStatement result = Parse("SELECT EXTRACT(year FROM x) FROM t");

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("date_part", func.FunctionName);
        Assert.Equal(2, func.Arguments.Count);

        LiteralExpression field = Assert.IsType<LiteralExpression>(func.Arguments[0]);
        Assert.Equal("year", field.Value);

        ColumnReference col = Assert.IsType<ColumnReference>(func.Arguments[1]);
        Assert.Equal("x", col.ColumnName);
    }

    [Fact]
    public void Extract_FieldIsCaseInsensitive()
    {
        SelectStatement result = Parse("SELECT EXTRACT(MONTH FROM x) FROM t");

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        LiteralExpression field = Assert.IsType<LiteralExpression>(func.Arguments[0]);
        Assert.Equal("month", field.Value);
    }

    [Theory]
    [InlineData("year")]
    [InlineData("month")]
    [InlineData("day")]
    [InlineData("hour")]
    [InlineData("minute")]
    [InlineData("second")]
    [InlineData("quarter")]
    [InlineData("week")]
    [InlineData("dow")]
    [InlineData("doy")]
    [InlineData("epoch")]
    [InlineData("century")]
    [InlineData("decade")]
    [InlineData("millennium")]
    [InlineData("isodow")]
    [InlineData("isoyear")]
    [InlineData("julian")]
    [InlineData("millisecond")]
    [InlineData("microsecond")]
    public void Extract_AllPostgresFields_Parse(string field)
    {
        SelectStatement result = Parse($"SELECT EXTRACT({field} FROM x) FROM t");

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("date_part", func.FunctionName);

        LiteralExpression fieldExpr = Assert.IsType<LiteralExpression>(func.Arguments[0]);
        Assert.Equal(field, fieldExpr.Value);
    }

    [Fact]
    public void Extract_WithComplexSourceExpression()
    {
        SelectStatement result = Parse("SELECT EXTRACT(hour FROM created_at) FROM t");

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("date_part", func.FunctionName);

        LiteralExpression field = Assert.IsType<LiteralExpression>(func.Arguments[0]);
        Assert.Equal("hour", field.Value);

        ColumnReference col = Assert.IsType<ColumnReference>(func.Arguments[1]);
        Assert.Equal("created_at", col.ColumnName);
    }

    [Fact]
    public void Extract_WithAlias()
    {
        SelectStatement result = Parse("SELECT EXTRACT(year FROM x) AS yr FROM t");

        Assert.Equal("yr", result.Columns[0].Alias);
        Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
    }

    [Fact]
    public void Extract_TimeField_ParsesCorrectly()
    {
        // 'time' is a keyword token (SqlToken.Time), so this tests the .Or(Token.EqualTo(SqlToken.Time)) branch.
        SelectStatement result = Parse("SELECT EXTRACT(time FROM x) FROM t");

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        LiteralExpression field = Assert.IsType<LiteralExpression>(func.Arguments[0]);
        Assert.Equal("time", field.Value);
    }

    [Fact]
    public void Extract_HasSourceSpan()
    {
        SelectStatement result = Parse("SELECT EXTRACT(year FROM x) FROM t");

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.NotNull(func.Span);
    }
}
