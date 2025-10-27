using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Parsing.Tokens;
using Superpower;
using Superpower.Model;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Tests for parameterized query support: lexing, parsing, binding, and value parsing.
/// </summary>
public sealed class ParameterTests : ServiceTestBase
{
    // ───────────────────── Tokenizer ─────────────────────

    private static Token<SqlToken>[] Tokenize(string input)
    {
        Tokenizer<SqlToken> tokenizer = SqlTokenizer.Instance;
        TokenList<SqlToken> result = tokenizer.Tokenize(input);
        return result.ToArray();
    }

    [Theory]
    [InlineData("$threshold")]
    [InlineData("$name")]
    [InlineData("$_private")]
    [InlineData("$column1")]
    public void Tokenizer_RecognizesParameterTokens(string input)
    {
        Token<SqlToken>[] tokens = Tokenize(input);
        Assert.Single(tokens);
        Assert.Equal(SqlToken.Parameter, tokens[0].Kind);
    }

    [Fact]
    public void Tokenizer_ParameterInExpression()
    {
        Token<SqlToken>[] tokens = Tokenize("x > $threshold");
        Assert.Equal(3, tokens.Length);
        Assert.Equal(SqlToken.Identifier, tokens[0].Kind);
        Assert.Equal(SqlToken.GreaterThan, tokens[1].Kind);
        Assert.Equal(SqlToken.Parameter, tokens[2].Kind);
    }

    [Fact]
    public void Tokenizer_MultipleParameters()
    {
        Token<SqlToken>[] tokens = Tokenize("$a + $b");
        Assert.Equal(3, tokens.Length);
        Assert.Equal(SqlToken.Parameter, tokens[0].Kind);
        Assert.Equal(SqlToken.Plus, tokens[1].Kind);
        Assert.Equal(SqlToken.Parameter, tokens[2].Kind);
    }

    // ───────────────────── Parser ─────────────────────

    private static SelectStatement Parse(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    [Fact]
    public void Parser_ParameterInWhereClause()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x > $threshold");

        BinaryExpression where = Assert.IsType<BinaryExpression>(result.Where);
        Assert.Equal(BinaryOperator.GreaterThan, where.Operator);
        ParameterExpression parameter = Assert.IsType<ParameterExpression>(where.Right);
        Assert.Equal("threshold", parameter.Name);
    }

    [Fact]
    public void Parser_ParameterInSelectList()
    {
        SelectStatement result = Parse("SELECT $val AS computed FROM t");

        Assert.Single(result.Columns);
        ParameterExpression parameter = Assert.IsType<ParameterExpression>(result.Columns[0].Expression);
        Assert.Equal("val", parameter.Name);
        Assert.Equal("computed", result.Columns[0].Alias);
    }

    [Fact]
    public void Parser_ParameterInFunctionArgument()
    {
        SelectStatement result = Parse("SELECT concat($prefix, name) FROM t");

        FunctionCallExpression function = Assert.IsType<FunctionCallExpression>(result.Columns[0].Expression);
        Assert.Equal("concat", function.FunctionName);
        ParameterExpression parameter = Assert.IsType<ParameterExpression>(function.Arguments[0]);
        Assert.Equal("prefix", parameter.Name);
    }

    [Fact]
    public void Parser_ParameterInInList()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x IN ($a, $b, $c)");

