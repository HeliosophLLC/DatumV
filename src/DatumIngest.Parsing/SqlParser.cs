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

    /// <summary>
    /// Extracts the text content from a token span, stripping surrounding
    /// delimiters from bracket-quoted, double-quoted, and single-quoted tokens.
    /// </summary>
    private static string GetTokenText(Token<SqlToken> token)
    {
        string text = token.ToStringValue();

        if (token.Kind == SqlToken.Identifier && text.Length >= 2)
        {
            if (text[0] == '[' && text[^1] == ']')
                return text[1..^1];
            if (text[0] == '"' && text[^1] == '"')
                return text[1..^1].Replace("\"\"", "\"");
        }

        if (token.Kind == SqlToken.StringLiteral && text.Length >= 2
            && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1].Replace("''", "'");
        }

        return text;
    }

    /// <summary>Creates a <see cref="SourceSpan"/> from a single token.</summary>
    private static SourceSpan ToSpan(Token<SqlToken> token)
    {
        return new SourceSpan(token.Span.Position.Line, token.Span.Position.Column, token.Span.Length);
    }

    /// <summary>
    /// Creates a <see cref="SourceSpan"/> that covers from the start of
    /// <paramref name="first"/> to the end of <paramref name="last"/>.
    /// </summary>
    private static SourceSpan ToSpan(Token<SqlToken> first, Token<SqlToken> last)
    {
        int length = (last.Span.Position.Absolute + last.Span.Length) - first.Span.Position.Absolute;
        return new SourceSpan(first.Span.Position.Line, first.Span.Position.Column, length);
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
                ? (Expression)new ColumnReference(GetTokenText(first), "*", ToSpan(first, rest))
                : new ColumnReference(GetTokenText(first), GetTokenText(rest), ToSpan(first, rest)))
            : new ColumnReference(null, GetTokenText(first), ToSpan(first));

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

    /// <summary>Named parameter reference: <c>$threshold</c>.</summary>
    private static readonly TokenListParser<SqlToken, Expression> ParameterReference =
        Token.EqualTo(SqlToken.Parameter)
            .Select(token => (Expression)new ParameterExpression(
                token.ToStringValue()[1..],
                ToSpan(token)));

    // ───────────────────── Window specification parsers ─────────────────────

    /// <summary>
    /// A single window frame bound: UNBOUNDED PRECEDING, N PRECEDING,
    /// CURRENT ROW, N FOLLOWING, or UNBOUNDED FOLLOWING.
    /// CURRENT ROW is parsed as the CURRENT token followed by an identifier
    /// whose text is "ROW" (ROW is not a reserved keyword).
    /// </summary>
    private static readonly TokenListParser<SqlToken, FrameBound> FrameBoundParser =
        // UNBOUNDED PRECEDING
        (from unbounded in Token.EqualTo(SqlToken.Unbounded)
         from preceding in Token.EqualTo(SqlToken.Preceding)
         select (FrameBound)new UnboundedPrecedingBound())
        .Try()
        // UNBOUNDED FOLLOWING
        .Or(from unbounded in Token.EqualTo(SqlToken.Unbounded)
            from following in Token.EqualTo(SqlToken.Following)
            select (FrameBound)new UnboundedFollowingBound())
        .Try()
        // CURRENT ROW
        .Or(from current in Token.EqualTo(SqlToken.Current)
            from row in Token.EqualTo(SqlToken.Identifier)
                .Where(token => string.Equals(token.ToStringValue(), "ROW", StringComparison.OrdinalIgnoreCase))
            select (FrameBound)new CurrentRowBound())
        .Try()
        // N PRECEDING
        .Or(from offset in Token.EqualTo(SqlToken.NumberLiteral)
                .Apply(Numerics.DecimalDouble)
            from preceding in Token.EqualTo(SqlToken.Preceding)
            select (FrameBound)new PrecedingBound((int)offset))
        .Try()
        // N FOLLOWING
        .Or(from offset in Token.EqualTo(SqlToken.NumberLiteral)
                .Apply(Numerics.DecimalDouble)
            from following in Token.EqualTo(SqlToken.Following)
            select (FrameBound)new FollowingBound((int)offset));

    /// <summary>
    /// Window frame: ROWS BETWEEN start AND end.
    /// Only the ROWS frame type is supported; RANGE is reserved for future use.
    /// </summary>
    private static readonly TokenListParser<SqlToken, WindowFrame> WindowFrameParser =
        from rows in Token.EqualTo(SqlToken.Rows)
        from between in Token.EqualTo(SqlToken.Between)
        from start in FrameBoundParser
        from and in Token.EqualTo(SqlToken.And)
        from end in FrameBoundParser
        select new WindowFrame(WindowFrameType.Rows, start, end);

    /// <summary>
    /// PARTITION BY expression list within a window specification.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression[]> WindowPartitionByParser =
        from partitionKw in Token.EqualTo(SqlToken.Partition)
        from byKw in Token.EqualTo(SqlToken.By)
        from expressions in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select expressions;

    /// <summary>
    /// ORDER BY item list within a window specification.
    /// Reuses <see cref="OrderByItem"/> from the main ORDER BY parser.
    /// </summary>
    private static readonly TokenListParser<SqlToken, OrderByItem[]> WindowOrderByParser =
        from orderKw in Token.EqualTo(SqlToken.Order)
        from byKw in Token.EqualTo(SqlToken.By)
        from items in SP.Ref(() => WindowOrderByItemParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select items;

    /// <summary>
    /// A single ORDER BY item within a window specification.
    /// Defined separately to avoid circular reference with the main ExpressionParser.
    /// </summary>
    private static readonly TokenListParser<SqlToken, OrderByItem> WindowOrderByItemParser =
        from expression in SP.Ref(() => ExpressionParser!)
        from direction in Token.EqualTo(SqlToken.Asc).Select(_ => SortDirection.Ascending)
            .Or(Token.EqualTo(SqlToken.Desc).Select(_ => SortDirection.Descending))
            .OptionalOrDefault(SortDirection.Ascending)
        select new OrderByItem(expression, direction);

    /// <summary>
    /// OVER ( [PARTITION BY ...] [ORDER BY ...] [frame] ) — the full window specification.
    /// Returns <see langword="null"/> when no OVER keyword is present.
    /// </summary>
    private static readonly TokenListParser<SqlToken, WindowSpecification?> WindowSpecificationParser =
        (from over in Token.EqualTo(SqlToken.Over)
         from open in Token.EqualTo(SqlToken.LeftParen)
         from partitionBy in WindowPartitionByParser.OptionalOrDefault()
         from orderBy in WindowOrderByParser.OptionalOrDefault()
         from frame in WindowFrameParser.Try().OptionalOrDefault()
         from close in Token.EqualTo(SqlToken.RightParen)
         select (WindowSpecification?)new WindowSpecification(partitionBy, orderBy, frame))
        .OptionalOrDefault();

    /// <summary>
    /// Function call: identifier ( [DISTINCT] arg1, arg2, ... ) [OVER window_spec]
    /// Must be tried before bare column reference because both start with Identifier.
    /// Supports <c>COUNT(*)</c> by treating a bare <c>*</c> inside the argument list
    /// as a sentinel <see cref="LiteralExpression"/> with value <c>"*"</c>.
    /// When followed by an <c>OVER</c> keyword, produces a <see cref="WindowFunctionCallExpression"/>.
    /// The optional <c>DISTINCT</c> keyword before arguments is used by aggregate
    /// functions such as <c>COUNT(DISTINCT col)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> FunctionCall =
        from name in Token.EqualTo(SqlToken.Identifier)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from args in Token.EqualTo(SqlToken.Star)
                .Select(_ => (Expression)new LiteralExpression("*"))
                .Or(SP.Ref(() => ExpressionParser!))
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        from windowSpec in WindowSpecificationParser.OptionalOrDefault()
        select windowSpec is not null
            ? (Expression)new WindowFunctionCallExpression(GetTokenText(name), args, windowSpec, Distinct: distinct.HasValue, Span: ToSpan(name))
            : (Expression)new FunctionCallExpression(GetTokenText(name), args, Distinct: distinct.HasValue, Span: ToSpan(name));

    /// <summary>CAST( expression AS type )</summary>
    private static readonly TokenListParser<SqlToken, Expression> CastCall =
        from cast in Token.EqualTo(SqlToken.Cast)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from expression in SP.Ref(() => ExpressionParser!)
        from asKw in Token.EqualTo(SqlToken.As)
        from targetType in Token.EqualTo(SqlToken.Identifier)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Expression)new CastExpression(expression, GetTokenText(targetType), ToSpan(cast, close));

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
    /// CASE expression supporting both simple and searched forms.
    /// Simple: <c>CASE expr WHEN val THEN result ... [ELSE default] END</c>.
    /// Searched: <c>CASE WHEN cond THEN result ... [ELSE default] END</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> CaseCall =
        from caseKw in Token.EqualTo(SqlToken.Case)
        from operand in SP.Ref(() => ExpressionParser!).OptionalOrDefault()
        from whenClauses in (
            from whenKw in Token.EqualTo(SqlToken.When)
            from condition in SP.Ref(() => ExpressionParser!)
            from thenKw in Token.EqualTo(SqlToken.Then)
            from result in SP.Ref(() => ExpressionParser!)
            select new WhenClause(condition, result)
        ).AtLeastOnce()
        from elseResult in (
            from elseKw in Token.EqualTo(SqlToken.Else)
            from result in SP.Ref(() => ExpressionParser!)
            select result
        ).OptionalOrDefault()
        from endKw in Token.EqualTo(SqlToken.End)
        select (Expression)new CaseExpression(operand, whenClauses, elseResult, ToSpan(caseKw, endKw));

    /// <summary>[NOT] EXISTS (SELECT ...) expression.</summary>
    private static readonly TokenListParser<SqlToken, Expression> ExistsCall =
        from notKw in Token.EqualTo(SqlToken.Not).OptionalOrDefault()
        from existsKw in Token.EqualTo(SqlToken.Exists)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from query in SP.Ref(() => SelectStatementParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Expression)new ExistsExpression(query, Negated: notKw.HasValue);

    /// <summary>
    /// Primary expression: the atomic unit in the precedence hierarchy.
    /// Order matters: function call must be tried before column reference
    /// because both start with an Identifier token.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> PrimaryExpression =
        ExistsCall.Try()
            .Or(CaseCall.Try())
            .Or(CastCall.Try())
            .Or(FunctionCall.Try())
            .Or(QualifiedColumn)
            .Or(NumberLiteral)
            .Or(StringLiteral)
            .Or(NullLiteral)
            .Or(TrueLiteral)
            .Or(FalseLiteral)
            .Or(ParameterReference)
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
            .Or(NotInSubqueryPostfix.Try())
            .Or(NotInPostfix.Try())
            .Or(InSubqueryPostfix.Try())
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

    /// <summary>IN (SELECT ...) subquery postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> InSubqueryPostfix =
        from inKw in Token.EqualTo(SqlToken.In)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from query in SP.Ref(() => SelectStatementParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Func<Expression, Expression>)(expr =>
            new InSubqueryExpression(expr, query));

    /// <summary>NOT IN (SELECT ...) subquery postfix.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> NotInSubqueryPostfix =
        from notKw in Token.EqualTo(SqlToken.Not)
        from inKw in Token.EqualTo(SqlToken.In)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from query in SP.Ref(() => SelectStatementParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Func<Expression, Expression>)(expr =>
            new InSubqueryExpression(expr, query, Negated: true));

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
        select (SelectColumn)new SelectTableColumns(GetTokenText(table), ToSpan(table, star));

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
            .Or(Token.EqualTo(SqlToken.StringLiteral))
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in Token.EqualTo(SqlToken.Identifier)
            select GetTokenText(aliasName)
        ).OptionalOrDefault()
        select (TableSource)new TableReference(GetTokenText(name), alias, ToSpan(name));

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
        select (TableSource)new FunctionSource(GetTokenText(name), args, alias, ToSpan(name));

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

    // ───────────────────── GROUP BY clause ─────────────────────

    /// <summary>GROUP BY expr1, expr2, ...</summary>
    private static readonly TokenListParser<SqlToken, GroupByClause> GroupByClauseParser =
        from groupKw in Token.EqualTo(SqlToken.Group)
        from byKw in Token.EqualTo(SqlToken.By)
        from expressions in ExpressionParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select new GroupByClause(expressions);

    // ───────────────────── HAVING clause ─────────────────────

    /// <summary>HAVING expression</summary>
    private static readonly TokenListParser<SqlToken, Expression> HavingClauseParser =
        from havingKw in Token.EqualTo(SqlToken.Having)
        from condition in ExpressionParser
        select condition;

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
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from columns in ColumnList
        from fromClause in FromClauseParser
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from groupByClause in GroupByClauseParser.OptionalOrDefault()
        from havingClause in HavingClauseParser.OptionalOrDefault()
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
            groupByClause,
            havingClause,
            orderByClause,
            limitValue,
            offsetValue,
            Distinct: distinct.HasValue);

    /// <summary>The full statement parser that expects to consume all input.</summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> FullParser =
        SelectStatementParser.AtEnd();

    /// <summary>
    /// Set of tokens that begin top-level clauses. Used by the error-recovering
    /// parser to find the next safe synchronization point after a clause failure.
    /// </summary>
    private static readonly HashSet<SqlToken> ClauseStartTokens =
    [
        SqlToken.From,
        SqlToken.Join,
        SqlToken.Inner,
        SqlToken.Left,
        SqlToken.Right,
        SqlToken.Full,
        SqlToken.Cross,
        SqlToken.Where,
        SqlToken.Group,
        SqlToken.Having,
        SqlToken.Into,
        SqlToken.Order,
        SqlToken.Limit,
        SqlToken.Offset,
    ];

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

    /// <summary>
    /// Parses SQL text with error recovery, collecting multiple errors and
    /// returning a partial AST where possible. For valid SQL this is equivalent
    /// to <see cref="Parse"/> but returns a <see cref="ParseResult"/> instead
    /// of throwing.
    /// </summary>
    /// <param name="sql">The SQL query text.</param>
    /// <returns>
    /// A <see cref="ParseResult"/> containing the (possibly partial) AST and
    /// any errors encountered. Check <see cref="ParseResult.IsSuccess"/> to
    /// determine whether the parse succeeded without errors.
    /// </returns>
    public static ParseResult TryParseRecovering(string sql)
    {
        TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);

        // Fast path: try the full parser first. If it succeeds, no recovery needed.
        TokenListParserResult<SqlToken, SelectStatement> fullResult = FullParser.TryParse(tokens);
        if (fullResult.HasValue)
        {
            return new ParseResult(fullResult.Value);
        }

        // Recovery path: parse clause-by-clause, collecting errors.
        return ParseWithRecovery(tokens);
    }

    /// <summary>
    /// Parses SQL clause-by-clause with error recovery. When a clause combinator
    /// fails, records the error, skips tokens to the next clause keyword, and
    /// continues parsing subsequent clauses.
    /// </summary>
    private static ParseResult ParseWithRecovery(TokenList<SqlToken> tokens)
    {
        List<ParseError> errors = new();
        Token<SqlToken>[] tokenArray = tokens.ToArray();
        int position = 0;

        // ── SELECT columns ──
        SelectColumn[]? columns = null;
        if (position < tokenArray.Length)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Token<SqlToken>> selectResult =
                Token.EqualTo(SqlToken.Select).TryParse(remaining);

            if (!selectResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Expected SELECT keyword.");
                position = SkipToNextClauseIndex(tokenArray, position);
            }
            else
            {
                position += CountConsumed(tokenArray, position, selectResult.Remainder);
                TokenList<SqlToken> afterSelect = new(tokenArray[position..]);
                TokenListParserResult<SqlToken, SelectColumn[]> columnsResult =
                    ColumnList.TryParse(afterSelect);

                if (!columnsResult.HasValue)
                {
                    AddErrorFromToken(errors, tokenArray, position, "Invalid column list after SELECT.");
                    position = SkipToNextClauseIndex(tokenArray, position);
                }
                else
                {
                    columns = columnsResult.Value;
                    position += CountConsumed(tokenArray, position, columnsResult.Remainder);
                }
            }
        }
        else
        {
            AddErrorFromToken(errors, tokenArray, position, "Expected SELECT keyword.");
        }

        // ── FROM clause ──
        FromClause? fromClause = null;
        if (position < tokenArray.Length)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, FromClause> fromResult =
                FromClauseParser.TryParse(remaining);

            if (!fromResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Expected FROM clause.");
                position = SkipToNextClauseIndex(tokenArray, position);
            }
            else
            {
                fromClause = fromResult.Value;
                position += CountConsumed(tokenArray, position, fromResult.Remainder);
            }
        }
        else
        {
            AddErrorFromToken(errors, tokenArray, position, "Expected FROM clause.");
        }

        // ── JOIN clauses ──
        List<JoinClause> joinClauses = new();
        while (position < tokenArray.Length && IsJoinStartToken(tokenArray[position].Kind))
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, JoinClause> joinResult =
                JoinClauseParser.TryParse(remaining);

            if (!joinResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid JOIN clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                joinClauses.Add(joinResult.Value);
                position += CountConsumed(tokenArray, position, joinResult.Remainder);
            }
        }

        // ── WHERE clause ──
        Expression? whereClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Where)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression> whereResult =
                WhereClauseParser.TryParse(remaining);

            if (!whereResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid WHERE clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                whereClause = whereResult.Value;
                position += CountConsumed(tokenArray, position, whereResult.Remainder);
            }
        }

        // ── GROUP BY clause ──
        GroupByClause? groupByClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Group)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, GroupByClause> groupByResult =
                GroupByClauseParser.TryParse(remaining);

            if (!groupByResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid GROUP BY clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                groupByClause = groupByResult.Value;
                position += CountConsumed(tokenArray, position, groupByResult.Remainder);
            }
        }

        // ── HAVING clause ──
        Expression? havingClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Having)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression> havingResult =
                HavingClauseParser.TryParse(remaining);

            if (!havingResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid HAVING clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                havingClause = havingResult.Value;
                position += CountConsumed(tokenArray, position, havingResult.Remainder);
            }
        }

        // ── INTO clause ──
        IntoClause? intoClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Into)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, IntoClause> intoResult =
                IntoClauseParser.TryParse(remaining);

            if (!intoResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid INTO clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                intoClause = intoResult.Value;
                position += CountConsumed(tokenArray, position, intoResult.Remainder);
            }
        }

        // ── ORDER BY clause ──
        OrderByClause? orderByClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Order)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, OrderByClause> orderByResult =
                OrderByClauseParser.TryParse(remaining);

            if (!orderByResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid ORDER BY clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                orderByClause = orderByResult.Value;
                position += CountConsumed(tokenArray, position, orderByResult.Remainder);
            }
        }

        // ── LIMIT ──
        int? limitValue = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Limit)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, int?> limitResult =
                LimitParser.TryParse(remaining);

            if (!limitResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid LIMIT value.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                limitValue = limitResult.Value;
                position += CountConsumed(tokenArray, position, limitResult.Remainder);
            }
        }

        // ── OFFSET ──
        int? offsetValue = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Offset)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, int?> offsetResult =
                OffsetParser.TryParse(remaining);

            if (!offsetResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid OFFSET value.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                offsetValue = offsetResult.Value;
                position += CountConsumed(tokenArray, position, offsetResult.Remainder);
            }
        }

        // ── Trailing tokens ──
        if (position < tokenArray.Length)
        {
            AddErrorFromToken(errors, tokenArray, position, "Unexpected tokens after end of statement.");
        }

        // Build partial AST if we have enough structure.
        SelectStatement? statement = null;
        if (columns is not null && fromClause is not null)
        {
            statement = new SelectStatement(
                columns,
                fromClause,
                intoClause,
                joinClauses.Count > 0 ? joinClauses.ToArray() : null,
                whereClause,
                groupByClause,
                havingClause,
                orderByClause,
                limitValue,
                offsetValue);
        }
        else if (columns is not null)
        {
            // Partial: we have columns but no FROM — build with a placeholder
            // source so downstream analysis can at least see the column expressions.
            statement = new SelectStatement(
                columns,
                new FromClause(new TableReference("?", Span: new SourceSpan(1, 1, 0))),
                intoClause,
                joinClauses.Count > 0 ? joinClauses.ToArray() : null,
                whereClause,
                groupByClause,
                havingClause,
                orderByClause,
                limitValue,
                offsetValue);
        }

        return new ParseResult(statement, errors);
    }

    /// <summary>
    /// Checks whether the given token kind starts a JOIN clause.
    /// </summary>
    private static bool IsJoinStartToken(SqlToken kind)
    {
        return kind is SqlToken.Join or SqlToken.Inner or
            SqlToken.Left or SqlToken.Right or SqlToken.Full or SqlToken.Cross;
    }

    /// <summary>
    /// Skips forward in the token array from the given index until a
    /// clause-starting keyword is found, returning that index. Returns
    /// past-the-end if no clause keyword is found.
    /// </summary>
    private static int SkipToNextClauseIndex(Token<SqlToken>[] tokenArray, int startIndex)
    {
        for (int i = startIndex; i < tokenArray.Length; i++)
        {
            if (ClauseStartTokens.Contains(tokenArray[i].Kind))
            {
                return i;
            }
        }

        return tokenArray.Length;
    }

    /// <summary>
    /// Counts how many tokens were consumed by comparing the starting position
    /// to the remainder returned by a <c>TryParse</c> call.
    /// </summary>
    private static int CountConsumed(Token<SqlToken>[] tokenArray, int startPosition, TokenList<SqlToken> remainder)
    {
        if (remainder.IsAtEnd)
        {
            return tokenArray.Length - startPosition;
        }

        Token<SqlToken> nextToken = remainder.ConsumeToken().Value;
        for (int i = startPosition; i < tokenArray.Length; i++)
        {
            if (tokenArray[i].Span.Position.Absolute == nextToken.Span.Position.Absolute)
            {
                return i - startPosition;
            }
        }

        return tokenArray.Length - startPosition;
    }

    /// <summary>
    /// Records a parse error at the given token index, or a default position
    /// if the index is past the end of the array.
    /// </summary>
    private static void AddErrorFromToken(List<ParseError> errors, Token<SqlToken>[] tokenArray, int index, string message)
    {
        if (index < tokenArray.Length)
        {
            Token<SqlToken> token = tokenArray[index];
            errors.Add(new ParseError
            {
                Message = message,
                Line = token.Span.Position.Line,
                Column = token.Span.Position.Column,
                Length = token.Span.Length,
            });
        }
        else
        {
            errors.Add(new ParseError
            {
                Message = message,
                Line = 1,
                Column = 1,
                Length = 1,
            });
        }
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
