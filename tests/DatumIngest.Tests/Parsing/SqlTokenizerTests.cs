using Heliosoph.DatumV.Parsing.Tokens;
using Superpower;
using Superpower.Model;

namespace Heliosoph.DatumV.Tests.Parsing;

public class SqlTokenizerTests : ServiceTestBase
{
    private static Token<SqlToken>[] Tokenize(string input)
    {
        Tokenizer<SqlToken> tokenizer = SqlTokenizer.Instance;
        TokenList<SqlToken> result = tokenizer.Tokenize(input);
        return result.ToArray();
    }

    private static void AssertSingleToken(string input, SqlToken expectedKind)
    {
        Token<SqlToken>[] tokens = Tokenize(input);
        Assert.Single(tokens);
        Assert.Equal(expectedKind, tokens[0].Kind);
    }

    // ───────────────────── Keywords (case-insensitive) ─────────────────────

    [Theory]
    [InlineData("SELECT", SqlToken.Select)]
    [InlineData("select", SqlToken.Select)]
    [InlineData("Select", SqlToken.Select)]
    [InlineData("INTO", SqlToken.Into)]
    [InlineData("FROM", SqlToken.From)]
    [InlineData("JOIN", SqlToken.Join)]
    [InlineData("LEFT", SqlToken.Left)]
    [InlineData("RIGHT", SqlToken.Right)]
    [InlineData("FULL", SqlToken.Full)]
    [InlineData("OUTER", SqlToken.Outer)]
    [InlineData("CROSS", SqlToken.Cross)]
    [InlineData("INNER", SqlToken.Inner)]
    [InlineData("ON", SqlToken.On)]
    [InlineData("WHERE", SqlToken.Where)]
    [InlineData("AND", SqlToken.And)]
    [InlineData("OR", SqlToken.Or)]
    [InlineData("NOT", SqlToken.Not)]
    [InlineData("IN", SqlToken.In)]
    [InlineData("BETWEEN", SqlToken.Between)]
    [InlineData("LIKE", SqlToken.Like)]
    [InlineData("ILIKE", SqlToken.ILike)]
    [InlineData("ilike", SqlToken.ILike)]
    [InlineData("REGEXP", SqlToken.Regexp)]
    [InlineData("regexp", SqlToken.Regexp)]
    [InlineData("ESCAPE", SqlToken.Escape)]
    [InlineData("escape", SqlToken.Escape)]
    [InlineData("IS", SqlToken.Is)]
    [InlineData("NULL", SqlToken.Null)]
    [InlineData("AS", SqlToken.As)]
    [InlineData("SHARD", SqlToken.Shard)]
    [InlineData("ORDER", SqlToken.Order)]
    [InlineData("BY", SqlToken.By)]
    [InlineData("ASC", SqlToken.Asc)]
    [InlineData("DESC", SqlToken.Desc)]
    [InlineData("LIMIT", SqlToken.Limit)]
    [InlineData("OFFSET", SqlToken.Offset)]
    [InlineData("CAST", SqlToken.Cast)]
    [InlineData("TRUE", SqlToken.True)]
    [InlineData("FALSE", SqlToken.False)]
    [InlineData("OVER", SqlToken.Over)]
    [InlineData("over", SqlToken.Over)]
    [InlineData("PARTITION", SqlToken.Partition)]
    [InlineData("ROWS", SqlToken.Rows)]
    [InlineData("UNBOUNDED", SqlToken.Unbounded)]
    [InlineData("PRECEDING", SqlToken.Preceding)]
    [InlineData("FOLLOWING", SqlToken.Following)]
    [InlineData("CURRENT", SqlToken.Current)]
    [InlineData("BEGIN", SqlToken.Begin)]
    [InlineData("begin", SqlToken.Begin)]
    [InlineData("WHILE", SqlToken.While)]
    [InlineData("while", SqlToken.While)]
    [InlineData("DECLARE", SqlToken.Declare)]
    [InlineData("declare", SqlToken.Declare)]
    [InlineData("TO", SqlToken.To)]
    [InlineData("to", SqlToken.To)]
    public void KeywordsAreRecognized(string input, SqlToken expected)
    {
        AssertSingleToken(input, expected);
    }

    // ───────────────────── Procedural variable references ─────────────────────

