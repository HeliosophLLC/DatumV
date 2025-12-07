using System.Linq;
using DatumIngest.Parsing.Ast;
using DatumIngest.Parsing.Tokens;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using SP = Superpower.Parse;

#pragma warning disable CS8604, CS8620 // Superpower combinators lack consistent nullable reference type annotations

namespace DatumIngest.Parsing;

/// <summary>
/// Parses tokenized SQL into an AST rooted at <see cref="QueryExpression"/>.
/// Uses Superpower's <see cref="TokenListParser{TKind,T}"/> combinators to implement
/// a recursive-descent parser with proper operator precedence.
/// </summary>
public static class SqlParser
{
    // ───────────────────── Helpers ─────────────────────

    /// <summary>
    /// Extracts the text content from a token span, stripping surrounding
    /// delimiters from double-quoted and single-quoted tokens.
    /// </summary>
    private static string GetTokenText(Token<SqlToken> token)
    {
        string text = token.ToStringValue();

        if (token.Kind == SqlToken.Identifier && text.Length >= 2
            && text[0] == '"' && text[^1] == '"')
        {
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

    /// <summary>
    /// Attaches trailing ORDER BY, LIMIT, OFFSET, and INTO clauses to a compound
    /// query expression. For a single SELECT these clauses are already parsed on
    /// the <see cref="SelectStatement"/> itself and no lifting is needed.
    /// </summary>
    private static QueryExpression ApplyTrailingClauses(
        QueryExpression query,
        OrderByClause? orderBy,
        int? limit,
        int? offset,
        IntoClause? into)
    {
        bool hasTrailing = orderBy is not null || limit is not null || offset is not null || into is not null;
        if (!hasTrailing)
        {
            return query;
        }

        if (query is CompoundQueryExpression compound)
        {
            return compound with { OrderBy = orderBy, Limit = limit, Offset = offset, Into = into };
        }

        if (query is SelectQueryExpression selectQuery)
        {
            return new SelectQueryExpression(
                selectQuery.Statement with
                {
                    OrderBy = orderBy ?? selectQuery.Statement.OrderBy,
                    Limit = limit ?? selectQuery.Statement.Limit,
                    Offset = offset ?? selectQuery.Statement.Offset,
                    Into = into ?? selectQuery.Statement.Into,
                });
        }

        return query;
    }

    /// <summary>
    /// Matches an <see cref="SqlToken.Identifier"/> or a <see cref="SqlToken.TypeKeyword"/>
    /// in positions where an identifier-like name is expected (column aliases, table aliases,
    /// column names in DDL, etc.). Type keywords are reserved in expression position but can
    /// still be used as names in non-expression contexts.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Token<SqlToken>> IdentifierLike =
        Token.EqualTo(SqlToken.Identifier).Or(Token.EqualTo(SqlToken.TypeKeyword));

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

    /// <summary>
    /// Number literal parsed as the narrowest type that fits the value.
    /// Whole numbers: sbyte (-128..127) → short → int → long based on magnitude.
    /// Decimals/scientific notation: float if no precision loss, otherwise double.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> NumberLiteral =
        Token.EqualTo(SqlToken.NumberLiteral)
            .Apply(Numerics.DecimalDouble)
            .Select(value => (Expression)new LiteralExpression(NarrowNumericLiteral(value)));

    private static object NarrowNumericLiteral(double d)
    {
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (d != System.Math.Truncate(d))
        {
            float f = (float)d;
            // Without the explicit (object) cast on the float branch, the C# ternary
            // unifies the operand types to the common arithmetic type (double) and
            // implicitly widens the float back, so the narrowing was getting silently
            // discarded — every decimal literal landed as a double regardless of
            // representability. Boxing the float-branch result preserves the runtime
            // type as System.Single.
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            return (double)f == d ? (object)f : d;
        }

        if (d >= sbyte.MinValue && d <= sbyte.MaxValue) return (sbyte)d;
        if (d >= short.MinValue && d <= short.MaxValue) return (short)d;
        if (d >= int.MinValue && d <= int.MaxValue) return (int)d;
        if (d >= long.MinValue && d <= long.MaxValue) return (long)d;
        return d;
    }

    /// <summary>String literal with quote unescaping.</summary>
    private static readonly TokenListParser<SqlToken, Expression> StringLiteral =
        Token.EqualTo(SqlToken.StringLiteral)
            .Select(token => (Expression)new LiteralExpression(UnquoteString(token)));

    /// <summary>
    /// Backtick-delimited template string with <c>${expression}</c> splices.
    /// Lowers to a <c>concat(…)</c> call interleaving literal chunks with the
    /// parsed splice expressions. A template with no splices collapses to a
    /// single <see cref="LiteralExpression"/>; one with no literal text and a
    /// single splice still goes through <c>concat(…)</c> so the result is
    /// guaranteed to be a string regardless of the splice's runtime kind.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> TemplateStringLiteral =
        Token.EqualTo(SqlToken.TemplateString)
            .Select(token => LowerTemplateString(token));

    /// <summary>
    /// Walks the captured text of a <see cref="SqlToken.TemplateString"/> token,
    /// splitting on <c>${…}</c> boundaries and producing either a
    /// <see cref="LiteralExpression"/> (no splices) or a <c>concat(…)</c>
    /// <see cref="FunctionCallExpression"/> interleaving literal chunks with
    /// parsed splice expressions.
    /// </summary>
    /// <remarks>
    /// Each splice's contents are tokenized and parsed via the existing
    /// <c>ExpressionParser</c>. A failure to parse the splice surfaces as a
    /// <see cref="ParseException"/> identifying the splice text — the outer
    /// recovering parser converts that into a diagnostic anchored at the
    /// template string's position. Source spans on sub-expressions are
    /// reported relative to the splice text, not the outer SQL; PR1 accepts
    /// this approximation for splice diagnostics and leaves precise span
    /// remapping for a follow-up if it becomes painful in practice.
    /// </remarks>
    private static Expression LowerTemplateString(Token<SqlToken> token)
    {
        string raw = token.ToStringValue();
        // Strip surrounding backticks. The tokenizer guarantees the open/close.
        string body = raw.Length >= 2 ? raw[1..^1] : string.Empty;
        SourceSpan tokenSpan = ToSpan(token);

        List<Expression> parts = new();
        System.Text.StringBuilder literalBuffer = new();
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];

            // Escape: \`  \$  \\  → emit the escaped character; everything
            // else passes through with the leading backslash preserved.
            if (c == '\\' && i + 1 < body.Length)
            {
                char next = body[i + 1];
                if (next is '`' or '$' or '\\')
                {
                    literalBuffer.Append(next);
                    i += 2;
                    continue;
                }
                literalBuffer.Append(c);
                i++;
                continue;
            }

            // Splice start: ${ … }
            if (c == '$' && i + 1 < body.Length && body[i + 1] == '{')
            {
                // Find the matching close brace, tracking nesting and skipping
                // single-quoted string contents (the tokenizer applies the same
                // rules; this is a straight mirror so the boundaries agree).
                int spliceStart = i + 2;
                int j = spliceStart;
                int braceDepth = 1;
                while (j < body.Length && braceDepth > 0)
                {
                    char cj = body[j];
                    if (cj == '\\' && j + 1 < body.Length) { j += 2; continue; }
                    if (cj == '\'')
                    {
                        j++;
                        while (j < body.Length)
                        {
                            if (body[j] == '\'')
                            {
                                if (j + 1 < body.Length && body[j + 1] == '\'') { j += 2; continue; }
                                j++;
                                break;
                            }
                            j++;
                        }
                        continue;
                    }
                    if (cj == '{') braceDepth++;
                    else if (cj == '}') braceDepth--;
                    if (braceDepth == 0) break;
                    j++;
                }

                // Flush any pending literal chunk before the splice.
                if (literalBuffer.Length > 0)
                {
                    parts.Add(new LiteralExpression(literalBuffer.ToString()));
                    literalBuffer.Clear();
                }

                if (braceDepth != 0)
                {
                    // Tokenizer accepted the template (matching backtick) but the
                    // splice braces don't balance — this should be rare since the
                    // tokenizer mirrors the same rules, but guard defensively.
                    throw new ParseException(
                        "Unterminated ${...} splice in template string.",
                        new Position(token.Position.Absolute + 1 + i, token.Position.Line, token.Position.Column + 1 + i));
                }

                string spliceText = body.Substring(spliceStart, j - spliceStart);
                Expression spliceExpression = ParseSpliceExpression(spliceText, token, spliceStart);
                parts.Add(spliceExpression);

                i = j + 1; // skip past the closing }
                continue;
            }

            literalBuffer.Append(c);
            i++;
        }

        // Flush trailing literal chunk.
        if (literalBuffer.Length > 0)
        {
            parts.Add(new LiteralExpression(literalBuffer.ToString()));
        }

        // No splices → single string literal. Cheap path; also keeps the AST
        // shape identical to a single-quoted string when the user just wanted
        // backticks for readability without interpolation.
        if (parts.Count == 0)
        {
            return new LiteralExpression(string.Empty);
        }
        if (parts.Count == 1 && parts[0] is LiteralExpression onlyLiteral)
        {
            return onlyLiteral;
        }

        return new FunctionCallExpression("concat", parts, Span: tokenSpan);
    }

    /// <summary>
    /// Tokenizes and parses the contents of a single <c>${…}</c> splice as a
    /// scalar expression. Throws <see cref="ParseException"/> on parse failure
    /// so the outer recovering parser can surface a diagnostic.
    /// </summary>
    private static Expression ParseSpliceExpression(
        string spliceText,
        Token<SqlToken> outerToken,
        int spliceOffsetInBody)
    {
        if (string.IsNullOrWhiteSpace(spliceText))
        {
            throw new ParseException(
                "Empty ${...} splice in template string.",
                new Position(
                    outerToken.Position.Absolute + 1 + spliceOffsetInBody,
                    outerToken.Position.Line,
                    outerToken.Position.Column + 1 + spliceOffsetInBody));
        }

        TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(spliceText);
        TokenListParserResult<SqlToken, Expression> result =
            ExpressionParser!.AtEnd().TryParse(tokens);

        if (!result.HasValue)
        {
            throw new ParseException(
                $"Failed to parse splice expression '{spliceText.Trim()}': {result}",
                new Position(
                    outerToken.Position.Absolute + 1 + spliceOffsetInBody,
                    outerToken.Position.Line,
                    outerToken.Position.Column + 1 + spliceOffsetInBody));
        }

        return result.Value;
    }

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

    /// <summary>
    /// Type literal: a bare type name (<c>Int32</c>, <c>Float64</c>, <c>String</c>, etc.)
    /// in expression position. Produces a <see cref="TypeLiteralExpression"/> for use with
    /// <c>typeof()</c> comparisons. Also accepts <c>Time</c> which is tokenized as
    /// <see cref="SqlToken.Time"/> because it is a reserved keyword (AT TIME ZONE).
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> TypeLiteral =
        Token.EqualTo(SqlToken.TypeKeyword)
            .Or(Token.EqualTo(SqlToken.Time))
            .Select(token => (Expression)new TypeLiteralExpression(token.ToStringValue(), ToSpan(token)));

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
    /// Function name with optional namespace qualifier: <c>name</c> or <c>namespace.name</c>.
    /// The namespaced form is disambiguated from a <see cref="ColumnReference"/> like
    /// <c>t.col</c> by lookahead to the opening <c>(</c> in <see cref="FunctionCall"/>:
    /// when <c>IDENT '.' IDENT '('</c> matches we commit to the function-call branch;
    /// otherwise the parent parser falls back to <see cref="QualifiedColumn"/>.
    /// </summary>
    /// <remarks>
    /// Returns the raw <see cref="Token{TKind}"/> for the unqualified name (used for
    /// <c>SourceSpan</c>) plus the optional namespace string. Callers fold the namespace
    /// into the function name with a dot — <c>"models.mobilenetv2"</c> — so
    /// <see cref="FunctionCallExpression"/>'s string-keyed lookup picks the right entry
    /// without an AST change.
    /// </remarks>
    /// <summary>
    /// A function-name token (the unqualified part of a <c>namespace.name</c>
    /// pair, or a bare function name). Mirrors <c>IdentifierOrKeywordAsName</c>
    /// but returns the <see cref="Token{TKind}"/> rather than the string so
    /// the call site can compute a <see cref="SourceSpan"/>. UDFs may have
    /// names that collide with SQL keywords (<c>add</c>, <c>update</c>, etc.)
    /// — we accept any keyword that can serve as an unquoted name.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Token<SqlToken>> FunctionNameToken =
        Token.EqualTo(SqlToken.Identifier)
            .Or(Token.EqualTo(SqlToken.TypeKeyword))
            .Or(Token.EqualTo(SqlToken.Add))
            .Or(Token.EqualTo(SqlToken.Set))
            .Or(Token.EqualTo(SqlToken.Default))
            .Or(Token.EqualTo(SqlToken.Column))
            .Or(Token.EqualTo(SqlToken.Table))
            .Or(Token.EqualTo(SqlToken.Values))
            .Or(Token.EqualTo(SqlToken.Key))
            .Or(Token.EqualTo(SqlToken.Primary))
            .Or(Token.EqualTo(SqlToken.If))
            .Or(Token.EqualTo(SqlToken.Analyze));

