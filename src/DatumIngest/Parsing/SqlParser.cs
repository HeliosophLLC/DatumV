using DatumIngest.Parsing.Ast;
using DatumIngest.Parsing.Tokens;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using SP = Superpower.Parse;

#pragma warning disable CS8604, CS8620 // Superpower combinators lack consistent nullable reference type annotations

namespace DatumIngest.Parsing;

/// <summary>
/// Parses tokenized SQL into an AST rooted at <see cref="SelectStatement"/>.
/// Uses Superpower's <see cref="TokenListParser{TKind,T}"/> combinators to implement
/// a recursive-descent parser with proper operator precedence.
/// </summary>
public static class SqlParser
{
    // ───────────────────── Helpers ─────────────────────

    /// <summary>Extracts the text content from a token span.</summary>
    private static string GetTokenText(Token<SqlToken> token)
    {
        return token.ToStringValue();
    }

    /// <summary>
    /// Strips surrounding single quotes and un-escapes doubled quotes
    /// from a string literal token.
    /// </summary>
    private static string UnquoteString(Token<SqlToken> token)
    {
        string raw = token.ToStringValue();
        // Remove surrounding quotes
        string inner = raw[1..^1];
        // Un-escape '' -> '
        return inner.Replace("''", "'");
    }

    // ───────────────────── Atomic expressions ─────────────────────

    /// <summary>Column reference: optional table qualifier + column name.</summary>
    private static readonly TokenListParser<SqlToken, Expression> QualifiedColumn =
        from first in Token.EqualTo(SqlToken.Identifier)
        from rest in (
            from dot in Token.EqualTo(SqlToken.Dot)
            from second in Token.EqualTo(SqlToken.Identifier)
                .Or(Token.EqualTo(SqlToken.Star))
            select second
        ).OptionalOrDefault()
        select rest.HasValue
            ? (rest.Kind == SqlToken.Star
                ? (Expression)new ColumnReference(GetTokenText(first), "*")
                : new ColumnReference(GetTokenText(first), GetTokenText(rest)))
            : new ColumnReference(GetTokenText(first));

    /// <summary>Number literal parsed as a double.</summary>
    private static readonly TokenListParser<SqlToken, Expression> NumberLiteral =
        Token.EqualTo(SqlToken.NumberLiteral)
            .Apply(Numerics.DecimalDouble)
            .Select(value => (Expression)new LiteralExpression(value));

    /// <summary>String literal with quote unescaping.</summary>
    private static readonly TokenListParser<SqlToken, Expression> StringLiteral =
        Token.EqualTo(SqlToken.StringLiteral)
            .Select(token => (Expression)new LiteralExpression(UnquoteString(token)));

    /// <summary>NULL literal.</summary>
    private static readonly TokenListParser<SqlToken, Expression> NullLiteral =
        Token.EqualTo(SqlToken.Null)
            .Select(_ => (Expression)new LiteralExpression(null));

    /// <summary>TRUE literal.</summary>
    private static readonly TokenListParser<SqlToken, Expression> TrueLiteral =
        Token.EqualTo(SqlToken.True)
            .Select(_ => (Expression)new LiteralExpression(true));

    /// <summary>FALSE literal.</summary>
    private static readonly TokenListParser<SqlToken, Expression> FalseLiteral =
        Token.EqualTo(SqlToken.False)
            .Select(_ => (Expression)new LiteralExpression(false));