    [Fact]
    public void BareVariableNameTokenizesAsIdentifier()
    {
        // Procedural variables are bare PG-style identifiers (no sigil).
        // The tokenizer can't distinguish a variable reference from a column
        // reference — that's the evaluator's job via VariableScope.
        Token<SqlToken>[] tokens = Tokenize("count");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.Identifier, tokens[0].Kind);
        Assert.Equal("count", tokens[0].ToStringValue());
    }

    [Fact]
    public void LeadingAtSigil_IsRejected()
    {
        // Post-PG-alignment, the leading `@` is no longer a valid lexeme:
        // tokenizing `@count` errors out. Variables are bare identifiers.
        Assert.ThrowsAny<Exception>(() => Tokenize("@count"));
    }

    [Fact]
    public void ColonEquals_TokenizesAsAssignmentOperator()
    {
        // `:=` is the PL/pgSQL assignment operator, used in SELECT-list
        // assignments (`SELECT x := expr`). The multi-char matcher pulls
        // it as one token rather than `:` followed by `=`.
        Token<SqlToken>[] tokens = Tokenize(":=");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.ColonEquals, tokens[0].Kind);
    }

    [Fact]
    public void DoubleColon_TokenizesAsPostfixCastOperator()
    {
        // `::` is the PG postfix cast operator (`x::int`). Pulled as one
        // token; must not split into two `:` tokens.
        Token<SqlToken>[] tokens = Tokenize("::");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.DoubleColon, tokens[0].Kind);
    }

    [Fact]
    public void FatArrow_TokenizesAsNamedArgumentOperator()
    {
        // `=>` is the PG 11+ canonical named-argument operator
        // (`fn(arg => value)`). Must tokenize as a single FatArrow
        // and not split into `=` + `>`.
        Token<SqlToken>[] tokens = Tokenize("=>");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.FatArrow, tokens[0].Kind);
    }

    // ───────────────────── Identifiers ─────────────────────

    [Fact]
    public void SimpleIdentifierIsRecognized()
    {
        AssertSingleToken("my_column", SqlToken.Identifier);
    }

    [Fact]
    public void IdentifierWithDigitsIsRecognized()
    {
        AssertSingleToken("col123", SqlToken.Identifier);
    }

    [Fact]
    public void IdentifierStartingWithUnderscoreIsRecognized()
    {
        AssertSingleToken("_private", SqlToken.Identifier);
    }

    // ───────────────────── Literals ─────────────────────

    [Fact]
    public void IntegerLiteralIsRecognized()
    {
        AssertSingleToken("42", SqlToken.NumberLiteral);
    }

    [Fact]
    public void FloatingPointLiteralIsRecognized()
    {
        AssertSingleToken("3.14", SqlToken.NumberLiteral);
    }

    [Fact]
    public void NegativeNumberTokenizesAsMinusThenNumber()
    {
        Token<SqlToken>[] tokens = Tokenize("-5");
        Assert.Equal(2, tokens.Length);
        Assert.Equal(SqlToken.Minus, tokens[0].Kind);
        Assert.Equal(SqlToken.NumberLiteral, tokens[1].Kind);
    }

    [Fact]
    public void StringLiteralWithSingleQuotesIsRecognized()
    {
        Token<SqlToken>[] tokens = Tokenize("'hello world'");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.StringLiteral, tokens[0].Kind);
        Assert.Equal("'hello world'", tokens[0].ToStringValue());
    }

    [Fact]
    public void EmptyStringLiteralIsRecognized()
    {
        Token<SqlToken>[] tokens = Tokenize("''");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.StringLiteral, tokens[0].Kind);
    }

    // ───────────────────── Symbols ─────────────────────

    [Theory]
    [InlineData("*", SqlToken.Star)]
    [InlineData(",", SqlToken.Comma)]
    [InlineData(".", SqlToken.Dot)]
    [InlineData("(", SqlToken.LeftParen)]
    [InlineData(")", SqlToken.RightParen)]
    [InlineData("=", SqlToken.Equals)]
    [InlineData("<", SqlToken.LessThan)]
    [InlineData(">", SqlToken.GreaterThan)]
    [InlineData("<=", SqlToken.LessOrEqual)]
    [InlineData(">=", SqlToken.GreaterOrEqual)]
    [InlineData("!=", SqlToken.NotEquals)]
    [InlineData("<>", SqlToken.NotEquals)]
    [InlineData("|", SqlToken.Pipe)]
    [InlineData("+", SqlToken.Plus)]
    [InlineData("-", SqlToken.Minus)]
    [InlineData("/", SqlToken.Slash)]
    public void SymbolsAreRecognized(string input, SqlToken expected)
    {
        AssertSingleToken(input, expected);
    }

    // ───────────────────── Compound expressions ─────────────────────

    [Fact]
    public void SimpleSelectIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT a, b FROM t");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.Identifier, SqlToken.Comma,
             SqlToken.Identifier, SqlToken.From, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void QualifiedColumnIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("t.column_name");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Identifier, SqlToken.Dot, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void FunctionCallIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("normalize(x, 0, 255)");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Identifier, SqlToken.LeftParen, SqlToken.Identifier,
             SqlToken.Comma, SqlToken.NumberLiteral, SqlToken.Comma,
             SqlToken.NumberLiteral, SqlToken.RightParen],
            kinds);
    }

    [Fact]
    public void WhereClauseIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("WHERE x > 5 AND y IS NOT NULL");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Where, SqlToken.Identifier, SqlToken.GreaterThan,
             SqlToken.NumberLiteral, SqlToken.And, SqlToken.Identifier,
             SqlToken.Is, SqlToken.Not, SqlToken.Null],
            kinds);
    }

    [Fact]
    public void IntoClauseIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("INTO 'output.parquet' SHARD ON sample_count 1000");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Into, SqlToken.StringLiteral, SqlToken.Shard,
             SqlToken.On, SqlToken.Identifier, SqlToken.NumberLiteral],
            kinds);
    }

    [Fact]
    public void WhitespaceIsIgnored()
    {
        Token<SqlToken>[] tokens = Tokenize("  SELECT   a   ");
        Assert.Equal(2, tokens.Length);
        Assert.Equal(SqlToken.Select, tokens[0].Kind);
        Assert.Equal(SqlToken.Identifier, tokens[1].Kind);
    }

    [Fact]
    public void SelectStarIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT *");
        Assert.Equal(2, tokens.Length);
        Assert.Equal(SqlToken.Select, tokens[0].Kind);
        Assert.Equal(SqlToken.Star, tokens[1].Kind);
    }

    [Fact]
    public void JoinClauseIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("LEFT JOIN t2 ON t1.id = t2.id");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Left, SqlToken.Join, SqlToken.Identifier,
             SqlToken.On, SqlToken.Identifier, SqlToken.Dot,
             SqlToken.Identifier, SqlToken.Equals, SqlToken.Identifier,
             SqlToken.Dot, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void StringLiteralWithEscapedQuoteIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("'it''s'");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.StringLiteral, tokens[0].Kind);
    }

    [Fact]
    public void OrderByLimitIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("ORDER BY x DESC LIMIT 10 OFFSET 5");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Order, SqlToken.By, SqlToken.Identifier,
             SqlToken.Desc, SqlToken.Limit, SqlToken.NumberLiteral,
             SqlToken.Offset, SqlToken.NumberLiteral],
            kinds);
    }

    [Fact]
    public void CastExpressionIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("CAST(x AS Scalar)");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Cast, SqlToken.LeftParen, SqlToken.Identifier,
             SqlToken.As, SqlToken.Identifier, SqlToken.RightParen],
            kinds);
    }

    [Fact]
    public void BetweenExpressionIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("x BETWEEN 1 AND 10");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Identifier, SqlToken.Between, SqlToken.NumberLiteral,
             SqlToken.And, SqlToken.NumberLiteral],
            kinds);
    }

    [Fact]
    public void InExpressionIsTokenized()
    {
        Token<SqlToken>[] tokens = Tokenize("x IN (1, 2, 3)");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Identifier, SqlToken.In, SqlToken.LeftParen,
             SqlToken.NumberLiteral, SqlToken.Comma, SqlToken.NumberLiteral,
             SqlToken.Comma, SqlToken.NumberLiteral, SqlToken.RightParen],
            kinds);
    }

    // ───────────────────── Quoted identifiers ─────────────────────

    [Fact]
    public void DoubleQuotedIdentifierIsRecognized()
    {
        AssertSingleToken("\"adult.data\"", SqlToken.Identifier);
    }

    [Fact]
    public void DoubleQuotedIdentifierWithEscapedQuoteIsRecognized()
    {
        Token<SqlToken>[] tokens = Tokenize("\"col\"\"name\"");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.Identifier, tokens[0].Kind);
    }

    // ───────────────────── Comments ─────────────────────

    [Fact]
    public void LineCommentIsIgnored()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT a -- this is a comment");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void LineCommentOnItsOwnLineIsIgnored()
    {
        Token<SqlToken>[] tokens = Tokenize("-- comment\nSELECT a");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void LineCommentAtEndOfInputIsIgnored()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT a\n-- trailing comment");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void BlockCommentIsIgnored()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT /* skip this */ a");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void MultiLineBlockCommentIsIgnored()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT /*\n  multi\n  line\n*/ a");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void InputWithOnlyACommentProducesNoTokens()
    {
        Token<SqlToken>[] tokens = Tokenize("-- just a comment");
        Assert.Empty(tokens);
    }

    [Fact]
    public void BlockCommentOnlyProducesNoTokens()
    {
        Token<SqlToken>[] tokens = Tokenize("/* block only */");
        Assert.Empty(tokens);
    }

    [Fact]
    public void CommentBetweenTokensIsIgnored()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT a, /* comment */ b FROM t");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.Identifier, SqlToken.Comma,
             SqlToken.Identifier, SqlToken.From, SqlToken.Identifier],
            kinds);
    }

    // ───────────────────── Template strings (backticks) ─────────────────────

    [Fact]
    public void EmptyTemplateStringIsRecognized()
    {
        Token<SqlToken>[] tokens = Tokenize("``");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
        Assert.Equal("``", tokens[0].ToStringValue());
    }

    [Fact]
    public void TemplateStringWithoutSplicesIsRecognized()
    {
        Token<SqlToken>[] tokens = Tokenize("`hello world`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
        Assert.Equal("`hello world`", tokens[0].ToStringValue());
    }

    [Fact]
    public void TemplateStringWithSingleSpliceIsCapturedWhole()
    {
        Token<SqlToken>[] tokens = Tokenize("`hello ${name}`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
        Assert.Equal("`hello ${name}`", tokens[0].ToStringValue());
    }

    [Fact]
    public void TemplateStringWithMultipleSplicesIsCapturedWhole()
    {
        Token<SqlToken>[] tokens = Tokenize("`Tone: ${tone}, Threat: ${threat}`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
    }

    [Fact]
    public void TemplateStringWithMultilineBodyIsCapturedWhole()
    {
        Token<SqlToken>[] tokens = Tokenize("`line1\n  line2 ${x}\n  line3`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
    }

    [Fact]
    public void TemplateStringWithEscapedBacktickIsCapturedWhole()
    {
        Token<SqlToken>[] tokens = Tokenize(@"`a \` b`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
    }

    [Fact]
    public void TemplateStringWithEscapedDollarSuppressesSplice()
    {
        // \${name} should be a literal "${name}" — no splice tokenization.
        Token<SqlToken>[] tokens = Tokenize(@"`literal \${name} text`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
    }

    [Fact]
    public void TemplateStringWithNestedBracesInsideSpliceWorks()
    {
        // Splice contains a struct literal — the close brace of the struct
        // must not be mistaken for the close of the splice.
        Token<SqlToken>[] tokens = Tokenize("`x = ${ {a: 1, b: 2}.a }`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
    }

    [Fact]
    public void TemplateStringWithSingleQuotedStringInsideSpliceWorks()
    {
        // A literal '}' inside a single-quoted string must not close the splice.
        Token<SqlToken>[] tokens = Tokenize("`x = ${ concat('}', a) }`");
        Assert.Single(tokens);
        Assert.Equal(SqlToken.TemplateString, tokens[0].Kind);
    }

    [Fact]
    public void TemplateStringInExpressionContextTokenizesCleanly()
    {
        Token<SqlToken>[] tokens = Tokenize("SELECT `Hello ${name}` FROM t");
        SqlToken[] kinds = tokens.Select(token => token.Kind).ToArray();

        Assert.Equal(
            [SqlToken.Select, SqlToken.TemplateString, SqlToken.From, SqlToken.Identifier],
            kinds);
    }

    [Fact]
    public void UnterminatedTemplateStringFailsTokenization()
    {
        // No closing backtick — Superpower throws on the unrecognized input.
        Assert.ThrowsAny<Exception>(() => Tokenize("`unterminated"));
    }
}