        InExpression inExpr = Assert.IsType<InExpression>(result.Where);
        Assert.Equal(3, inExpr.Values.Count);
        Assert.Equal("a", Assert.IsType<ParameterExpression>(inExpr.Values[0]).Name);
        Assert.Equal("b", Assert.IsType<ParameterExpression>(inExpr.Values[1]).Name);
        Assert.Equal("c", Assert.IsType<ParameterExpression>(inExpr.Values[2]).Name);
    }

    [Fact]
    public void Parser_ParameterInBetween()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x BETWEEN $lo AND $hi");

        BetweenExpression between = Assert.IsType<BetweenExpression>(result.Where);
        Assert.Equal("lo", Assert.IsType<ParameterExpression>(between.Low).Name);
        Assert.Equal("hi", Assert.IsType<ParameterExpression>(between.High).Name);
    }

    [Fact]
    public void Parser_ParameterInCaseWhen()
    {
        SelectStatement result = Parse(
            "SELECT CASE WHEN x > $threshold THEN 1 ELSE 0 END FROM t");

        CaseExpression caseExpr = Assert.IsType<CaseExpression>(result.Columns[0].Expression);
        BinaryExpression condition = Assert.IsType<BinaryExpression>(caseExpr.WhenClauses[0].Condition);
        Assert.Equal("threshold", Assert.IsType<ParameterExpression>(condition.Right).Name);
    }

    [Fact]
    public void Parser_ParameterInJoinOn()
    {
        SelectStatement result = Parse(
            "SELECT * FROM a JOIN b ON a.id = b.id AND b.status = $status");

        Assert.NotNull(result.Joins);
        BinaryExpression onCondition = Assert.IsType<BinaryExpression>(result.Joins[0].OnCondition);
        Assert.Equal(BinaryOperator.And, onCondition.Operator);
        BinaryExpression right = Assert.IsType<BinaryExpression>(onCondition.Right);
        Assert.Equal("status", Assert.IsType<ParameterExpression>(right.Right).Name);
    }

    [Fact]
    public void Parser_ParameterInHaving()
    {
        SelectStatement result = Parse(
            "SELECT category, COUNT(*) AS cnt FROM t GROUP BY category HAVING COUNT(*) > $min_count");

        BinaryExpression having = Assert.IsType<BinaryExpression>(result.Having);
        Assert.Equal("min_count", Assert.IsType<ParameterExpression>(having.Right).Name);
    }

    [Fact]
    public void Parser_ParameterHasSourceSpan()
    {
        SelectStatement result = Parse("SELECT * FROM t WHERE x > $threshold");

        ParameterExpression parameter = Assert.IsType<ParameterExpression>(
            Assert.IsType<BinaryExpression>(result.Where).Right);
        Assert.NotNull(parameter.Span);
        Assert.Equal(1, parameter.Span.Line);
    }

    // ───────────────────── ParameterBinder ─────────────────────

    [Fact]
    public void Binder_SubstitutesParameterWithLiteral()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE x > $threshold");
        Dictionary<string, DataValue> parameters = new()
        {
            ["threshold"] = DataValue.FromFloat32(0.5f)
        };

        SelectStatement bound = ParameterBinder.Bind(statement, parameters);

        BinaryExpression where = Assert.IsType<BinaryExpression>(bound.Where);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(where.Right);
        Assert.IsType<float>(literal.Value);
    }

    [Fact]
    public void Binder_SubstitutesStringParameter()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE name = $name");
        Dictionary<string, DataValue> parameters = new()
        {
            ["name"] = DataValue.FromString("hello")
        };

        SelectStatement bound = ParameterBinder.Bind(statement, parameters);

        BinaryExpression where = Assert.IsType<BinaryExpression>(bound.Where);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(where.Right);
        Assert.Equal("hello", literal.Value);
    }

    [Fact]
    public void Binder_SubstitutesNullParameter()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE x > $val");
        Dictionary<string, DataValue> parameters = new()
        {
            ["val"] = DataValue.Null(DataKind.Float32)
        };

        SelectStatement bound = ParameterBinder.Bind(statement, parameters);

        BinaryExpression where = Assert.IsType<BinaryExpression>(bound.Where);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(where.Right);
        Assert.Null(literal.Value);
    }

    [Fact]
    public void Binder_SubstitutesBooleanParameter()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE active = $flag");
        Dictionary<string, DataValue> parameters = new()
        {
            ["flag"] = DataValue.FromBoolean(true)
        };

        SelectStatement bound = ParameterBinder.Bind(statement, parameters);

        BinaryExpression where = Assert.IsType<BinaryExpression>(bound.Where);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(where.Right);
        Assert.Equal(true, literal.Value);
    }

    [Fact]
    public void Binder_ThrowsOnUnboundParameter()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE x > $threshold");
        Dictionary<string, DataValue> parameters = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ParameterBinder.Bind(statement, parameters));
        Assert.Contains("$threshold", exception.Message);
    }

    [Fact]
    public void Binder_ThrowsOnUnusedParameter()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE x > $threshold");
        Dictionary<string, DataValue> parameters = new()
        {
            ["threshold"] = DataValue.FromFloat32(0.5f),
            ["extra"] = DataValue.FromFloat32(1.0f)
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ParameterBinder.Bind(statement, parameters));
        Assert.Contains("$extra", exception.Message);
    }

    [Fact]
    public void Binder_HandlesRepeatedParameter()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE x > $val AND y < $val");
        Dictionary<string, DataValue> parameters = new()
        {
            ["val"] = DataValue.FromFloat32(5.0f)
        };

        SelectStatement bound = ParameterBinder.Bind(statement, parameters);

        BinaryExpression and = Assert.IsType<BinaryExpression>(bound.Where);
        BinaryExpression left = Assert.IsType<BinaryExpression>(and.Left);
        BinaryExpression right = Assert.IsType<BinaryExpression>(and.Right);
        Assert.IsType<LiteralExpression>(left.Right);
        Assert.IsType<LiteralExpression>(right.Right);
    }

    [Fact]
    public void Binder_NoParametersIsPassthrough()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE x > 5");
        Dictionary<string, DataValue> parameters = new();

        SelectStatement bound = ParameterBinder.Bind(statement, parameters);

        Assert.Same(statement, bound);
    }

    [Fact]
    public void Binder_SubstitutesNestedExpressions()
    {
        SelectStatement statement = Parse(
            "SELECT CASE WHEN x > $threshold THEN $label ELSE 'default' END FROM t");
        Dictionary<string, DataValue> parameters = new()
        {
            ["threshold"] = DataValue.FromFloat32(0.5f),
            ["label"] = DataValue.FromString("high")
        };

        SelectStatement bound = ParameterBinder.Bind(statement, parameters);

        CaseExpression caseExpr = Assert.IsType<CaseExpression>(bound.Columns[0].Expression);
        BinaryExpression condition = Assert.IsType<BinaryExpression>(caseExpr.WhenClauses[0].Condition);
        Assert.IsType<LiteralExpression>(condition.Right);
        LiteralExpression result = Assert.IsType<LiteralExpression>(caseExpr.WhenClauses[0].Result);
        Assert.Equal("high", result.Value);
    }

    [Fact]
    public void CollectParameterNames_FindsAllParameters()
    {
        SelectStatement statement = Parse(
            "SELECT $a FROM t WHERE x > $b AND y IN ($c, $d) ORDER BY $e");

        HashSet<string> names = ParameterBinder.CollectParameterNames(statement);

        Assert.Equal(5, names.Count);
        Assert.Contains("a", names);
        Assert.Contains("b", names);
        Assert.Contains("c", names);
        Assert.Contains("d", names);
        Assert.Contains("e", names);
    }

    [Fact]
    public void CollectParameterNames_EmptyForNoParameters()
    {
        SelectStatement statement = Parse("SELECT * FROM t WHERE x > 5");

        HashSet<string> names = ParameterBinder.CollectParameterNames(statement);

        Assert.Empty(names);
    }

    // ───────────────────── Error recovery ─────────────────────

    [Fact]
    public void ErrorRecovery_ParameterInWhereClause()
    {
        ParseResult result = SqlParser.TryParseRecovering(
            "SELECT * FROM t WHERE x > $threshold");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Statement);

        BinaryExpression where = Assert.IsType<BinaryExpression>(result.Statement.Where);
        Assert.IsType<ParameterExpression>(where.Right);
    }
}