    private static readonly TokenListParser<SqlToken, (string? Namespace, Superpower.Model.Token<SqlToken> Name)> NamespacedFunctionName =
        (from ns in Token.EqualTo(SqlToken.Identifier)
         from dot in Token.EqualTo(SqlToken.Dot)
         from name in FunctionNameToken
         select ((string?)GetTokenText(ns), name))
        .Try()
        .Or(Token.EqualTo(SqlToken.Identifier).Select(name => ((string?)null, name)));

    /// <summary>
    /// Function call: identifier ( [DISTINCT] arg1, arg2, ... [ORDER BY ...] )
    /// [WITHIN GROUP ( ORDER BY ... )]
    /// [FROM FIRST | FROM LAST] [IGNORE NULLS | RESPECT NULLS] [OVER window_spec]
    /// Must be tried before bare column reference because both start with Identifier.
    /// Supports <c>COUNT(*)</c> by treating a bare <c>*</c> inside the argument list
    /// as a sentinel <see cref="LiteralExpression"/> with value <c>"*"</c>.
    /// When followed by an <c>OVER</c> keyword, produces a <see cref="WindowFunctionCallExpression"/>.
    /// The optional <c>DISTINCT</c> keyword before arguments is used by aggregate
    /// functions such as <c>COUNT(DISTINCT col)</c>.
    /// The optional <c>ORDER BY</c> before the closing paren is used by
    /// <c>STRING_AGG(expr, separator ORDER BY expr [ASC|DESC])</c>.
    /// The optional <c>WITHIN GROUP (ORDER BY ...)</c> clause is used by ordered-set
    /// aggregates such as <c>MODE() WITHIN GROUP (ORDER BY col)</c>. The ORDER BY
    /// expressions are promoted into the argument list and also set as the OrderBy
    /// on the resulting <see cref="FunctionCallExpression"/>.
    /// The optional <c>FROM FIRST</c> / <c>FROM LAST</c> clause controls the search
    /// direction for <c>NTH_VALUE</c>. <c>FIRST</c> and <c>LAST</c> are parsed as
    /// contextual identifiers — they are not reserved keywords.
    /// The optional <c>IGNORE NULLS</c> / <c>RESPECT NULLS</c> clause controls
    /// null handling for value window functions (FIRST_VALUE, LAST_VALUE, NTH_VALUE).
    /// Function names may be namespaced (<c>models.mobilenetv2</c>); the namespace is
    /// folded into the function name with a dot for registry lookup.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> FunctionCall =
        from nameTuple in NamespacedFunctionName
        from open in Token.EqualTo(SqlToken.LeftParen)
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from args in Token.EqualTo(SqlToken.Star)
                .Select(_ => (Expression)new LiteralExpression("*"))
                .Or(SP.Ref(() => ExpressionParser!))
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from orderBy in WindowOrderByParser.OptionalOrDefault()
        from close in Token.EqualTo(SqlToken.RightParen)
        from withinGroup in (
            from _within in Token.EqualTo(SqlToken.Within)
            from _group  in Token.EqualTo(SqlToken.Group)
            from _open   in Token.EqualTo(SqlToken.LeftParen)
            from items   in WindowOrderByParser
            from _close  in Token.EqualTo(SqlToken.RightParen)
            select items
        ).AsNullable().Try().OptionalOrDefault()
        from fromLast in
            (from _from in Token.EqualTo(SqlToken.From)
             from direction in Token.EqualTo(SqlToken.Identifier)
                 .Where(token => string.Equals(token.ToStringValue(), "FIRST", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(token.ToStringValue(), "LAST", StringComparison.OrdinalIgnoreCase))
             select string.Equals(direction.ToStringValue(), "LAST", StringComparison.OrdinalIgnoreCase))
            .Try().OptionalOrDefault()
        from nullHandling in
            (from _ignore in Token.EqualTo(SqlToken.Ignore)
             from _nulls in Token.EqualTo(SqlToken.Nulls)
             select NullHandling.IgnoreNulls)
            .Try()
            .Or(
             from _respect in Token.EqualTo(SqlToken.Respect)
             from _nulls in Token.EqualTo(SqlToken.Nulls)
             select NullHandling.RespectNulls)
            .Try().OptionalOrDefault()
        from windowSpec in WindowSpecificationParser.OptionalOrDefault()
        let name = nameTuple.Name
        let qualifiedName = nameTuple.Namespace is null
            ? GetTokenText(nameTuple.Name)
            : $"{nameTuple.Namespace}.{GetTokenText(nameTuple.Name)}"
        select withinGroup is not null
            ? (Expression)new FunctionCallExpression(
                qualifiedName,
                [.. withinGroup.Select(item => item.Expression), .. args],
                OrderBy: withinGroup,
                Distinct: distinct.HasValue,
                Span: ToSpan(name))
            : windowSpec is not null
                ? (Expression)new WindowFunctionCallExpression(qualifiedName, args, windowSpec, Distinct: distinct.HasValue, NullHandling: nullHandling, FromLast: fromLast, Span: ToSpan(name))
                : (Expression)new FunctionCallExpression(qualifiedName, args, OrderBy: orderBy, Distinct: distinct.HasValue, Span: ToSpan(name));

    /// <summary>CAST( expression AS type )</summary>
    private static readonly TokenListParser<SqlToken, Expression> CastCall =
        from cast in Token.EqualTo(SqlToken.Cast)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from expression in SP.Ref(() => ExpressionParser!)
        from asKw in Token.EqualTo(SqlToken.As)
        from targetType in Token.EqualTo(SqlToken.TypeKeyword).Or(Token.EqualTo(SqlToken.Identifier)).Or(Token.EqualTo(SqlToken.Time))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Expression)new CastExpression(expression, GetTokenText(targetType), ToSpan(cast, close));

    /// <summary>
    /// EXTRACT( field FROM expression ) — desugared to <c>date_part('field', expression)</c>.
    /// The field token may be an identifier or a keyword that overlaps with field names (e.g. <c>TIME</c>).
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> ExtractCall =
        from extract in Token.EqualTo(SqlToken.Extract)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from field in Token.EqualTo(SqlToken.Identifier)
            .Or(Token.EqualTo(SqlToken.Time))
        from fromKw in Token.EqualTo(SqlToken.From)
        from source in SP.Ref(() => ExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Expression)new FunctionCallExpression(
            "date_part",
            [new LiteralExpression(GetTokenText(field).ToLowerInvariant()), source],
            Span: ToSpan(extract, close));

    // ───────────────────── Transaction-stable temporal constants ─────────────────────

    /// <summary>CURRENT_DATE — bare keyword, no parentheses.</summary>
    private static readonly TokenListParser<SqlToken, Expression> CurrentDateExpr =
        Token.EqualTo(SqlToken.CurrentDate)
            .Select(_ => (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentDate));

    /// <summary>CURRENT_TIME or CURRENT_TIME(precision).</summary>
    private static readonly TokenListParser<SqlToken, Expression> CurrentTimeExpr =
        (from kw in Token.EqualTo(SqlToken.CurrentTime)
         from open in Token.EqualTo(SqlToken.LeftParen)
         from precision in Token.EqualTo(SqlToken.NumberLiteral)
         from close in Token.EqualTo(SqlToken.RightParen)
         select (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTime, int.Parse(GetTokenText(precision))))
        .Try()
        .Or(Token.EqualTo(SqlToken.CurrentTime)
            .Select(_ => (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTime)));

    /// <summary>CURRENT_TIMESTAMP or CURRENT_TIMESTAMP(precision).</summary>
    private static readonly TokenListParser<SqlToken, Expression> CurrentTimestampExpr =
        (from kw in Token.EqualTo(SqlToken.CurrentTimestamp)
         from open in Token.EqualTo(SqlToken.LeftParen)
         from precision in Token.EqualTo(SqlToken.NumberLiteral)
         from close in Token.EqualTo(SqlToken.RightParen)
         select (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp, int.Parse(GetTokenText(precision))))
        .Try()
        .Or(Token.EqualTo(SqlToken.CurrentTimestamp)
            .Select(_ => (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp)));

    /// <summary>LOCALTIME or LOCALTIME(precision).</summary>
    private static readonly TokenListParser<SqlToken, Expression> LocalTimeExpr =
        (from kw in Token.EqualTo(SqlToken.LocalTime)
         from open in Token.EqualTo(SqlToken.LeftParen)
         from precision in Token.EqualTo(SqlToken.NumberLiteral)
         from close in Token.EqualTo(SqlToken.RightParen)
         select (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTime, int.Parse(GetTokenText(precision))))
        .Try()
        .Or(Token.EqualTo(SqlToken.LocalTime)
            .Select(_ => (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTime)));