    /// <summary>
    /// Function call: identifier ( arg1, arg2, ... )
    /// Must be tried before bare column reference because both start with Identifier.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> FunctionCall =
        from name in Token.EqualTo(SqlToken.Identifier)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from args in SP.Ref(() => ExpressionParser!)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Expression)new FunctionCallExpression(GetTokenText(name), args);

    /// <summary>CAST( expression AS type )</summary>
    private static readonly TokenListParser<SqlToken, Expression> CastCall =
        from cast in Token.EqualTo(SqlToken.Cast)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from expression in SP.Ref(() => ExpressionParser!)
        from asKw in Token.EqualTo(SqlToken.As)
        from targetType in Token.EqualTo(SqlToken.Identifier)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Expression)new CastExpression(expression, GetTokenText(targetType));

    /// <summary>Parenthesized expression or subquery.</summary>
    private static readonly TokenListParser<SqlToken, Expression> ParenExpression =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from inner in SP.Ref(() => SubqueryExpression!)
            .Try()
            .Or(SP.Ref(() => ExpressionParser!))
        from close in Token.EqualTo(SqlToken.RightParen)
        select inner;

    /// <summary>A subquery expression: SELECT ... inside parentheses.</summary>
    private static readonly TokenListParser<SqlToken, Expression> SubqueryExpression =
        SP.Ref(() => SelectStatementParser!)
            .Select(query => (Expression)new SubqueryExpression(query));

    /// <summary>
    /// NOT prefix: lower precedence than comparison so that
    /// <c>NOT x = 1</c> parses as <c>NOT (x = 1)</c>.
    /// Self-recursive to support <c>NOT NOT expr</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> NotExpression =
        (from notKw in Token.EqualTo(SqlToken.Not)
         from operand in SP.Ref(() => NotExpression!)
         select (Expression)new UnaryExpression(UnaryOperator.Not, operand))
        .Try()
        .Or(SP.Ref(() => Comparison!));

    /// <summary>Unary minus (negation).</summary>
    private static readonly TokenListParser<SqlToken, Expression> NegationExpression =
        from minus in Token.EqualTo(SqlToken.Minus)
        from operand in SP.Ref(() => PrimaryExpression!)
        select (Expression)new UnaryExpression(UnaryOperator.Negate, operand);

    /// <summary>
    /// Primary expression: the atomic unit in the precedence hierarchy.
    /// Order matters: function call must be tried before column reference
    /// because both start with an Identifier token.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> PrimaryExpression =
        CastCall.Try()
            .Or(FunctionCall.Try())
            .Or(QualifiedColumn)
            .Or(NumberLiteral)
            .Or(StringLiteral)
            .Or(NullLiteral)
            .Or(TrueLiteral)
            .Or(FalseLiteral)
            .Or(NegationExpression)
            .Or(ParenExpression);

    // ───────────────────── Operator precedence layers ─────────────────────
    // Precedence (lowest to highest):
    //   OR
    //   AND
    //   NOT (handled as unary above)
    //   Comparison: =, !=, <, >, <=, >=, LIKE, IS [NOT] NULL, IN, BETWEEN
    //   Addition: +, -
    //   Multiplication: *, /
    //   Unary: -, NOT (handled as unary above)
    //   Primary: literals, columns, function calls, parenthesized

    /// <summary>Exponentiation (highest binary precedence).</summary>
    private static readonly TokenListParser<SqlToken, Expression> Power =
        SP.Chain(
            Token.EqualTo(SqlToken.Caret).Select(_ => BinaryOperator.Power),
            PrimaryExpression,
            (op, left, right) => new BinaryExpression(left, op, right));

    /// <summary>Multiplication, division, and modulo.</summary>
    private static readonly TokenListParser<SqlToken, Expression> Multiplicative =
        SP.Chain(
            Token.EqualTo(SqlToken.Star).Select(_ => BinaryOperator.Multiply)
                .Or(Token.EqualTo(SqlToken.Slash).Select(_ => BinaryOperator.Divide))
                .Or(Token.EqualTo(SqlToken.Percent).Select(_ => BinaryOperator.Modulo)),
            Power,
            (op, left, right) => new BinaryExpression(left, op, right));

    /// <summary>Addition and subtraction.</summary>
    private static readonly TokenListParser<SqlToken, Expression> Additive =
        SP.Chain(
            Token.EqualTo(SqlToken.Plus).Select(_ => BinaryOperator.Add)
                .Or(Token.EqualTo(SqlToken.Minus).Select(_ => BinaryOperator.Subtract)),
            Multiplicative,
            (op, left, right) => new BinaryExpression(left, op, right));

    /// <summary>
    /// Postfix predicates: IS [NOT] NULL, [NOT] IN (...), [NOT] BETWEEN ... AND ..., LIKE.
    /// Applied to the result of an Additive expression.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> Comparison =
        from left in Additive
        from postfix in IsNullPostfix.Try()
            .Or(NotInPostfix.Try())
            .Or(InPostfix.Try())
            .Or(NotBetweenPostfix.Try())
            .Or(BetweenPostfix.Try())
            .Or(LikePostfix.Try())
            .Or(ComparisonPostfix)
            .OptionalOrDefault()
        select postfix is not null ? postfix(left) : left;

    /// <summary>IS [NOT] NULL postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> IsNullPostfix =
        from isKw in Token.EqualTo(SqlToken.Is)
        from notKw in Token.EqualTo(SqlToken.Not).OptionalOrDefault()
        from nullKw in Token.EqualTo(SqlToken.Null)
        select (Func<Expression, Expression>)(expr =>
            new IsNullExpression(expr, Negated: notKw.HasValue));

    /// <summary>IN (value, ...) postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> InPostfix =
        from inKw in Token.EqualTo(SqlToken.In)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from values in SP.Ref(() => ExpressionParser!)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Func<Expression, Expression>)(expr =>
            new InExpression(expr, values));

    /// <summary>NOT IN (value, ...) postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> NotInPostfix =
        from notKw in Token.EqualTo(SqlToken.Not)
        from inKw in Token.EqualTo(SqlToken.In)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from values in SP.Ref(() => ExpressionParser!)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Func<Expression, Expression>)(expr =>
            new InExpression(expr, values, Negated: true));

    /// <summary>BETWEEN low AND high postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> BetweenPostfix =
        from betweenKw in Token.EqualTo(SqlToken.Between)
        from low in Additive
        from andKw in Token.EqualTo(SqlToken.And)
        from high in Additive
        select (Func<Expression, Expression>)(expr =>
            new BetweenExpression(expr, low, high));

    /// <summary>NOT BETWEEN low AND high postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> NotBetweenPostfix =
        from notKw in Token.EqualTo(SqlToken.Not)
        from betweenKw in Token.EqualTo(SqlToken.Between)
        from low in Additive
        from andKw in Token.EqualTo(SqlToken.And)
        from high in Additive
        select (Func<Expression, Expression>)(expr =>
            new BetweenExpression(expr, low, high, Negated: true));

    /// <summary>LIKE pattern postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> LikePostfix =
        from likeKw in Token.EqualTo(SqlToken.Like)
        from pattern in SP.Ref(() => ExpressionParser!)
        select (Func<Expression, Expression>)(expr =>
            new BinaryExpression(expr, BinaryOperator.Like, pattern));

    /// <summary>Infix comparison operators: =, !=, &lt;, &gt;, &lt;=, &gt;=.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> ComparisonPostfix =
        from op in Token.EqualTo(SqlToken.Equals).Select(_ => BinaryOperator.Equal)
            .Or(Token.EqualTo(SqlToken.NotEquals).Select(_ => BinaryOperator.NotEqual))
            .Or(Token.EqualTo(SqlToken.LessOrEqual).Select(_ => BinaryOperator.LessThanOrEqual))
            .Or(Token.EqualTo(SqlToken.GreaterOrEqual).Select(_ => BinaryOperator.GreaterThanOrEqual))
            .Or(Token.EqualTo(SqlToken.LessThan).Select(_ => BinaryOperator.LessThan))
            .Or(Token.EqualTo(SqlToken.GreaterThan).Select(_ => BinaryOperator.GreaterThan))
        from right in Additive
        select (Func<Expression, Expression>)(left =>
            new BinaryExpression(left, op, right));

    /// <summary>AND expressions (binds tighter than OR but looser than NOT).</summary>
    private static readonly TokenListParser<SqlToken, Expression> AndExpression =
        SP.Chain(
            Token.EqualTo(SqlToken.And).Select(_ => BinaryOperator.And),
            NotExpression,
            (op, left, right) => new BinaryExpression(left, op, right));

    /// <summary>OR expressions (lowest precedence binary operator).</summary>
    private static readonly TokenListParser<SqlToken, Expression> OrExpression =
        SP.Chain(
            Token.EqualTo(SqlToken.Or).Select(_ => BinaryOperator.Or),
            AndExpression,
            (op, left, right) => new BinaryExpression(left, op, right));

    /// <summary>The top-level expression parser, exposed as the entry point.</summary>
    private static readonly TokenListParser<SqlToken, Expression> ExpressionParser =
        OrExpression;

    // ───────────────────── SELECT columns ─────────────────────

    /// <summary>SELECT * (all columns).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> StarColumn =
        Token.EqualTo(SqlToken.Star)
            .Select(_ => (SelectColumn)new SelectAllColumns());

    /// <summary>SELECT table.* (all columns from a specific table).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> TableStarColumn =
        from table in Token.EqualTo(SqlToken.Identifier)
        from dot in Token.EqualTo(SqlToken.Dot)
        from star in Token.EqualTo(SqlToken.Star)
        select (SelectColumn)new SelectTableColumns(GetTokenText(table));

    /// <summary>A single expression column with optional AS alias.</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> ExpressionColumn =
        from expression in ExpressionParser
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from name in Token.EqualTo(SqlToken.Identifier)
            select GetTokenText(name)
        ).OptionalOrDefault()
        select new SelectColumn(expression, alias);

    /// <summary>A single column in the SELECT list.</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> ColumnItem =
        TableStarColumn.Try()
            .Or(StarColumn.Try())
            .Or(ExpressionColumn);

    /// <summary>Comma-delimited list of SELECT columns.</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn[]> ColumnList =
        ColumnItem.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma));

    // ───────────────────── FROM clause ─────────────────────

    /// <summary>A table reference with optional alias.</summary>
    private static readonly TokenListParser<SqlToken, TableSource> TableReferenceParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in Token.EqualTo(SqlToken.Identifier)
            select GetTokenText(aliasName)
        ).OptionalOrDefault()
        select (TableSource)new TableReference(GetTokenText(name), alias);

    /// <summary>A subquery source: (SELECT ...) AS alias.</summary>
    private static readonly TokenListParser<SqlToken, TableSource> SubquerySourceParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from query in SP.Ref(() => SelectStatementParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        from asKw in Token.EqualTo(SqlToken.As)
        from alias in Token.EqualTo(SqlToken.Identifier)
        select (TableSource)new SubquerySource(query, GetTokenText(alias));

    /// <summary>
    /// A table-valued function source: identifier(args) [AS alias].
    /// Must be tried before table reference because both start with Identifier.
    /// </summary>
    private static readonly TokenListParser<SqlToken, TableSource> FunctionSourceParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from args in SP.Ref(() => ExpressionParser!)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in Token.EqualTo(SqlToken.Identifier)
            select GetTokenText(aliasName)
        ).OptionalOrDefault()
        select (TableSource)new FunctionSource(GetTokenText(name), args, alias);

    /// <summary>A table source: subquery, function call, or table reference.</summary>
    private static readonly TokenListParser<SqlToken, TableSource> TableSourceParser =
        SubquerySourceParser.Try()
            .Or(FunctionSourceParser.Try())
            .Or(TableReferenceParser);

    /// <summary>FROM table_source</summary>
    private static readonly TokenListParser<SqlToken, FromClause> FromClauseParser =
        from fromKw in Token.EqualTo(SqlToken.From)
        from source in TableSourceParser
        select new FromClause(source);

    // ───────────────────── JOIN clauses ─────────────────────

    /// <summary>Join type keyword combinations.</summary>
    private static readonly TokenListParser<SqlToken, JoinType> JoinTypeParser =
        // INNER JOIN or plain JOIN
        Token.EqualTo(SqlToken.Inner).IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => JoinType.Inner).Try()
        // LEFT [OUTER] JOIN
        .Or(Token.EqualTo(SqlToken.Left)
            .IgnoreThen(Token.EqualTo(SqlToken.Outer).OptionalOrDefault())
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => JoinType.Left).Try())
        // RIGHT [OUTER] JOIN
        .Or(Token.EqualTo(SqlToken.Right)
            .IgnoreThen(Token.EqualTo(SqlToken.Outer).OptionalOrDefault())
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => JoinType.Right).Try())
        // FULL [OUTER] JOIN
        .Or(Token.EqualTo(SqlToken.Full)
            .IgnoreThen(Token.EqualTo(SqlToken.Outer).OptionalOrDefault())
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => JoinType.FullOuter).Try())
        // CROSS JOIN
        .Or(Token.EqualTo(SqlToken.Cross)
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => JoinType.Cross).Try())
        // Plain JOIN (defaults to INNER)
        .Or(Token.EqualTo(SqlToken.Join)
            .Select(_ => JoinType.Inner));

    /// <summary>A single JOIN clause with source and optional ON condition.</summary>
    private static readonly TokenListParser<SqlToken, JoinClause> JoinClauseParser =
        from joinType in JoinTypeParser
        from source in TableSourceParser
        from onCondition in (
            from onKw in Token.EqualTo(SqlToken.On)
            from condition in ExpressionParser
            select condition
        ).OptionalOrDefault()
        select new JoinClause(joinType, source, onCondition);

    /// <summary>Zero or more JOIN clauses.</summary>
    private static readonly TokenListParser<SqlToken, JoinClause[]> JoinClausesParser =
        JoinClauseParser.Many();

    // ───────────────────── WHERE clause ─────────────────────

    /// <summary>WHERE expression</summary>
    private static readonly TokenListParser<SqlToken, Expression> WhereClauseParser =
        from whereKw in Token.EqualTo(SqlToken.Where)
        from condition in ExpressionParser
        select condition;

    // ───────────────────── INTO clause ─────────────────────

    /// <summary>SHARD ON sample_count|byte_size value</summary>
    private static readonly TokenListParser<SqlToken, ShardClause> ShardClauseParser =
        from shardKw in Token.EqualTo(SqlToken.Shard)
        from onKw in Token.EqualTo(SqlToken.On)
        from mode in Token.EqualTo(SqlToken.Identifier)
        from value in Token.EqualTo(SqlToken.NumberLiteral)
            .Apply(Numerics.DecimalDouble)
        select new ShardClause(
            ParseShardMode(GetTokenText(mode)),
            (long)value);

    /// <summary>INTO 'path' [SHARD ON ...]</summary>
    private static readonly TokenListParser<SqlToken, IntoClause> IntoClauseParser =
        from intoKw in Token.EqualTo(SqlToken.Into)
        from path in Token.EqualTo(SqlToken.StringLiteral)
        from shard in ShardClauseParser.OptionalOrDefault()
        select new IntoClause(
            InferOutputFormat(UnquoteString(path)),
            UnquoteString(path),
            shard);

    // ───────────────────── ORDER BY clause ─────────────────────

    /// <summary>A single ORDER BY item: expression [ASC|DESC].</summary>
    private static readonly TokenListParser<SqlToken, OrderByItem> OrderByItemParser =
        from expression in ExpressionParser
        from direction in Token.EqualTo(SqlToken.Asc).Select(_ => SortDirection.Ascending)
            .Or(Token.EqualTo(SqlToken.Desc).Select(_ => SortDirection.Descending))
            .OptionalOrDefault(SortDirection.Ascending)
        select new OrderByItem(expression, direction);

    /// <summary>ORDER BY item1, item2, ...</summary>
    private static readonly TokenListParser<SqlToken, OrderByClause> OrderByClauseParser =
        from orderKw in Token.EqualTo(SqlToken.Order)
        from byKw in Token.EqualTo(SqlToken.By)
        from items in OrderByItemParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select new OrderByClause(items);

    // ───────────────────── LIMIT / OFFSET ─────────────────────

    /// <summary>LIMIT count</summary>
    private static readonly TokenListParser<SqlToken, int?> LimitParser =
        from limitKw in Token.EqualTo(SqlToken.Limit)
        from value in Token.EqualTo(SqlToken.NumberLiteral)
            .Apply(Numerics.DecimalDouble)
        select (int?)value;

    /// <summary>OFFSET count</summary>
    private static readonly TokenListParser<SqlToken, int?> OffsetParser =
        from offsetKw in Token.EqualTo(SqlToken.Offset)
        from value in Token.EqualTo(SqlToken.NumberLiteral)
            .Apply(Numerics.DecimalDouble)
        select (int?)value;

    // ───────────────────── SELECT statement ─────────────────────

    /// <summary>The complete SELECT statement parser.</summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> SelectStatementParser =
        from selectKw in Token.EqualTo(SqlToken.Select)
        from columns in ColumnList
        from fromClause in FromClauseParser
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from intoClause in IntoClauseParser.OptionalOrDefault()
        from orderByClause in OrderByClauseParser.OptionalOrDefault()
        from limitValue in LimitParser.OptionalOrDefault()
        from offsetValue in OffsetParser.OptionalOrDefault()
        select new SelectStatement(
            columns,
            fromClause,
            intoClause,
            joinClauses.Length > 0 ? joinClauses : null,
            whereClause,
            orderByClause,
            limitValue,
            offsetValue);

    /// <summary>The full statement parser that expects to consume all input.</summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> FullParser =
        SelectStatementParser.AtEnd();

    // ───────────────────── Public API ─────────────────────

    /// <summary>
    /// Parses a SQL string into a <see cref="SelectStatement"/> AST.
    /// </summary>
    /// <param name="sql">The SQL query text.</param>
    /// <returns>The parsed AST.</returns>
    /// <exception cref="ParseException">Thrown when the input cannot be parsed.</exception>
    public static SelectStatement Parse(string sql)
    {
        TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);
        TokenListParserResult<SqlToken, SelectStatement> result = FullParser.TryParse(tokens);

        if (!result.HasValue)
        {
            throw new ParseException(
                result.ToString(),
                result.ErrorPosition);
        }

        return result.Value;
    }

    // ───────────────────── Helpers ─────────────────────

    /// <summary>
    /// Determines the output format from the file extension in the INTO path.
    /// </summary>
    private static OutputFormat InferOutputFormat(string path)
    {
        if (path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
            return OutputFormat.Parquet;
        }

        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return OutputFormat.Csv;
        }

        // .h5, .hdf5, or anything else defaults to HDF5
        return OutputFormat.Hdf5;
    }

    /// <summary>
    /// Parses the shard mode identifier (sample_count or byte_size).
    /// </summary>
    private static ShardMode ParseShardMode(string mode)
    {
        if (string.Equals(mode, "sample_count", StringComparison.OrdinalIgnoreCase))
        {
            return ShardMode.SampleCount;
        }

        if (string.Equals(mode, "byte_size", StringComparison.OrdinalIgnoreCase))
        {
            return ShardMode.ByteSize;
        }

        throw new ParseException(
            $"Unknown shard mode '{mode}'. Expected 'sample_count' or 'byte_size'.",
            Position.Empty);
    }
}
