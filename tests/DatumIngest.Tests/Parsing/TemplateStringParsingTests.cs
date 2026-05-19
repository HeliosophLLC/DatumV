using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// Tests for backtick-delimited template strings: <c>`text ${expr} more text`</c>.
/// The parser lowers these to a <c>concat(...)</c> call interleaving literal
/// chunks with parsed splice expressions, so the rest of the engine sees no
/// new AST node — it sees a normal <see cref="FunctionCallExpression"/>.
/// </summary>
public class TemplateStringParsingTests : ServiceTestBase
{
    private static Expression ParseExpression(string templateLiteral)
    {
        SelectQueryExpression query = (SelectQueryExpression)SqlParser.Parse(
            $"SELECT {templateLiteral}");
        return query.Statement.Columns[0].Expression;
    }

    [Fact]
    public void TemplateWithNoSplicesProducesStringLiteral()
    {
        Expression expression = ParseExpression("`hello world`");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(expression);
        Assert.Equal("hello world", literal.Value);
    }

    [Fact]
    public void EmptyTemplateProducesEmptyStringLiteral()
    {
        Expression expression = ParseExpression("``");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(expression);
        Assert.Equal(string.Empty, literal.Value);
    }

    [Fact]
    public void TemplateWithSingleSpliceLowersToConcatCall()
    {
        Expression expression = ParseExpression("`hello ${name}`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        Assert.Equal("concat", call.FunctionName);
        Assert.Equal(2, call.Arguments.Count);

        LiteralExpression prefix = Assert.IsType<LiteralExpression>(call.Arguments[0]);
        Assert.Equal("hello ", prefix.Value);

        ColumnReference splice = Assert.IsType<ColumnReference>(call.Arguments[1]);
        Assert.Equal("name", splice.ColumnName);
    }

    [Fact]
    public void TemplateWithLeadingSpliceLowersWithoutEmptyPrefix()
    {
        Expression expression = ParseExpression("`${name} is here`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        Assert.Equal("concat", call.FunctionName);
        Assert.Equal(2, call.Arguments.Count);

        ColumnReference splice = Assert.IsType<ColumnReference>(call.Arguments[0]);
        Assert.Equal("name", splice.ColumnName);

        LiteralExpression suffix = Assert.IsType<LiteralExpression>(call.Arguments[1]);
        Assert.Equal(" is here", suffix.Value);
    }

    [Fact]
    public void TemplateWithMultipleSplicesInterleavesLiteralsAndExpressions()
    {
        Expression expression = ParseExpression("`Tone: ${tone}, Threat: ${threat}`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        Assert.Equal("concat", call.FunctionName);
        Assert.Equal(4, call.Arguments.Count);

        Assert.Equal("Tone: ", Assert.IsType<LiteralExpression>(call.Arguments[0]).Value);
        Assert.Equal("tone", Assert.IsType<ColumnReference>(call.Arguments[1]).ColumnName);
        Assert.Equal(", Threat: ", Assert.IsType<LiteralExpression>(call.Arguments[2]).Value);
        Assert.Equal("threat", Assert.IsType<ColumnReference>(call.Arguments[3]).ColumnName);
    }

    [Fact]
    public void SpliceCanContainArbitraryExpression()
    {
        // A splice's contents are full-fledged scalar expressions, not just
        // bare identifiers.
        Expression expression = ParseExpression("`x + y = ${x + y}`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        BinaryExpression sum = Assert.IsType<BinaryExpression>(call.Arguments[1]);
        Assert.Equal(BinaryOperator.Add, sum.Operator);
    }

    [Fact]
    public void SpliceCanContainFunctionCall()
    {
        Expression expression = ParseExpression("`Result: ${upper(name)}`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        FunctionCallExpression upper = Assert.IsType<FunctionCallExpression>(call.Arguments[1]);
        Assert.Equal("upper", upper.FunctionName);
    }

    [Fact]
    public void SpliceCanContainQualifiedColumn()
    {
        Expression expression = ParseExpression("`Hello ${u.name}`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        ColumnReference column = Assert.IsType<ColumnReference>(call.Arguments[1]);
        Assert.Equal("u", column.TableName);
        Assert.Equal("name", column.ColumnName);
    }

    [Fact]
    public void SpliceCanContainStructLiteralWithBraces()
    {
        // Nested braces inside the splice must not close the splice prematurely.
        // Struct field access uses bracket syntax (['a']) in this dialect.
        Expression expression = ParseExpression("`field = ${ {a: 1, b: 2}['a'] }`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        Assert.Equal(2, call.Arguments.Count);
        Assert.IsType<IndexAccessExpression>(call.Arguments[1]);
    }

    [Fact]
    public void EscapedDollarSuppressesSpliceAndYieldsLiteral()
    {
        Expression expression = ParseExpression(@"`literal \${name} text`");

        // No splice → single literal expression with the text reproduced verbatim
        // (with the leading backslash stripped per the escape rule).
        LiteralExpression literal = Assert.IsType<LiteralExpression>(expression);
        Assert.Equal("literal ${name} text", literal.Value);
    }

    [Fact]
    public void EscapedBacktickInsideBodyIsPartOfTheLiteral()
    {
        Expression expression = ParseExpression(@"`a \` b`");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(expression);
        Assert.Equal("a ` b", literal.Value);
    }

    [Fact]
    public void EscapedBackslashYieldsSingleBackslash()
    {
        Expression expression = ParseExpression(@"`path\\to\\file`");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(expression);
        Assert.Equal(@"path\to\file", literal.Value);
    }

    [Fact]
    public void MultilineBodyPreservesNewlines()
    {
        Expression expression = ParseExpression("`line1\n  line2 ${x}\n  line3`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        Assert.Equal("concat", call.FunctionName);
        Assert.Equal(3, call.Arguments.Count);

        LiteralExpression prefix = Assert.IsType<LiteralExpression>(call.Arguments[0]);
        Assert.Contains("\n", prefix.Value as string);
    }

    [Fact]
    public void ConsecutiveSplicesProduceNoEmptyLiteralBetween()
    {
        Expression expression = ParseExpression("`${a}${b}`");

        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(expression);
        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal("a", Assert.IsType<ColumnReference>(call.Arguments[0]).ColumnName);
        Assert.Equal("b", Assert.IsType<ColumnReference>(call.Arguments[1]).ColumnName);
    }

    [Fact]
    public void TemplateStringWorksInWhereClause()
    {
        // Should be usable anywhere an expression is — not just in SELECT.
        SelectQueryExpression query = (SelectQueryExpression)SqlParser.Parse(
            "SELECT a FROM t WHERE name = `hello ${suffix}`");

        BinaryExpression where = Assert.IsType<BinaryExpression>(query.Statement.Where);
        FunctionCallExpression rhs = Assert.IsType<FunctionCallExpression>(where.Right);
        Assert.Equal("concat", rhs.FunctionName);
    }

    [Fact]
    public void TemplateStringWorksInFunctionArgument()
    {
        // The most common real-world use: passing a built-up prompt to a
        // model invocation.
        SelectQueryExpression query = (SelectQueryExpression)SqlParser.Parse(
            "SELECT upper(`hello ${name}`) FROM t");

        FunctionCallExpression upper = Assert.IsType<FunctionCallExpression>(
            query.Statement.Columns[0].Expression);
        Assert.Equal("upper", upper.FunctionName);
        FunctionCallExpression concat = Assert.IsType<FunctionCallExpression>(upper.Arguments[0]);
        Assert.Equal("concat", concat.FunctionName);
    }

    [Fact]
    public void EmptySpliceFailsToParse()
    {
        // An empty splice has no expression to parse.
        Assert.Throws<ParseException>(() => ParseExpression("`text ${} more`"));
    }

    [Fact]
    public void SpliceWithSyntaxErrorFailsToParse()
    {
        Assert.Throws<ParseException>(() => ParseExpression("`text ${1 +} more`"));
    }
}