    /// <summary>LOCALTIMESTAMP or LOCALTIMESTAMP(precision).</summary>
    private static readonly TokenListParser<SqlToken, Expression> LocalTimestampExpr =
        (from kw in Token.EqualTo(SqlToken.LocalTimestamp)
         from open in Token.EqualTo(SqlToken.LeftParen)
         from precision in Token.EqualTo(SqlToken.NumberLiteral)
         from close in Token.EqualTo(SqlToken.RightParen)
         select (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp, int.Parse(GetTokenText(precision))))
        .Try()
        .Or(Token.EqualTo(SqlToken.LocalTimestamp)
            .Select(_ => (Expression)new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp)));

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
    /// Array literal: <c>[expr, expr, ...]</c> desugars to <c>array(expr, expr, ...)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> ArrayLiteral =
        from open in Token.EqualTo(SqlToken.LeftBracket)
        from elements in SP.Ref(() => ExpressionParser!)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightBracket)
        select (Expression)new FunctionCallExpression(
            "array",
            elements,
            Span: ToSpan(open, close));

    /// <summary>
    /// A single struct field: <c>name: expr</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, StructField> StructFieldParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        from colon in Token.EqualTo(SqlToken.Colon)
        from value in SP.Ref(() => ExpressionParser!)
        select new StructField(GetTokenText(name), value);

    /// <summary>
    /// Struct literal: <c>{ field1: expr1, field2: expr2, ... }</c>.
    /// An empty struct <c>{}</c> is also valid.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> StructLiteral =
        from open in Token.EqualTo(SqlToken.LeftBrace)
        from fields in StructFieldParser
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightBrace)
        select (Expression)new StructLiteralExpression(fields, Span: ToSpan(open, close));

    /// <summary>
    /// Primary expression: the atomic unit in the precedence hierarchy.
    /// Order matters: function call must be tried before column reference
    /// because both start with an Identifier token.
    /// StructLiteral is tried before ArrayLiteral (both start with a bracket-style delimiter).
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> PrimaryExpression =
        SP.Ref(() => ScanExpressionParser!).Try()
            .Or(ExistsCall.Try())
            .Or(CaseCall.Try())
            .Or(CastCall.Try())
            .Or(ExtractCall.Try())
            .Or(CurrentDateExpr.Try())
            .Or(CurrentTimestampExpr.Try())
            .Or(CurrentTimeExpr.Try())
            .Or(LocalTimestampExpr.Try())
            .Or(LocalTimeExpr.Try())
            .Or(FunctionCall.Try())
            .Or(QualifiedColumn)
            .Or(TypeLiteral)
            .Or(NumberLiteral)
            .Or(StringLiteral)
            .Or(TemplateStringLiteral)
            .Or(NullLiteral)
            .Or(TrueLiteral)
            .Or(FalseLiteral)
            .Or(ParameterReference)
            .Or(NegationExpression)
            .Or(ParenExpression)
            .Or(StructLiteral.Try())
            .Or(ArrayLiteral);

    /// <summary>
    /// Primary expression with optional postfix index-access subscripts: <c>expr[i]</c>.
    /// Zero or more <c>[index]</c> suffixes are applied left-to-right, so
    /// <c>a[0][1]</c> parses as <c>(a[0])[1]</c>.
    /// The disambiguation from array literals is natural: an <c>ArrayLiteral</c> is
    /// consumed entirely by <see cref="PrimaryExpression"/>, and the postfix layer
    /// only applies <em>after</em> a primary has already been matched.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> PostfixPrimary =
        from primary in PrimaryExpression
        from subscripts in (
            from open in Token.EqualTo(SqlToken.LeftBracket)
            from index in SP.Ref(() => ExpressionParser!)
            from close in Token.EqualTo(SqlToken.RightBracket)
            select (open, index)
        ).Try().Many()
        select subscripts.Aggregate(
            primary,
            (expr, sub) => (Expression)new IndexAccessExpression(expr, sub.index, ToSpan(sub.open)));

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
            PostfixPrimary,
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
    /// AT TIME ZONE level — sits between Additive and the comparison predicates so that
    /// <c>ts AT TIME ZONE 'X' = ts AT TIME ZONE 'Y'</c> parses correctly without parens.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> AtTimeZoneLevel =
        from expr in Additive
        from result in (
            from atKw in Token.EqualTo(SqlToken.At)
            from timeKw in Token.EqualTo(SqlToken.Time)
            from zoneKw in Token.EqualTo(SqlToken.Zone)
            from tz in Additive
            select (Expression)new AtTimeZoneExpression(expr, tz, ToSpan(atKw, zoneKw))
        ).Try().OptionalOrDefault()
        select result ?? expr;

    /// <summary>
    /// Postfix predicates: IS [NOT] NULL, [NOT] IN (...), [NOT] BETWEEN ... AND ..., LIKE.
    /// Applied to the result of an AtTimeZoneLevel expression.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> Comparison =
        from left in AtTimeZoneLevel
        from postfix in IsNullPostfix.Try()
            .Or(IsTypePostfix.Try())
            .Or(NotInSubqueryPostfix.Try())
            .Or(NotInPostfix.Try())
            .Or(InSubqueryPostfix.Try())
            .Or(InPostfix.Try())
            .Or(NotBetweenPostfix.Try())
            .Or(BetweenPostfix.Try())
            .Or(LikePostfix.Try())
            .Or(ILikePostfix.Try())
            .Or(RegexpPostfix.Try())
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

    /// <summary>
    /// IS [NOT] TypeKeyword postfix: <c>x IS Int32</c> desugars to
    /// <c>typeof(x) = Int32</c>; <c>x IS NOT Int32</c> desugars to
    /// <c>typeof(x) != Int32</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> IsTypePostfix =
        from isKw in Token.EqualTo(SqlToken.Is)
        from notKw in Token.EqualTo(SqlToken.Not).OptionalOrDefault()
        from typeKw in Token.EqualTo(SqlToken.TypeKeyword)
        select (Func<Expression, Expression>)(expr =>
            new BinaryExpression(
                new FunctionCallExpression("typeof", [expr]),
                notKw.HasValue ? BinaryOperator.NotEqual : BinaryOperator.Equal,
                new TypeLiteralExpression(typeKw.ToStringValue(), ToSpan(typeKw))));

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

    /// <summary>LIKE pattern postfix (case-sensitive), with optional ESCAPE clause.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> LikePostfix =
        from likeKw in Token.EqualTo(SqlToken.Like)
        from pattern in SP.Ref(() => ExpressionParser!)
        from escape in Token.EqualTo(SqlToken.Escape)
            .IgnoreThen(SP.Ref(() => ExpressionParser!))
            .OptionalOrDefault()
        select (Func<Expression, Expression>)(expr =>
            escape is not null
                ? new LikeExpression(expr, pattern, escape, CaseInsensitive: false)
                : new BinaryExpression(expr, BinaryOperator.Like, pattern));

    /// <summary>ILIKE pattern postfix (case-insensitive), with optional ESCAPE clause.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> ILikePostfix =
        from ilikeKw in Token.EqualTo(SqlToken.ILike)
        from pattern in SP.Ref(() => ExpressionParser!)
        from escape in Token.EqualTo(SqlToken.Escape)
            .IgnoreThen(SP.Ref(() => ExpressionParser!))
            .OptionalOrDefault()
        select (Func<Expression, Expression>)(expr =>
            escape is not null
                ? new LikeExpression(expr, pattern, escape, CaseInsensitive: true)
                : new BinaryExpression(expr, BinaryOperator.ILike, pattern));

    /// <summary>REGEXP pattern postfix (regular expression matching).</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> RegexpPostfix =
        from regexpKw in Token.EqualTo(SqlToken.Regexp)
        from pattern in SP.Ref(() => ExpressionParser!)
        select (Func<Expression, Expression>)(expr =>
            new BinaryExpression(expr, BinaryOperator.Regexp, pattern));

    /// <summary>Infix comparison operators: =, !=, &lt;, &gt;, &lt;=, &gt;=.</summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> ComparisonPostfix =
        from op in Token.EqualTo(SqlToken.Equals).Select(_ => BinaryOperator.Equal)
            .Or(Token.EqualTo(SqlToken.NotEquals).Select(_ => BinaryOperator.NotEqual))
            .Or(Token.EqualTo(SqlToken.LessOrEqual).Select(_ => BinaryOperator.LessThanOrEqual))
            .Or(Token.EqualTo(SqlToken.GreaterOrEqual).Select(_ => BinaryOperator.GreaterThanOrEqual))
            .Or(Token.EqualTo(SqlToken.LessThan).Select(_ => BinaryOperator.LessThan))
            .Or(Token.EqualTo(SqlToken.GreaterThan).Select(_ => BinaryOperator.GreaterThan))
        from right in SP.Ref(() => AtTimeZoneLevel!)
        select (Func<Expression, Expression>)(left =>
            new BinaryExpression(left, op, right));

    /// <summary>
    /// Parses a type-narrowing bind: <c>expr AS TypeKeyword name</c>.
    /// Only valid as the left operand of AND. Desugars to an IS type guard
    /// and inline CAST substitution:
    /// <c>x AS Int32 y AND y &gt; 0</c> becomes <c>typeof(x) = Int32 AND CAST(x AS Int32) &gt; 0</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (Expression Source, string TypeName, string BindingName)> TypeNarrowPrefix =
        from source in NotExpression
        from asKw in Token.EqualTo(SqlToken.As)
        from typeKw in Token.EqualTo(SqlToken.TypeKeyword)
        from bindingName in IdentifierLike
        select (source, typeKw.ToStringValue(), GetTokenText(bindingName));

    /// <summary>AND expressions (binds tighter than OR but looser than NOT).</summary>
    /// <remarks>
    /// Supports optional type-narrowing binds on the left side of AND:
    /// <c>x AS Int32 y AND y &gt; 0</c>. The bind is desugared into an IS type guard
    /// plus inline CAST substitution before the AST reaches downstream consumers.
    /// Standard AND chains without narrowing are parsed via the Chain combinator.
    /// </remarks>
    private static readonly TokenListParser<SqlToken, Expression> AndExpression =
        (from narrow in TypeNarrowPrefix
         from andKw in Token.EqualTo(SqlToken.And)
         from rest in SP.Ref(() => AndExpression!)
         select DesugarTypeNarrow(narrow.Source, narrow.TypeName, narrow.BindingName, rest))
        .Try()
        .Or(SP.Chain(
            Token.EqualTo(SqlToken.And).Select(_ => BinaryOperator.And),
            NotExpression,
            (op, left, right) => new BinaryExpression(left, op, right)));

    /// <summary>
    /// Desugars a type-narrowing bind into a <c>can_cast</c> guard ANDed with the body
    /// where the binding name has been replaced with CAST(source AS type).
    /// <c>x AS Int32 y AND y &gt; 0</c> becomes <c>can_cast(x, Int32) AND CAST(x AS Int32) &gt; 0</c>.
    /// </summary>
    private static Expression DesugarTypeNarrow(Expression source, string typeName, string bindingName, Expression body)
    {
        // Left side: can_cast(source, TypeLiteral)
        Expression guard = new FunctionCallExpression("can_cast",
            [source, new TypeLiteralExpression(typeName)]);

        // Right side: body with bindingName → CAST(source AS typeName)
        Expression cast = new CastExpression(source, typeName);
        Expression substituted = SubstituteBinding(body, bindingName, cast);

        return new BinaryExpression(guard, BinaryOperator.And, substituted);
    }

    /// <summary>
    /// Recursively replaces all <see cref="ColumnReference"/> nodes whose unqualified
    /// name matches <paramref name="bindingName"/> with <paramref name="replacement"/>.
    /// Used by type-narrowing desugaring to inline CAST expressions.
    /// </summary>
    private static Expression SubstituteBinding(Expression expression, string bindingName, Expression replacement)
    {
        return expression switch
        {
            ColumnReference col when col.TableName is null
                && string.Equals(col.ColumnName, bindingName, StringComparison.OrdinalIgnoreCase)
                => replacement,

            BinaryExpression bin => new BinaryExpression(
                SubstituteBinding(bin.Left, bindingName, replacement),
                bin.Operator,
                SubstituteBinding(bin.Right, bindingName, replacement)),

            UnaryExpression un => new UnaryExpression(
                un.Operator,
                SubstituteBinding(un.Operand, bindingName, replacement)),

            FunctionCallExpression func => new FunctionCallExpression(
                func.FunctionName,
                func.Arguments.Select(a => SubstituteBinding(a, bindingName, replacement)).ToList(),
                func.OrderBy,
                func.Distinct,
                func.Span),

            CastExpression cast => new CastExpression(
                SubstituteBinding(cast.Expression, bindingName, replacement),
                cast.TargetType,
                cast.Span),

            InExpression inExpr => new InExpression(
                SubstituteBinding(inExpr.Expression, bindingName, replacement),
                inExpr.Values.Select(v => SubstituteBinding(v, bindingName, replacement)).ToList(),
                inExpr.Negated),

            BetweenExpression bet => new BetweenExpression(
                SubstituteBinding(bet.Expression, bindingName, replacement),
                SubstituteBinding(bet.Low, bindingName, replacement),
                SubstituteBinding(bet.High, bindingName, replacement),
                bet.Negated),

            IsNullExpression isNull => new IsNullExpression(
                SubstituteBinding(isNull.Expression, bindingName, replacement),
                isNull.Negated),

            CaseExpression caseExpr => new CaseExpression(
                caseExpr.Operand is not null ? SubstituteBinding(caseExpr.Operand, bindingName, replacement) : null,
                caseExpr.WhenClauses.Select(w => new WhenClause(
                    SubstituteBinding(w.Condition, bindingName, replacement),
                    SubstituteBinding(w.Result, bindingName, replacement))).ToList(),
                caseExpr.ElseResult is not null ? SubstituteBinding(caseExpr.ElseResult, bindingName, replacement) : null,
                caseExpr.Span),

            LikeExpression like => new LikeExpression(
                SubstituteBinding(like.Expression, bindingName, replacement),
                SubstituteBinding(like.Pattern, bindingName, replacement),
                SubstituteBinding(like.EscapeCharacter, bindingName, replacement),
                like.CaseInsensitive),

            IndexAccessExpression idx => new IndexAccessExpression(
                SubstituteBinding(idx.Source, bindingName, replacement),
                SubstituteBinding(idx.Index, bindingName, replacement),
                idx.Span),

            // Leaf nodes that cannot contain the binding name.
            LiteralExpression or TypeLiteralExpression or ParameterExpression => expression,

            // Anything else (subqueries, window functions, etc.) — pass through unchanged.
            // Bindings cannot cross subquery boundaries by design.
            _ => expression,
        };
    }

    /// <summary>OR expressions (lowest precedence binary operator).</summary>
    private static readonly TokenListParser<SqlToken, Expression> OrExpression =
        SP.Chain(
            Token.EqualTo(SqlToken.Or).Select(_ => BinaryOperator.Or),
            AndExpression,
            (op, left, right) => new BinaryExpression(left, op, right));

    // ───────────────────── Lambda expressions ─────────────────────

    /// <summary>
    /// Bare single-parameter lambda: <c>x -&gt; expr</c>.
    /// Must be tried before the normal expression chain because both start with an Identifier token.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> SingleParameterLambda =
        from parameter in Token.EqualTo(SqlToken.Identifier)
        from arrow in Token.EqualTo(SqlToken.Arrow)
        from body in SP.Ref(() => OrExpression!)
        select (Expression)new LambdaExpression(
            [GetTokenText(parameter)],
            body,
            ToSpan(arrow));

    /// <summary>
    /// Parenthesized multi-parameter lambda: <c>(x, y) -&gt; expr</c>.
    /// Also supports single-parameter parenthesized form: <c>(x) -&gt; expr</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> MultiParameterLambda =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from parameters in Token.EqualTo(SqlToken.Identifier)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        from arrow in Token.EqualTo(SqlToken.Arrow)
        from body in SP.Ref(() => OrExpression!)
        select (Expression)new LambdaExpression(
            parameters.Select(GetTokenText).ToArray(),
            body,
            ToSpan(arrow));

    /// <summary>The top-level expression parser, exposed as the entry point.</summary>
    private static readonly TokenListParser<SqlToken, Expression> ExpressionParser =
        SingleParameterLambda.Try()
            .Or(MultiParameterLambda.Try())
            .Or(OrExpression);

    // ───────────────────── SELECT columns ─────────────────────

    /// <summary>
    /// An optional EXCEPT clause that follows <c>*</c> or <c>table.*</c> to exclude
    /// specific columns from the wildcard expansion: <c>* EXCEPT (col1, col2)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<string>> ExceptColumnsClause =
        from exceptKw in Token.EqualTo(SqlToken.Except)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from columns in Token.EqualTo(SqlToken.Identifier)
            .Select(GetTokenText)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (IReadOnlyList<string>)columns;

    /// <summary>
    /// A single replacement item inside a REPLACE clause: <c>expression AS column_name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnReplacement> ReplacementItem =
        from expression in ExpressionParser
        from asKw in Token.EqualTo(SqlToken.As)
        from name in Token.EqualTo(SqlToken.Identifier)
        select new ColumnReplacement(expression, GetTokenText(name));

    /// <summary>
    /// An optional REPLACE clause that follows <c>*</c> or <c>table.*</c> (and optional EXCEPT)
    /// to substitute column values: <c>* REPLACE (expr AS col1, expr AS col2)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<ColumnReplacement>> ReplaceColumnsClause =
        from replaceKw in Token.EqualTo(SqlToken.Replace)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from items in ReplacementItem.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (IReadOnlyList<ColumnReplacement>)items;

    /// <summary>SELECT * or SELECT * EXCEPT (col1, col2) or SELECT * REPLACE (expr AS col) (all columns with optional exclusion/replacement).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> StarColumn =
        from star in Token.EqualTo(SqlToken.Star)
        from excluded in ExceptColumnsClause.Try().AsNullable().OptionalOrDefault()
        from replaced in ReplaceColumnsClause.Try().AsNullable().OptionalOrDefault()
        select (SelectColumn)new SelectAllColumns(excluded, replaced);

    /// <summary>SELECT table.* or SELECT table.* EXCEPT (col1, col2) or SELECT table.* REPLACE (expr AS col) (all columns from a specific table with optional exclusion/replacement).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> TableStarColumn =
        from table in Token.EqualTo(SqlToken.Identifier)
        from dot in Token.EqualTo(SqlToken.Dot)
        from star in Token.EqualTo(SqlToken.Star)
        from excluded in ExceptColumnsClause.Try().AsNullable().OptionalOrDefault()
        from replaced in ReplaceColumnsClause.Try().AsNullable().OptionalOrDefault()
        select (SelectColumn)new SelectTableColumns(GetTokenText(table), ToSpan(table, star), excluded, replaced);

    /// <summary>A single expression column with optional AS alias.</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> ExpressionColumn =
        from expression in ExpressionParser
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from name in IdentifierLike
            select GetTokenText(name)
        ).Try().Or(IdentifierLike.Select(GetTokenText))
        .OptionalOrDefault()
        select new SelectColumn(expression, alias);

    /// <summary>A single column in the SELECT list.</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> ColumnItem =
        TableStarColumn.Try()
            .Or(StarColumn.Try())
            .Or(ExpressionColumn);

    // ───────────────────── SCAN (fold/prefix-scan) expressions ─────────────────────

    /// <summary>
    /// Parses the OVER clause for a SCAN expression. Unlike window functions, ORDER BY is required.
    /// </summary>
    private static readonly TokenListParser<SqlToken, WindowSpecification> ScanOverClauseParser =
        from over in Token.EqualTo(SqlToken.Over)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from partitionBy in WindowPartitionByParser.OptionalOrDefault()
        from orderBy in WindowOrderByParser
        from close in Token.EqualTo(SqlToken.RightParen)
        select new WindowSpecification(partitionBy, orderBy);

    /// <summary>
    /// Parses a parenthesized, comma-separated list of two or more expressions.
    /// Used for tuple-form SCAN body and INIT clauses.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression[]> ParenExpressionListParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from first in SP.Ref(() => ExpressionParser!)
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from expr in SP.Ref(() => ExpressionParser!)
            select expr
        ).AtLeastOnce()
        from close in Token.EqualTo(SqlToken.RightParen)
        select new Expression[] { first }.Concat(rest).ToArray();

    /// <summary>
    /// Parses a parenthesized, comma-separated list of two or more identifiers for
    /// SCAN accumulator names or output aliases. Already consumes the delimiters.
    /// </summary>
    private static TokenListParser<SqlToken, string[]> ScanNameListParser() =>
        from open in Token.EqualTo(SqlToken.LeftParen)
        from firstName in IdentifierLike
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from ident in IdentifierLike
            select ident
        ).AtLeastOnce()
        from close in Token.EqualTo(SqlToken.RightParen)
        select new[] { GetTokenText(firstName) }.Concat(rest.Select(GetTokenText)).ToArray();

    /// <summary>
    /// Tuple-form SCAN expression:
    /// <c>SCAN (a, b) = (e1, e2) INIT (v1, v2) OVER (...) AS (a1, a2)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> TupleScanExpressionParser =
        from scanKw in Token.EqualTo(SqlToken.Scan)
        from names in ScanNameListParser()
        from eq in Token.EqualTo(SqlToken.Equals)
        from bodies in ParenExpressionListParser
        from initKw in Token.EqualTo(SqlToken.Init)
        from inits in ParenExpressionListParser
        from window in ScanOverClauseParser
        from asKw in Token.EqualTo(SqlToken.As)
        from aliases in ScanNameListParser()
        select (Expression)new ScanExpression(names, bodies, inits, window, aliases, ToSpan(scanKw));

    /// <summary>
    /// Scalar SCAN expression:
    /// <c>SCAN acc = expr INIT seed OVER (...) AS alias</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> ScalarScanExpressionParser =
        from scanKw in Token.EqualTo(SqlToken.Scan)
        from name in Token.EqualTo(SqlToken.Identifier)
        from eq in Token.EqualTo(SqlToken.Equals)
        from body in SP.Ref(() => ExpressionParser!)
        from initKw in Token.EqualTo(SqlToken.Init)
        from init in SP.Ref(() => ExpressionParser!)
        from window in ScanOverClauseParser
        from asKw in Token.EqualTo(SqlToken.As)
        from alias in IdentifierLike
        select (Expression)new ScanExpression(
            [GetTokenText(name)], [body], [init], window, [GetTokenText(alias)], ToSpan(scanKw));

    /// <summary>
    /// SCAN expression in either tuple or scalar form.
    /// Tuple is tried first with backtracking since both start with the SCAN token.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> ScanExpressionParser =
        TupleScanExpressionParser.Try().Or(ScalarScanExpressionParser);

    // ───────────────────── LET bindings ─────────────────────

    /// <summary>
    /// Parses a comma-separated list of two or more identifiers for a destructure pattern,
    /// already positioned after the opening delimiter.
    /// </summary>
    private static TokenListParser<SqlToken, string[]> DestructureNameListParser(SqlToken closingToken) =>
        from firstName in Token.EqualTo(SqlToken.Identifier)
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from ident in Token.EqualTo(SqlToken.Identifier)
            select ident
        ).AtLeastOnce()
        from close in Token.EqualTo(closingToken)
        select new[] { GetTokenText(firstName) }.Concat(rest.Select(GetTokenText)).ToArray();

    /// <summary>
    /// A positional destructuring LET binding: <c>LET (a, b [, c ...]) = expression</c>.
    /// Extracts values by zero-based index from a Vector, Array, or Struct.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding> PositionalDestructureLetBindingParser =
        from letKw in Token.EqualTo(SqlToken.Let)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from names in DestructureNameListParser(SqlToken.RightParen)
        from eq in Token.EqualTo(SqlToken.Equals)
        from expression in ExpressionParser
        select new LetBinding(
            string.Empty,
            expression,
            OutputAlias: null,
            Span: ToSpan(open),
            Destructure: new DestructurePattern(names, DestructureMode.Positional, ToSpan(open)));

    /// <summary>
    /// A named destructuring LET binding: <c>LET {field1, field2 [, field3 ...]} = expression</c>.
    /// Extracts values by field name from a Struct.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding> NamedDestructureLetBindingParser =
        from letKw in Token.EqualTo(SqlToken.Let)
        from open in Token.EqualTo(SqlToken.LeftBrace)
        from names in DestructureNameListParser(SqlToken.RightBrace)
        from eq in Token.EqualTo(SqlToken.Equals)
        from expression in ExpressionParser
        select new LetBinding(
            string.Empty,
            expression,
            OutputAlias: null,
            Span: ToSpan(open),
            Destructure: new DestructurePattern(names, DestructureMode.Named, ToSpan(open)));

    /// <summary>A single LET binding: <c>LET name = expression [AS alias]</c>.</summary>
    private static readonly TokenListParser<SqlToken, LetBinding> ScalarLetBindingParser =
        from letKw in Token.EqualTo(SqlToken.Let)
        from name in IdentifierLike
        from eq in Token.EqualTo(SqlToken.Equals)
        from expression in ExpressionParser
        from outputAlias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from alias in IdentifierLike
            select GetTokenText(alias)
        ).OptionalOrDefault()
        select new LetBinding(GetTokenText(name), expression, outputAlias, ToSpan(name));

    /// <summary>
    /// A single LET binding in any form: positional destructuring, named destructuring, or scalar.
    /// Positional and named are tried first (with backtracking) so that the scalar parser does not
    /// greedily consume the <c>LET</c> keyword before the pattern delimiter is visible.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding> LetBindingParser =
        PositionalDestructureLetBindingParser.Try()
            .Or(NamedDestructureLetBindingParser.Try())
            .Or(ScalarLetBindingParser);

    /// <summary>
    /// Zero or more comma-separated LET bindings at the start of a SELECT list.
    /// Each binding is followed by a comma that separates it from the next
    /// binding or the first output column.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding[]> LetBindingsParser =
        (from binding in LetBindingParser
         from comma in Token.EqualTo(SqlToken.Comma)
         select binding).Many();

    /// <summary>Comma-delimited list of SELECT columns (at least one required).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn[]> ColumnList =
        from first in ColumnItem
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from item in ColumnItem
            select item
        ).Many()
        select new SelectColumn[] { first }.Concat(rest).ToArray();

    // ───────────────────── FROM clause ─────────────────────

    /// <summary>
    /// Parses BERNOULLI, SYSTEM, STRATIFIED, or BALANCED as a <see cref="TablesampleMethod"/>.
    /// These are parsed as identifiers (not reserved keywords) to avoid breaking user table names.
    /// </summary>
    private static readonly TokenListParser<SqlToken, TablesampleMethod> TablesampleMethodParser =
        Token.EqualTo(SqlToken.Identifier)
            .Where(token =>
            {
                string text = GetTokenText(token);
                return text.Equals("BERNOULLI", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("STRATIFIED", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("BALANCED", StringComparison.OrdinalIgnoreCase);
            }, "BERNOULLI, SYSTEM, STRATIFIED, or BALANCED")
            .Select(token =>
            {
                string text = GetTokenText(token);
                if (text.Equals("BERNOULLI", StringComparison.OrdinalIgnoreCase)) return TablesampleMethod.Bernoulli;
                if (text.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)) return TablesampleMethod.System;
                if (text.Equals("STRATIFIED", StringComparison.OrdinalIgnoreCase)) return TablesampleMethod.Stratified;
                return TablesampleMethod.Balanced;
            });

    /// <summary>
    /// Parses a single unqualified column name as a <see cref="ColumnReference"/>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnReference> UnqualifiedColumnParser =
        Token.EqualTo(SqlToken.Identifier)
            .Select(token => new ColumnReference(null, GetTokenText(token), ToSpan(token)));

    /// <summary>
    /// Parses an <c>ON column</c> or <c>ON (col1, col2, ...)</c> stratification key list.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<ColumnReference>> StratifyColumnsParser =
        from onKeyword in Token.EqualTo(SqlToken.On)
        from columns in (
            // Parenthesized composite key: ON (col1, col2, ...)
            from open in Token.EqualTo(SqlToken.LeftParen)
            from cols in UnqualifiedColumnParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from close in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<ColumnReference>)cols
        ).Try().Or(
            // Single column: ON col
            UnqualifiedColumnParser.Select(col => (IReadOnlyList<ColumnReference>)new[] { col })
        )
        select columns;

    /// <summary>
    /// Parses a TABLESAMPLE clause:
    /// <c>TABLESAMPLE BERNOULLI|SYSTEM(percentage) [REPEATABLE(seed)]</c> or
    /// <c>TABLESAMPLE STRATIFIED|BALANCED(arg) ON column [REPEATABLE(seed)]</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, TablesampleClause> TablesampleClauseParser =
        from tablesampleKeyword in Token.EqualTo(SqlToken.Tablesample)
        from method in TablesampleMethodParser
        from open in Token.EqualTo(SqlToken.LeftParen)
        from argument in SP.Ref(() => ExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        from stratifyColumns in StratifyColumnsParser.AsNullable().OptionalOrDefault()
        from seed in (
            from repeatableKeyword in Token.EqualTo(SqlToken.Repeatable)
            from seedOpen in Token.EqualTo(SqlToken.LeftParen)
            from seedExpression in SP.Ref(() => ExpressionParser!)
            from seedClose in Token.EqualTo(SqlToken.RightParen)
            select seedExpression
        ).AsNullable().OptionalOrDefault()
        select ValidateTablesampleClause(method, argument, seed, stratifyColumns);

    /// <summary>
    /// Validates that the ON clause is present for Stratified/Balanced and absent for Bernoulli/System.
    /// </summary>
    private static TablesampleClause ValidateTablesampleClause(
        TablesampleMethod method, Expression argument, Expression? seed,
        IReadOnlyList<ColumnReference>? stratifyColumns)
    {
        bool requiresOn = method is TablesampleMethod.Stratified or TablesampleMethod.Balanced;

        if (requiresOn && stratifyColumns is null)
        {
            throw new ParseException(
                $"TABLESAMPLE {method.ToString().ToUpperInvariant()} requires an ON clause specifying the stratification column(s).",
                default);
        }

        if (!requiresOn && stratifyColumns is not null)
        {
            throw new ParseException(
                $"TABLESAMPLE {method.ToString().ToUpperInvariant()} does not support an ON clause.",
                default);
        }

        return new TablesampleClause(method, argument, seed, stratifyColumns);
    }

    /// <summary>A table reference with optional schema qualifier, TABLESAMPLE clause, and alias.</summary>
    private static readonly TokenListParser<SqlToken, TableSource> TableReferenceParser =
        from first in Token.EqualTo(SqlToken.Identifier)
            .Or(Token.EqualTo(SqlToken.StringLiteral))
        from schemaQualified in (
            from dot in Token.EqualTo(SqlToken.Dot)
            from second in Token.EqualTo(SqlToken.Identifier)
                .Or(Token.EqualTo(SqlToken.StringLiteral))
            select second
        ).OptionalOrDefault()
        from tablesample in TablesampleClauseParser.AsNullable().OptionalOrDefault()
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().Or(IdentifierLike.Select(GetTokenText))
        .OptionalOrDefault()
        select (TableSource)(schemaQualified.HasValue
            ? new TableReference(GetTokenText(schemaQualified), alias, ToSpan(first, schemaQualified), tablesample, SchemaName: GetTokenText(first))
            : new TableReference(GetTokenText(first), alias, ToSpan(first), tablesample));

    /// <summary>A subquery source: (SELECT ...) [AS] alias.</summary>
    private static readonly TokenListParser<SqlToken, TableSource> SubquerySourceParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from query in SP.Ref(() => SelectStatementParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select aliasName
        ).Try().Or(IdentifierLike)
        select (TableSource)new SubquerySource(query, GetTokenText(alias));

    /// <summary>
    /// A table-valued function source: identifier(args) [AS] alias.
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
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().Or(IdentifierLike.Select(GetTokenText))
        .OptionalOrDefault()
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

    /// <summary>Join type keyword combinations, including LATERAL and T-SQL APPLY variants.</summary>
    private static readonly TokenListParser<SqlToken, (JoinType Type, bool IsLateral)> JoinTypeParser =
        // CROSS APPLY (T-SQL style lateral cross join)
        Token.EqualTo(SqlToken.Cross)
            .IgnoreThen(Token.EqualTo(SqlToken.Apply))
            .Select(_ => (JoinType.Cross, true)).Try()
        // OUTER APPLY (T-SQL style lateral left join)
        .Or(Token.EqualTo(SqlToken.Outer)
            .IgnoreThen(Token.EqualTo(SqlToken.Apply))
            .Select(_ => (JoinType.Left, true)).Try())
        // INNER JOIN
        .Or(Token.EqualTo(SqlToken.Inner).IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => (JoinType.Inner, false)).Try())
        // LEFT [OUTER] JOIN [LATERAL]
        .Or(from _ in Token.EqualTo(SqlToken.Left)
            from __ in Token.EqualTo(SqlToken.Outer).OptionalOrDefault()
            from ___ in Token.EqualTo(SqlToken.Join)
            from isLateral in Token.EqualTo(SqlToken.Lateral).Select(t => true).OptionalOrDefault(false)
            select (JoinType.Left, isLateral))
        // RIGHT [OUTER] JOIN
        .Or(Token.EqualTo(SqlToken.Right)
            .IgnoreThen(Token.EqualTo(SqlToken.Outer).OptionalOrDefault())
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => (JoinType.Right, false)).Try())
        // FULL [OUTER] JOIN
        .Or(Token.EqualTo(SqlToken.Full)
            .IgnoreThen(Token.EqualTo(SqlToken.Outer).OptionalOrDefault())
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => (JoinType.FullOuter, false)).Try())
        // CROSS JOIN [LATERAL] — .Try() allows backtracking when CROSS is followed by
        // VALIDATE (for CROSS VALIDATE) instead of JOIN.
        .Or((from _ in Token.EqualTo(SqlToken.Cross)
            from __ in Token.EqualTo(SqlToken.Join)
            from isLateral in Token.EqualTo(SqlToken.Lateral).Select(t => true).OptionalOrDefault(false)
            select (JoinType.Cross, isLateral)).Try())
        // Plain JOIN (defaults to INNER)
        .Or(Token.EqualTo(SqlToken.Join)
            .Select(_ => (JoinType.Inner, false)));

    /// <summary>A single JOIN clause with source and optional ON condition.</summary>
    private static readonly TokenListParser<SqlToken, JoinClause> JoinClauseParser =
        from joinInfo in JoinTypeParser
        from source in TableSourceParser
        from onCondition in (
            from onKw in Token.EqualTo(SqlToken.On)
            from condition in ExpressionParser
            select condition
        ).OptionalOrDefault()
        select new JoinClause(joinInfo.Type, source, onCondition, joinInfo.IsLateral);

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

    /// <summary>GROUP BY ALL | GROUP BY expr1, expr2, ...</summary>
    private static readonly TokenListParser<SqlToken, GroupByClause> GroupByClauseParser =
        from groupKw in Token.EqualTo(SqlToken.Group)
        from byKw in Token.EqualTo(SqlToken.By)
        from result in Token.EqualTo(SqlToken.All)
            .Select(_ => new GroupByClause(Array.Empty<Expression>(), IsAll: true))
            .Or(ExpressionParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
                .Select(expressions => new GroupByClause(expressions)))
        select result;

    // ───────────────────── HAVING clause ─────────────────────

    /// <summary>HAVING expression</summary>
    private static readonly TokenListParser<SqlToken, Expression> HavingClauseParser =
        from havingKw in Token.EqualTo(SqlToken.Having)
        from condition in ExpressionParser
        select condition;

    // ───────────────────── QUALIFY clause ─────────────────────

    /// <summary>QUALIFY expression (post-window-function filter).</summary>
    private static readonly TokenListParser<SqlToken, Expression> QualifyClauseParser =
        from qualifyKw in Token.EqualTo(SqlToken.Qualify)
        from condition in ExpressionParser
        select condition;

    // ───────────────────── ASSERT clause ─────────────────────

    /// <summary>
    /// Optional <c>ON FAIL SKIP | WARN | ABORT ["message"]</c> suffix for ASSERT.
    /// Parsed contextually using <c>ON</c> + identifier text to avoid reserving
    /// SKIP, WARN, and ABORT as keywords. An optional string literal immediately
    /// after the mode keyword is captured as an inline message.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (AssertFailureMode Mode, Expression? InlineMessage)> AssertFailureModeParser =
        from onKw in Token.EqualTo(SqlToken.On)
        from failKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => string.Equals(t.ToStringValue(), "FAIL", StringComparison.OrdinalIgnoreCase))
        from modeKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => string.Equals(t.ToStringValue(), "SKIP", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(t.ToStringValue(), "WARN", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(t.ToStringValue(), "ABORT", StringComparison.OrdinalIgnoreCase))
        from inlineMessage in StringLiteral.AsNullable().Try().OptionalOrDefault()
        select (
            Mode: string.Equals(modeKw.ToStringValue(), "SKIP", StringComparison.OrdinalIgnoreCase)
                ? AssertFailureMode.Skip
                : string.Equals(modeKw.ToStringValue(), "WARN", StringComparison.OrdinalIgnoreCase)
                    ? AssertFailureMode.Warn
                    : AssertFailureMode.Abort,
            InlineMessage: inlineMessage);

    /// <summary>
    /// A single ASSERT clause: <c>ASSERT predicate [MESSAGE expr] [ON FAIL SKIP | WARN | ABORT ["message"]]</c>.
    /// The <c>MESSAGE</c> keyword form takes precedence over an inline string after the mode keyword
    /// when both are present.
    /// </summary>
    private static readonly TokenListParser<SqlToken, AssertClause> AssertClauseParser =
        from assertKw in Token.EqualTo(SqlToken.Assert)
        from predicate in ExpressionParser
        from message in (
            from msgKw in Token.EqualTo(SqlToken.Message)
            from msgExpr in ExpressionParser
            select msgExpr
        ).AsNullable().Try().OptionalOrDefault()
        from failureModeResult in AssertFailureModeParser.Try().OptionalOrDefault((Mode: AssertFailureMode.Abort, InlineMessage: default(Expression?)))
        select new AssertClause(predicate, message ?? failureModeResult.InlineMessage, failureModeResult.Mode, ToSpan(assertKw));

    /// <summary>Zero or more ASSERT clauses following QUALIFY.</summary>
    private static readonly TokenListParser<SqlToken, AssertClause[]> AssertClausesParser =
        AssertClauseParser.Many();

    // ───────────────────── DEFINE block ─────────────────────

    /// <summary>
    /// A single declaration inside a DEFINE block: either an inline LET binding
    /// or an ASSERT clause. The first element is non-null for LET declarations;
    /// the second element is non-null for ASSERT declarations.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (LetBinding? Let, AssertClause? Assert)>
        DefineDeclarationParser =
        LetBindingParser
            .Select(let => (Let: (LetBinding?)let, Assert: (AssertClause?)null))
            .Or(AssertClauseParser
            .Select(assert => (Let: (LetBinding?)null, Assert: (AssertClause?)assert)));

    /// <summary>
    /// Parses a DEFINE block: <c>DEFINE { declaration [;] ... }</c>.
    /// Each declaration is either a LET binding or an ASSERT clause; semicolons
    /// are optional separators. Returns embedded LET bindings and ASSERT clauses
    /// as separate arrays so the SELECT parsers can merge them with their own bindings.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (LetBinding[] LetBindings, AssertClause[] Assertions)>
        DefineBlockParser =
        from defineKw in Token.EqualTo(SqlToken.Define)
        from openBrace in Token.EqualTo(SqlToken.LeftBrace)
        from declarations in (
            from decl in DefineDeclarationParser
            from semi in Token.EqualTo(SqlToken.Semicolon).OptionalOrDefault()
            select decl
        ).Many()
        from closeBrace in Token.EqualTo(SqlToken.RightBrace)
        select (
            declarations.Where(d => d.Let is not null).Select(d => d.Let!).ToArray(),
            declarations.Where(d => d.Assert is not null).Select(d => d.Assert!).ToArray()
        );

    /// <summary>
    /// Tries to parse a DEFINE block first; if absent, falls back to zero or more inline
    /// LET bindings followed by commas. Returns the same <c>(LetBinding[], AssertClause[])</c>
    /// tuple in both cases, enabling a single binding site in the SELECT parsers.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (LetBinding[] LetBindings, AssertClause[] Assertions)>
        LetOrDefineParser =
        DefineBlockParser.Try()
            .Or(LetBindingsParser
            .Select(bindings => (LetBindings: bindings, Assertions: Array.Empty<AssertClause>())));

    /// <summary>
    /// Combines ASSERT clauses sourced from a DEFINE block with ASSERT clauses
    /// written as trailing clauses after the column list. Returns <see langword="null"/>
    /// when both inputs are empty so the <see cref="SelectStatement"/> field stays null.
    /// </summary>
    private static IReadOnlyList<AssertClause>? MergeAssertions(AssertClause[] fromDefine, AssertClause[] fromClauses)
    {
        if (fromDefine.Length == 0 && fromClauses.Length == 0)
            return null;
        if (fromDefine.Length == 0)
            return fromClauses;
        if (fromClauses.Length == 0)
            return fromDefine;
        return fromDefine.Concat(fromClauses).ToArray();
    }

    // ───────────────────── PIVOT clause ─────────────────────

    /// <summary>
    /// Parses a bare column reference (no table prefix) and returns a
    /// <see cref="ColumnReference"/> rather than the <see cref="Expression"/> base type.
    /// Used by both <see cref="PivotClauseParser"/> and <see cref="UnpivotClauseParser"/>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnReference> BareColumnReferenceParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        select new ColumnReference(null, GetTokenText(name), ToSpan(name));

    /// <summary>
    /// PIVOT ( aggregate [, aggregate ...] FOR pivot_column [IN ( value [, value ...] )] ) [AS alias]
    /// <para>
    /// The value list is optional — when omitted the executor auto-discovers all distinct
    /// values at runtime (subject to the cardinality cap defined on <see cref="PivotClause"/>).
    /// </para>
    /// </summary>
    private static readonly TokenListParser<SqlToken, PivotClause> PivotClauseParser =
        from pivotKw in Token.EqualTo(SqlToken.Pivot)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from aggregates in FunctionCall
                .Where(e => e is FunctionCallExpression, "aggregate function call")
                .Select(e => (FunctionCallExpression)e)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from forKw in Token.EqualTo(SqlToken.For)
        from pivotColumn in BareColumnReferenceParser
        from valueList in (
            from inKw in Token.EqualTo(SqlToken.In)
            from openParen in Token.EqualTo(SqlToken.LeftParen)
            from values in ExpressionParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from closeParen in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<Expression>?)values
        ).Try().OptionalOrDefault()
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().OptionalOrDefault()
        select new PivotClause(aggregates, pivotColumn, valueList, alias);

    // ───────────────────── UNPIVOT clause ─────────────────────

    /// <summary>
    /// UNPIVOT [INCLUDE NULLS] ( value_column FOR name_column IN ( column [, column ...] ) ) [AS alias]
    /// </summary>
    private static readonly TokenListParser<SqlToken, UnpivotClause> UnpivotClauseParser =
        from unpivotKw in Token.EqualTo(SqlToken.Unpivot)
        from includeNulls in (
            from includKw in Token.EqualTo(SqlToken.Include)
            from nullsKw in Token.EqualTo(SqlToken.Nulls)
            select true
        ).Try().OptionalOrDefault()
        from open in Token.EqualTo(SqlToken.LeftParen)
        from valueColumn in Token.EqualTo(SqlToken.Identifier)
        from forKw in Token.EqualTo(SqlToken.For)
        from nameColumn in Token.EqualTo(SqlToken.Identifier)
        from inKw in Token.EqualTo(SqlToken.In)
        from openParen in Token.EqualTo(SqlToken.LeftParen)
        from sourceColumns in BareColumnReferenceParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from closeParen in Token.EqualTo(SqlToken.RightParen)
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().OptionalOrDefault()
        select new UnpivotClause(GetTokenText(valueColumn), GetTokenText(nameColumn), sourceColumns, includeNulls, alias);

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

    // ───────────────────── Common Table Expressions ─────────────────────

    /// <summary>
    /// Optional explicit column name list for a CTE: <c>(col1, col2, ...)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string[]> CommonTableExpressionColumnListParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from names in IdentifierLike
            .Select(GetTokenText)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select names;

    /// <summary>
    /// Materialization hint: <c>MATERIALIZED</c> or <c>NOT MATERIALIZED</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, MaterializationHint> MaterializationHintParser =
        (from notKw in Token.EqualTo(SqlToken.Not)
         from matKw in Token.EqualTo(SqlToken.Materialized)
         select MaterializationHint.NotMaterialized)
        .Try()
        .Or(from matKw in Token.EqualTo(SqlToken.Materialized)
            select MaterializationHint.Materialized);

    /// <summary>
    /// A single CTE definition: <c>name [(cols)] AS [MATERIALIZED | NOT MATERIALIZED] ( query )</c>.
    /// For recursive CTEs, the body contains <c>UNION ALL</c> separating the anchor and
    /// recursive member queries. For non-recursive CTEs, the body is a full query expression
    /// supporting set operations (UNION ALL, INTERSECT, EXCEPT).
    /// The <paramref name="isRecursive"/> flag is threaded from the WITH RECURSIVE prefix.
    /// </summary>
    private static TokenListParser<SqlToken, CommonTableExpression> SingleCommonTableExpressionParser(bool isRecursive) =>
        isRecursive
            ? RecursiveCommonTableExpressionParser
            : NonRecursiveCommonTableExpressionParser;

    /// <summary>
    /// Parses a non-recursive CTE whose body is a full query expression, supporting
    /// UNION, UNION ALL, INTERSECT, EXCEPT, ORDER BY, LIMIT, and OFFSET inside the parentheses.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CommonTableExpression> NonRecursiveCommonTableExpressionParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        from columnNames in CommonTableExpressionColumnListParser.OptionalOrDefault()
        from asKw in Token.EqualTo(SqlToken.As)
        from hint in MaterializationHintParser.OptionalOrDefault()
        from open in Token.EqualTo(SqlToken.LeftParen)
        from body in SP.Ref(() => QueryExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select new CommonTableExpression(
            GetTokenText(name),
            body,
            RecursiveQuery: null,
            columnNames,
            IsRecursive: false,
            hint);

    /// <summary>
    /// Parses a recursive CTE whose body is split into an anchor member and a recursive
    /// member separated by UNION ALL. Both are parsed as individual SELECT statements
    /// for separate planning at execution time.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CommonTableExpression> RecursiveCommonTableExpressionParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        from columnNames in CommonTableExpressionColumnListParser.OptionalOrDefault()
        from asKw in Token.EqualTo(SqlToken.As)
        from hint in MaterializationHintParser.OptionalOrDefault()
        from open in Token.EqualTo(SqlToken.LeftParen)
        from anchorQuery in SP.Ref(() => SelectStatementParser!)
        from recursivePart in (
            from unionKw in Token.EqualTo(SqlToken.Union)
            from allKw in Token.EqualTo(SqlToken.All)
            from recursiveQuery in SP.Ref(() => SelectStatementParser!)
            select (SelectStatement?)recursiveQuery
        ).OptionalOrDefault()
        from close in Token.EqualTo(SqlToken.RightParen)
        select new CommonTableExpression(
            GetTokenText(name),
            new SelectQueryExpression(anchorQuery),
            recursivePart,
            columnNames,
            IsRecursive: true,
            hint);

    /// <summary>
    /// The WITH clause: <c>WITH [RECURSIVE] cte1, cte2, ... SELECT ...</c>.
    /// Parses one or more comma-separated CTE definitions.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CommonTableExpression[]> WithClauseParser =
        from withKw in Token.EqualTo(SqlToken.With)
        from recursive in Token.EqualTo(SqlToken.Recursive).OptionalOrDefault()
        from ctes in SP.Ref(() => SingleCommonTableExpressionParser(recursive.HasValue))
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select ctes;

    // ───────────────────── CROSS VALIDATE ─────────────────────

    /// <summary>
    /// Parses a named argument of the form <c>name = value</c> where name is a contextual
    /// identifier and value is a numeric literal.
    /// </summary>
    private static TokenListParser<SqlToken, (string Name, Expression Value)> NamedArg(string name) =>
        from n in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals(name, StringComparison.OrdinalIgnoreCase), name)
        from eq in Token.EqualTo(SqlToken.Equals)
        from value in SP.Ref(() => ExpressionParser!)
        select (name, value);

    /// <summary>
    /// Parses a CROSS VALIDATE clause:
    /// <c>CROSS VALIDATE(k = N [, seed = S]) ON key [STRATIFY BY col] [GROUP BY col] AS alias</c>.
    /// CROSS is a keyword token; VALIDATE and STRATIFY are contextual identifiers.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CrossValidateClause> CrossValidateClauseParser =
        from cross in Token.EqualTo(SqlToken.Cross)
        from validate in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("VALIDATE", StringComparison.OrdinalIgnoreCase), "VALIDATE")
        from open in Token.EqualTo(SqlToken.LeftParen)
        from k in NamedArg("k")
        from seed in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from s in NamedArg("seed")
            select s.Value
        ).AsNullable().OptionalOrDefault()
        from close in Token.EqualTo(SqlToken.RightParen)
        from onKw in Token.EqualTo(SqlToken.On)
        from keyColumns in (
            from lp in Token.EqualTo(SqlToken.LeftParen)
            from cols in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from rp in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<Expression>)cols
        ).Try().Or(
            SP.Ref(() => ExpressionParser!).Select(e => (IReadOnlyList<Expression>)new[] { e })
        )
        from stratifyColumns in (
            from stratifyKw in Token.EqualTo(SqlToken.Identifier)
                .Where(t => GetTokenText(t).Equals("STRATIFY", StringComparison.OrdinalIgnoreCase), "STRATIFY")
            from byKw in Token.EqualTo(SqlToken.By)
            from cols in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            select (IReadOnlyList<Expression>?)cols
        ).OptionalOrDefault()
        from groupColumns in (
            from groupKw in Token.EqualTo(SqlToken.Group)
            from byKw in Token.EqualTo(SqlToken.By)
            from cols in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            select (IReadOnlyList<Expression>?)cols
        ).OptionalOrDefault()
        from asKw in Token.EqualTo(SqlToken.As)
        from alias in IdentifierLike
        select new CrossValidateClause(k.Value, seed, keyColumns, stratifyColumns, groupColumns, GetTokenText(alias));

    // ───────────────────── SELECT statement ─────────────────────

    /// <summary>The core SELECT statement parser (without WITH preamble).</summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> SelectStatementParser =
        from selectKw in Token.EqualTo(SqlToken.Select)
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from letOrDefine in LetOrDefineParser
        from columns in ColumnList
        from fromClause in FromClauseParser.AsNullable().OptionalOrDefault()
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from crossValidateClause in CrossValidateClauseParser.AsNullable().Try().OptionalOrDefault()
        from groupByClause in GroupByClauseParser.OptionalOrDefault()
        from havingClause in HavingClauseParser.OptionalOrDefault()
        from qualifyClause in QualifyClauseParser.OptionalOrDefault()
        from assertions in AssertClausesParser
        from pivotClause in PivotClauseParser.AsNullable().Try().OptionalOrDefault()
        from unpivotClause in UnpivotClauseParser.AsNullable().Try().OptionalOrDefault()
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
            qualifyClause,
            MergeAssertions(letOrDefine.Assertions, assertions),
            pivotClause,
            unpivotClause,
            orderByClause,
            limitValue,
            offsetValue,
            Distinct: distinct.HasValue,
            LetBindings: letOrDefine.LetBindings.Length > 0 ? letOrDefine.LetBindings : null,
            CrossValidate: crossValidateClause);

    /// <summary>
    /// Bare SELECT parser: same as <see cref="SelectStatementParser"/> but stops
    /// before ORDER BY, LIMIT, and OFFSET. INTO is still parsed here since it is
    /// a DatumIngest-specific output clause that applies to individual SELECTs.
    /// Used by <see cref="QueryPrimary"/> so that trailing ORDER BY/LIMIT/OFFSET
    /// bind to the compound level rather than to an individual SELECT branch
    /// in set operations.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> BareSelectStatementParser =
        from selectKw in Token.EqualTo(SqlToken.Select)
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from letOrDefine in LetOrDefineParser
        from columns in ColumnList
        from fromClause in FromClauseParser.AsNullable().OptionalOrDefault()
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from crossValidateClause in CrossValidateClauseParser.AsNullable().Try().OptionalOrDefault()
        from groupByClause in GroupByClauseParser.OptionalOrDefault()
        from havingClause in HavingClauseParser.OptionalOrDefault()
        from qualifyClause in QualifyClauseParser.OptionalOrDefault()
        from assertions in AssertClausesParser
        from pivotClause in PivotClauseParser.AsNullable().Try().OptionalOrDefault()
        from unpivotClause in UnpivotClauseParser.AsNullable().Try().OptionalOrDefault()
        from intoClause in IntoClauseParser.OptionalOrDefault()
        select new SelectStatement(
            columns,
            fromClause,
            intoClause,
            joinClauses.Length > 0 ? joinClauses : null,
            whereClause,
            groupByClause,
            havingClause,
            qualifyClause,
            MergeAssertions(letOrDefine.Assertions, assertions),
            pivotClause,
            unpivotClause,
            OrderBy: null,
            Limit: null,
            Offset: null,
            Distinct: distinct.HasValue,
            LetBindings: letOrDefine.LetBindings.Length > 0 ? letOrDefine.LetBindings : null,
            CrossValidate: crossValidateClause);

    /// <summary>
    /// Top-level statement parser: optional WITH clause followed by SELECT.
    /// The WITH clause's CTE definitions are threaded into the <see cref="SelectStatement"/>.
    /// Used only for backward-compatible direct statement parsing.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> StatementParser =
        from ctes in WithClauseParser.OptionalOrDefault()
        from statement in SelectStatementParser
        select ctes is not null && ctes.Length > 0
            ? statement with { CommonTableExpressions = ctes }
            : statement;

    /// <summary>
    /// Bare statement parser: optional WITH clause followed by a SELECT without
    /// trailing ORDER BY/LIMIT/OFFSET/INTO. Used for set operation branches.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> BareStatementParser =
        from ctes in WithClauseParser.OptionalOrDefault()
        from statement in BareSelectStatementParser
        select ctes is not null && ctes.Length > 0
            ? statement with { CommonTableExpressions = ctes }
            : statement;

    // ───────────────────── Compound query (set operations) ─────────────────────

    /// <summary>
    /// A single SELECT statement (with optional WITH preamble) as a query primary.
    /// Uses the bare parser to avoid consuming trailing clauses that should bind
    /// to the compound level.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> QueryPrimary =
        from statement in BareStatementParser
        select (QueryExpression)new SelectQueryExpression(statement);

    /// <summary>
    /// Parses a query term: one or more query primaries combined by INTERSECT [ALL].
    /// INTERSECT binds tighter than UNION/EXCEPT per SQL standard.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> QueryTerm =
        from first in QueryPrimary
        from rest in (
            from intersectKw in Token.EqualTo(SqlToken.Intersect)
            from all in Token.EqualTo(SqlToken.All).OptionalOrDefault()
            from right in QueryPrimary
            select (All: all.HasValue, Right: right)
        ).Many()
        select rest.Aggregate(
            first,
            (left, pair) => new CompoundQueryExpression(
                left, SetOperationType.Intersect, pair.All, pair.Right));

    /// <summary>
    /// Parses a full compound query: one or more query terms combined by UNION [ALL] or EXCEPT [ALL].
    /// UNION and EXCEPT have equal precedence, lower than INTERSECT.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> CompoundQueryParser =
        from first in QueryTerm
        from rest in (
            from op in Token.EqualTo(SqlToken.Union).Value(SetOperationType.Union)
                .Or(Token.EqualTo(SqlToken.Except).Value(SetOperationType.Except))
            from all in Token.EqualTo(SqlToken.All).OptionalOrDefault()
            from right in QueryTerm
            select (OperationType: op, All: all.HasValue, Right: right)
        ).Many()
        select rest.Aggregate(
            first,
            (left, pair) => new CompoundQueryExpression(
                left, pair.OperationType, pair.All, pair.Right));

    /// <summary>
    /// Full query expression parser: compound query optionally followed by ORDER BY, LIMIT,
    /// OFFSET, and INTO that apply to the entire combined result. For a single SELECT without
    /// set operations, these trailing clauses are already parsed on the SelectStatement itself.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> QueryExpressionParser =
        from query in CompoundQueryParser
        from orderBy in OrderByClauseParser.OptionalOrDefault()
        from limit in LimitParser.OptionalOrDefault()
        from offset in OffsetParser.OptionalOrDefault()
        from intoClause in IntoClauseParser.OptionalOrDefault()
        select ApplyTrailingClauses(query, orderBy, limit, offset, intoClause);

    // ───────────────────── DDL / DML statements ─────────────────────

    /// <summary>
    /// Parses an identifier that may also be a keyword token in other contexts.
    /// DDL table/column names like "set", "table", "values" etc. are valid identifiers
    /// when they appear in name position. This parser accepts an <see cref="SqlToken.Identifier"/>
    /// or any keyword token that can legally serve as an unquoted name.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string> IdentifierOrKeywordAsName =
        Token.EqualTo(SqlToken.Identifier).Select(GetTokenText)
            .Or(Token.EqualTo(SqlToken.Table).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Set).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Values).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Column).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Default).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Add).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.If).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Primary).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Key).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Analyze).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.TypeKeyword).Select(t => t.ToStringValue()));

    /// <summary>
    /// Parses a column type name. Accepts a plain identifier and also compound types
    /// like <c>NOT NULL</c> as a suffix modifier.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string> TypeNameParser =
        Token.EqualTo(SqlToken.TypeKeyword).Or(Token.EqualTo(SqlToken.Identifier)).Or(Token.EqualTo(SqlToken.Time))
            .Select(GetTokenText);

    /// <summary>
    /// Parses a single column definition: <c>name type [NOT NULL] [PRIMARY KEY]</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnDefinition> ColumnDefinitionParser =
        from name in IdentifierOrKeywordAsName
        from typeName in TypeNameParser
        from notNull in (
            from notKw in Token.EqualTo(SqlToken.Not)
            from nullKw in Token.EqualTo(SqlToken.Null)
            select true
        ).OptionalOrDefault()
        from primaryKey in (
            from primaryKw in Token.EqualTo(SqlToken.Primary)
            from keyKw in Token.EqualTo(SqlToken.Key)
            select true
        ).OptionalOrDefault()
        select new ColumnDefinition(name, typeName, Nullable: !notNull && !primaryKey, PrimaryKey: primaryKey);

    /// <summary>
    /// Parses <c>IF NOT EXISTS</c> as an optional guard clause for CREATE statements.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> IfNotExistsParser =
        (from ifKw in Token.EqualTo(SqlToken.If)
         from notKw in Token.EqualTo(SqlToken.Not)
         from existsKw in Token.EqualTo(SqlToken.Exists)
         select true
        ).OptionalOrDefault();

    /// <summary>
    /// Parses <c>IF EXISTS</c> as an optional guard clause for DROP statements.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> IfExistsParser =
        (from ifKw in Token.EqualTo(SqlToken.If)
         from existsKw in Token.EqualTo(SqlToken.Exists)
         select true
        ).OptionalOrDefault();

    /// <summary>
    /// Parses a table-level <c>PRIMARY KEY (col1, col2, ...)</c> constraint.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string[]> TablePrimaryKeyConstraintParser =
        from primaryKw in Token.EqualTo(SqlToken.Primary)
        from keyKw in Token.EqualTo(SqlToken.Key)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from names in IdentifierOrKeywordAsName.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select names;

    /// <summary>
    /// Parses a column definition list with an optional trailing table-level
    /// <c>PRIMARY KEY (col, ...)</c> constraint. Uses <c>.Try()</c> on each
    /// <c>(comma, column)</c> pair so that the comma before <c>PRIMARY KEY</c>
    /// is not greedily consumed by a column-definition lookahead.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (ColumnDefinition[] Columns, string[]? PrimaryKeyColumns)>
        ColumnListWithOptionalPrimaryKeyParser =
            from first in ColumnDefinitionParser
            from rest in (
                from comma in Token.EqualTo(SqlToken.Comma)
                from column in ColumnDefinitionParser
                select column
            ).Try().Many()
            from primaryKey in (
                from comma in Token.EqualTo(SqlToken.Comma)
                from constraint in TablePrimaryKeyConstraintParser
                select constraint
            ).AsNullable().OptionalOrDefault()
            select (new[] { first }.Concat(rest).ToArray(), primaryKey);

    /// <summary>
    /// Parses <c>CREATE [TEMP|TEMPORARY] TABLE [IF NOT EXISTS] name (col type, ..., [PRIMARY KEY (col, ...)])</c>.
    /// The <c>TEMP</c>/<c>TEMPORARY</c> keyword is optional — all tables created in a
    /// session are temporary, so <c>CREATE TABLE</c> is accepted as a synonym.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CreateTempTableParser =
        from createKw in Token.EqualTo(SqlToken.Create)
        from optionalTempKw in Token.EqualTo(SqlToken.Temp).Or(Token.EqualTo(SqlToken.Temporary)).OptionalOrDefault()
        from tableKw in Token.EqualTo(SqlToken.Table)
        from ifNotExists in IfNotExistsParser
        from tableName in IdentifierOrKeywordAsName
        from asOrParen in Token.EqualTo(SqlToken.As).Try()
            .Or(Token.EqualTo(SqlToken.LeftParen))
        from statement in asOrParen.Kind == SqlToken.As
            ? SP.Ref(() => QueryExpressionParser!)
                .Select(query => (Statement)new CreateTempTableAsSelectStatement(tableName, query, ifNotExists))
            : ColumnListWithOptionalPrimaryKeyParser
                .Then(result => Token.EqualTo(SqlToken.RightParen)
                    .Select(_ =>
                    {
                        IReadOnlyList<string>? primaryKeyColumns = result.PrimaryKeyColumns;
                        if (primaryKeyColumns is null)
                        {
                            List<string>? inlineKeys = null;
                            foreach (ColumnDefinition column in result.Columns)
                            {
                                if (column.PrimaryKey)
                                {
                                    inlineKeys ??= new List<string>();
                                    inlineKeys.Add(column.Name);
                                }
                            }
                            primaryKeyColumns = inlineKeys;
                        }
                        return (Statement)new CreateTempTableStatement(tableName, result.Columns, ifNotExists,
                            PrimaryKeyColumns: primaryKeyColumns);
                    }))
        select statement;

    /// <summary>
    /// Parses <c>DROP TABLE [IF EXISTS] name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropTableParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        from tableKw in Token.EqualTo(SqlToken.Table)
        from ifExists in IfExistsParser
        from tableName in IdentifierOrKeywordAsName
        select (Statement)new DropTableStatement(tableName, ifExists);

    /// <summary>
    /// Parses a single UDF parameter declaration: <c>name TYPE</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, UdfParameter> UdfParameterParser =
        from name in IdentifierOrKeywordAsName
        from typeName in TypeNameParser
        select new UdfParameter(name, typeName);

    /// <summary>
    /// Parses <c>OR REPLACE</c> as an optional modifier for <c>CREATE FUNCTION</c>.
    /// REPLACE is tokenized as a SQL keyword (used in <c>* REPLACE (...)</c>); we
    /// reuse the same token here.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> OrReplaceParser =
        (from orKw in Token.EqualTo(SqlToken.Or)
         from replaceKw in Token.EqualTo(SqlToken.Replace)
         select true
        ).OptionalOrDefault();

    /// <summary>
    /// Parses
    /// <c>CREATE [OR REPLACE] FUNCTION [IF NOT EXISTS] name(p1 TYPE [, p2 TYPE ...]) [RETURNS TYPE] AS expression</c>.
    /// The body is any scalar expression and may reference the parameters by
    /// name, plus any normal column / function names — the planner inlines the
    /// body at every call site so name resolution happens in the caller's scope.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CreateFunctionParser =
        from createKw in Token.EqualTo(SqlToken.Create)
        from orReplace in OrReplaceParser
        from functionKw in Token.EqualTo(SqlToken.Function)
        from ifNotExists in IfNotExistsParser
        // UDF names are permissive: accept any keyword that can serve as an
        // unquoted name (so e.g. CREATE FUNCTION add(...) doesn't trip on
        // ADD being tokenized as a keyword).
        from name in IdentifierOrKeywordAsName
        from open in Token.EqualTo(SqlToken.LeftParen)
        from parameters in UdfParameterParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        from returnType in (
            from returnsKw in Token.EqualTo(SqlToken.Returns)
            from typeName in TypeNameParser
            select typeName
        ).AsNullable().OptionalOrDefault()
        from asKw in Token.EqualTo(SqlToken.As)
        from body in SP.Ref(() => ExpressionParser!)
        select (Statement)new CreateFunctionStatement(
            name,
            parameters,
            returnType,
            body,
            ifNotExists,
            orReplace,
            Span: null);

    /// <summary>
    /// Parses <c>DROP FUNCTION [IF EXISTS] name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropFunctionParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        from functionKw in Token.EqualTo(SqlToken.Function)
        from ifExists in IfExistsParser
        from name in IdentifierOrKeywordAsName
        select (Statement)new DropFunctionStatement(
            name,
            ifExists,
            Span: null);

    /// <summary>
    /// Parses <c>INSERT INTO name [(col, ...)] SELECT ...</c> or
    /// <c>INSERT INTO name [(col, ...)] VALUES (...), (...)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> InsertParser =
        from insertKw in Token.EqualTo(SqlToken.Insert)
        from intoKw in Token.EqualTo(SqlToken.Into)
        from tableName in IdentifierOrKeywordAsName
        from columnNames in (
            from open in Token.EqualTo(SqlToken.LeftParen)
            from names in IdentifierOrKeywordAsName.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from close in Token.EqualTo(SqlToken.RightParen)
            select names
        ).AsNullable().OptionalOrDefault()
        from source in ValuesSourceParser.Select(v => (InsertSource)v).Try()
            .Or(SP.Ref(() => QueryExpressionParser!).Select(q => (InsertSource)new InsertQuerySource(q)))
        select (Statement)new InsertStatement(
            tableName,
            columnNames is { Length: > 0 } ? columnNames : null,
            source);

    /// <summary>
    /// Parses <c>VALUES (expr, ...), (expr, ...) ...</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, InsertValuesSource> ValuesSourceParser =
        from valuesKw in Token.EqualTo(SqlToken.Values)
        from rows in (
            from open in Token.EqualTo(SqlToken.LeftParen)
            from values in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from close in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<Expression>)values
        ).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select new InsertValuesSource(rows);

    /// <summary>
    /// Parses <c>UPDATE name [AS alias] SET col = expr [, ...] [FROM source [JOIN ...]*] [WHERE ...]</c>.
    /// Follows PostgreSQL semantics: the target table is not repeated in FROM; the WHERE clause
    /// contains both join conditions and filters.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> UpdateParser =
        from updateKw in Token.EqualTo(SqlToken.Update)
        from tableName in IdentifierOrKeywordAsName
        from alias in (
            from _as in Token.EqualTo(SqlToken.As).OptionalOrDefault()
            from aliasName in IdentifierLike.Select(GetTokenText)
            select aliasName
        ).Try().AsNullable().OptionalOrDefault()
        from setKw in Token.EqualTo(SqlToken.Set)
        from assignments in (
            from colName in IdentifierOrKeywordAsName
            from eq in Token.EqualTo(SqlToken.Equals)
            from value in SP.Ref(() => ExpressionParser!)
            select new ColumnAssignment(colName, value)
        ).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from fromClause in FromClauseParser.AsNullable().OptionalOrDefault()
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        select (Statement)new UpdateStatement(
            tableName,
            alias,
            assignments,
            fromClause,
            joinClauses.Length > 0 ? joinClauses : null,
            whereClause);

    /// <summary>
    /// Parses <c>DELETE FROM name [WHERE ...]</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DeleteParser =
        from deleteKw in Token.EqualTo(SqlToken.Delete)
        from fromKw in Token.EqualTo(SqlToken.From)
        from tableName in IdentifierOrKeywordAsName
        from whereClause in WhereClauseParser.OptionalOrDefault()
        select (Statement)new DeleteStatement(tableName, whereClause);

    /// <summary>
    /// Parses <c>ALTER TABLE name ADD [COLUMN] col type [NOT NULL] [DEFAULT expr | AS expr]</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> AlterTableParser =
        from alterKw in Token.EqualTo(SqlToken.Alter)
        from tableKw in Token.EqualTo(SqlToken.Table)
        from tableName in IdentifierOrKeywordAsName
        from addKw in Token.EqualTo(SqlToken.Add)
        from columnKw in Token.EqualTo(SqlToken.Column).OptionalOrDefault()
        from colName in IdentifierOrKeywordAsName
        from typeName in TypeNameParser
        from notNull in (
            from notKw in Token.EqualTo(SqlToken.Not)
            from nullKw in Token.EqualTo(SqlToken.Null)
            select true
        ).OptionalOrDefault()
        from defaultValue in (
            from defaultKw in Token.EqualTo(SqlToken.Default)
            from expr in SP.Ref(() => ExpressionParser!)
            select expr
        ).AsNullable().OptionalOrDefault()
        from computedExpression in (
            from asKw in Token.EqualTo(SqlToken.As)
            from expr in SP.Ref(() => ExpressionParser!)
            select expr
        ).AsNullable().OptionalOrDefault()
        select (Statement)new AlterTableAddColumnStatement(
            tableName, colName, typeName, defaultValue, Nullable: !notNull,
            ComputedExpression: computedExpression);

    /// <summary>
    /// Parses a single statement: a DDL/DML command or a query expression.
    /// </summary>
    /// <summary>
    /// Parses <c>ANALYZE table</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> AnalyzeTableParser =
        from analyzeKw in Token.EqualTo(SqlToken.Analyze)
        from tableName in IdentifierOrKeywordAsName
        select (Statement)new AnalyzeTableStatement(tableName);

    /// <summary>
    /// Parses a single statement: a DDL/DML command or a query expression.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> SingleStatementParser =
        CreateFunctionParser.Try()
            .Or(DropFunctionParser.Try())
            .Or(CreateTempTableParser.Try())
            .Or(DropTableParser.Try())
            .Or(InsertParser.Try())
            .Or(UpdateParser.Try())
            .Or(DeleteParser.Try())
            .Or(AlterTableParser.Try())
            .Or(AnalyzeTableParser.Try())
            .Or(QueryExpressionParser.Select(q => (Statement)new QueryStatement(q)));

    /// <summary>
    /// Parses a batch of semicolon-separated statements. Trailing semicolons
    /// and empty statements between semicolons are silently ignored.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<Statement>> BatchParser =
        from first in SingleStatementParser
        from rest in (
            from semi in Token.EqualTo(SqlToken.Semicolon).AtLeastOnce()
            from stmt in SingleStatementParser
            select stmt
        ).Try().Many()
        from trailing in Token.EqualTo(SqlToken.Semicolon).Many()
        select (IReadOnlyList<Statement>)(new[] { first }.Concat(rest).ToArray());

    /// <summary>The full batch parser that expects to consume all input.</summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<Statement>> FullBatchParser =
        BatchParser.AtEnd();

    /// <summary>The full query expression parser that expects to consume all input.</summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> FullParser =
        QueryExpressionParser.AtEnd();

    /// <summary>
    /// Set of tokens that begin top-level clauses. Used by the error-recovering
    /// parser to find the next safe synchronization point after a clause failure.
    /// </summary>
    private static readonly HashSet<SqlToken> ClauseStartTokens =
    [
        SqlToken.With,
        SqlToken.Select,
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
        SqlToken.Qualify,
        SqlToken.Assert,
        SqlToken.Define,
        SqlToken.Pivot,
        SqlToken.Unpivot,
        SqlToken.Into,
        SqlToken.Order,
        SqlToken.Limit,
        SqlToken.Offset,
        SqlToken.Union,
        SqlToken.Intersect,
        SqlToken.Except,
        SqlToken.Create,
        SqlToken.Drop,
        SqlToken.Insert,
        SqlToken.Update,
        SqlToken.Delete,
        SqlToken.Alter,
    ];

    // ───────────────────── Public API ─────────────────────

    /// <summary>
    /// Parses a SQL string into a <see cref="QueryExpression"/> AST.
    /// </summary>
    /// <param name="sql">The SQL query text.</param>
    /// <returns>The parsed AST.</returns>
    /// <exception cref="ParseException">Thrown when the input cannot be parsed.</exception>
    public static QueryExpression Parse(string sql)
    {
        TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);
        TokenListParserResult<SqlToken, QueryExpression> result = FullParser.TryParse(tokens);

        if (!result.HasValue)
        {
            throw new ParseException(
                result.ToString(),
                result.ErrorPosition);
        }

        return result.Value;
    }

    /// <summary>
    /// Parses a SQL string into a single <see cref="Statement"/> AST node.
    /// Accepts both query expressions and DDL/DML statements.
    /// </summary>
    /// <param name="sql">The SQL statement text.</param>
    /// <returns>The parsed statement.</returns>
    /// <exception cref="ParseException">Thrown when the input cannot be parsed.</exception>
    public static Statement ParseStatement(string sql)
    {
        TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);
        TokenListParserResult<SqlToken, Statement> result = SingleStatementParser.AtEnd().TryParse(tokens);

        if (!result.HasValue)
        {
            throw new ParseException(
                result.ToString(),
                result.ErrorPosition);
        }

        return result.Value;
    }

    /// <summary>
    /// Parses a SQL string containing one or more semicolon-separated statements
    /// into a list of <see cref="Statement"/> AST nodes. Trailing semicolons
    /// and empty statements between semicolons are silently ignored.
    /// </summary>
    /// <param name="sql">The SQL text containing one or more statements.</param>
    /// <returns>The list of parsed statements in order.</returns>
    /// <exception cref="ParseException">Thrown when the input cannot be parsed.</exception>
    public static IReadOnlyList<Statement> ParseBatch(string sql)
    {
        TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);
        TokenListParserResult<SqlToken, IReadOnlyList<Statement>> result = FullBatchParser.TryParse(tokens);

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
        TokenList<SqlToken> tokens;
        try
        {
            tokens = SqlTokenizer.Instance.Tokenize(sql);
        }
        catch (Superpower.ParseException ex)
        {
            // The tokenizer throws when it encounters an untokenizable sequence
            // (e.g. an incomplete quoted identifier like `"foo`). Convert the
            // exception into a ParseResult with an error diagnostic so the
            // language server can report it gracefully.
            return new ParseResult(
                query: null,
                [new ParseError
                {
                    Message = ex.Message,
                    Line = ex.ErrorPosition.Line,
                    Column = ex.ErrorPosition.Column,
                }]);
        }

        // Fast path: try the full batch parser first. This handles all statement
        // types (SELECT, CREATE, INSERT, UPDATE, DELETE, ALTER, ANALYZE) and
        // semicolon-separated batches. If it succeeds, no recovery needed.
        // Wrap in try/catch because some parser-side lowerings (template-string
        // splice parsing) raise DatumIngest ParseException directly rather than
        // failing through the combinator's HasValue path; recovering callers
        // (the language server) need a diagnostic, not an exception.
        TokenListParserResult<SqlToken, IReadOnlyList<Statement>> batchResult;
        try
        {
            batchResult = FullBatchParser.TryParse(tokens);
        }
        catch (ParseException ex)
        {
            return new ParseResult(
                query: null,
                [new ParseError
                {
                    Message = ex.Message,
                    Line = ex.ErrorPosition.Line,
                    Column = ex.ErrorPosition.Column,
                }]);
        }

        if (batchResult.HasValue)
        {
            return new ParseResult(batchResult.Value);
        }

        // Recovery path: parse clause-by-clause, collecting errors.
        // This only handles SELECT queries — DDL/DML that failed the fast path
        // will produce an "Expected SELECT keyword." error, which is appropriate
        // since the DDL/DML itself was syntactically invalid.
        try
        {
            return ParseWithRecovery(tokens);
        }
        catch (ParseException ex)
        {
            return new ParseResult(
                query: null,
                [new ParseError
                {
                    Message = ex.Message,
                    Line = ex.ErrorPosition.Line,
                    Column = ex.ErrorPosition.Column,
                }]);
        }
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

        // ── WITH clause (CTEs) ──
        CommonTableExpression[]? commonTableExpressions = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.With)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, CommonTableExpression[]> withResult =
                WithClauseParser.TryParse(remaining);

            if (!withResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid WITH clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                commonTableExpressions = withResult.Value;
                position += CountConsumed(tokenArray, position, withResult.Remainder);
            }
        }

        // ── SELECT columns ──
        SelectColumn[]? columns = null;
        LetBinding[]? recoveryLetBindings = null;
        List<AssertClause> recoveryDefineAssertions = new();
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

                // ── DEFINE block or inline LET bindings ──
                if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Define)
                {
                    TokenList<SqlToken> defineRemaining = new(tokenArray[position..]);
                    TokenListParserResult<SqlToken, (LetBinding[] LetBindings, AssertClause[] Assertions)> defineResult =
                        DefineBlockParser.TryParse(defineRemaining);

                    if (!defineResult.HasValue)
                    {
                        AddErrorFromToken(errors, tokenArray, position, "Invalid DEFINE block.");
                        position = SkipToNextClauseIndex(tokenArray, position + 1);
                    }
                    else
                    {
                        if (defineResult.Value.LetBindings.Length > 0)
                            recoveryLetBindings = defineResult.Value.LetBindings;
                        recoveryDefineAssertions.AddRange(defineResult.Value.Assertions);
                        position += CountConsumed(tokenArray, position, defineResult.Remainder);
                    }
                }
                else
                {
                    TokenList<SqlToken> letRemaining = new(tokenArray[position..]);
                    TokenListParserResult<SqlToken, LetBinding[]> letResult =
                        LetBindingsParser.TryParse(letRemaining);

                    if (letResult.HasValue && letResult.Value.Length > 0)
                    {
                        recoveryLetBindings = letResult.Value;
                        position += CountConsumed(tokenArray, position, letResult.Remainder);
                    }
                }

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

        // ── FROM clause (optional) ──
        FromClause? fromClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.From)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, FromClause> fromResult =
                FromClauseParser.TryParse(remaining);

            if (!fromResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid FROM clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                fromClause = fromResult.Value;
                position += CountConsumed(tokenArray, position, fromResult.Remainder);
            }
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

        // ── QUALIFY clause ──
        Expression? qualifyClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Qualify)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression> qualifyResult =
                QualifyClauseParser.TryParse(remaining);

            if (!qualifyResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid QUALIFY clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                qualifyClause = qualifyResult.Value;
                position += CountConsumed(tokenArray, position, qualifyResult.Remainder);
            }
        }

        // ── ASSERT clauses ──
        List<AssertClause> assertions = new();
        while (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Assert)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, AssertClause> assertResult =
                AssertClauseParser.TryParse(remaining);

            if (!assertResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid ASSERT clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
                break;
            }
            else
            {
                assertions.Add(assertResult.Value);
                position += CountConsumed(tokenArray, position, assertResult.Remainder);
            }
        }

        // ── PIVOT clause ──
        PivotClause? pivotClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Pivot)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, PivotClause> pivotResult =
                PivotClauseParser.TryParse(remaining);

            if (!pivotResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid PIVOT clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                pivotClause = pivotResult.Value;
                position += CountConsumed(tokenArray, position, pivotResult.Remainder);
            }
        }

        // ── UNPIVOT clause ──
        UnpivotClause? unpivotClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Unpivot)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, UnpivotClause> unpivotResult =
                UnpivotClauseParser.TryParse(remaining);

            if (!unpivotResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid UNPIVOT clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                unpivotClause = unpivotResult.Value;
                position += CountConsumed(tokenArray, position, unpivotResult.Remainder);
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

        // ── Trailing semicolons ──
        while (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Semicolon)
        {
            position++;
        }

        // ── Trailing tokens ──
        if (position < tokenArray.Length)
        {
            AddErrorFromToken(errors, tokenArray, position, "Unexpected tokens after end of statement.");
        }

        // Build partial AST if we have enough structure.
        QueryExpression? query = null;
        if (columns is not null)
        {
            SelectStatement statement = new(
                columns,
                fromClause,
                intoClause,
                joinClauses.Count > 0 ? joinClauses.ToArray() : null,
                whereClause,
                groupByClause,
                havingClause,
                qualifyClause,
                MergeAssertions(recoveryDefineAssertions.ToArray(), assertions.Count > 0 ? assertions.ToArray() : Array.Empty<AssertClause>()),
                pivotClause,
                unpivotClause,
                orderByClause,
                limitValue,
                offsetValue,
                CommonTableExpressions: commonTableExpressions,
                LetBindings: recoveryLetBindings);
            query = new SelectQueryExpression(statement);
        }

        return new ParseResult(query, errors);
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

        if (path.EndsWith(".datum", StringComparison.OrdinalIgnoreCase))
        {
            return OutputFormat.Datum;
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
