using System.Linq;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Parsing.Tokens;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using SP = Superpower.Parse;

#pragma warning disable CS8603, CS8604, CS8620 // Superpower combinators lack consistent nullable reference type annotations

namespace Heliosoph.DatumV.Parsing;

public static partial class SqlParser
{
    // ───────────────────── Atomic expressions ─────────────────────

    /// <summary>
    /// Column reference: bare <c>col</c>, <c>table.col</c> (or <c>table.*</c>),
    /// or <c>schema.table.col</c> (or <c>schema.table.*</c>). The third
    /// segment is the only one that may be <c>*</c> — <c>schema.*.col</c>
    /// has no meaningful shape.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> QualifiedColumn =
        from first in ColumnNameToken
        from second in (
            from dot in Token.EqualTo(SqlToken.Dot)
            from name in PostDotColumnNameToken
                .Or(Token.EqualTo(SqlToken.Star))
            select name
        ).OptionalOrDefault()
        from third in (
            // Only attempt a third segment if the second was a regular
            // identifier; `schema.*.col` is parser-invalid. `.Try()` so a
            // two-part reference followed by a dot in a different context
            // (e.g. concatenation chain) backtracks cleanly.
            second.HasValue && second.Kind != SqlToken.Star
                ? (from dot in Token.EqualTo(SqlToken.Dot)
                   from name in PostDotColumnNameToken
                       .Or(Token.EqualTo(SqlToken.Star))
                   select name).Try().OptionalOrDefault()
                : Superpower.Parse.Return<SqlToken, Token<SqlToken>>(default)
        )
        select third.HasValue
            ? (Expression)new ColumnReference(
                TableName: GetTokenText(second),
                ColumnName: third.Kind == SqlToken.Star ? "*" : GetTokenText(third),
                Span: ToSpan(first, third),
                SchemaName: GetTokenText(first))
            : second.HasValue
                ? new ColumnReference(
                    TableName: GetTokenText(first),
                    ColumnName: second.Kind == SqlToken.Star ? "*" : GetTokenText(second),
                    Span: ToSpan(first, second))
                : new ColumnReference(
                    TableName: null,
                    ColumnName: GetTokenText(first),
                    Span: ToSpan(first));

    /// <summary>
    /// Number literal parsed as the narrowest type that fits the value.
    /// Whole numbers: sbyte (-128..127) → short → int → long based on magnitude.
    /// Decimals/scientific notation: float if no precision loss, otherwise double.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> NumberLiteral =
        Token.EqualTo(SqlToken.NumberLiteral)
            .Select(token => (Expression)new LiteralExpression(ParseNumericLiteral(token.ToStringValue())));

    /// <summary>
    /// Parses a numeric literal token (digits, optional '.', optional exponent)
    /// into the narrowest CLR type that represents it exactly. Pure-integer
    /// text walks sbyte → short → int → long → ulong → Int128 → UInt128.
    /// Fractional or exponent-bearing text parses as double; if the value is
    /// whole (e.g. <c>1.0</c>) it then narrows through the integer ladder, and
    /// otherwise narrows to float when the float round-trip is exact. The
    /// token never carries a sign — unary minus is a separate parse node.
    /// </summary>
    private static object ParseNumericLiteral(string text)
    {
        bool fractional = text.IndexOf('.') >= 0
            || text.IndexOf('e') >= 0
            || text.IndexOf('E') >= 0;

        if (fractional)
        {
            double d = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (d == System.Math.Truncate(d) && !double.IsInfinity(d))
            {
                // Whole-valued fractional literal (e.g. 1.0, 1e3) — narrow
                // through the integer ladder. A finite whole double fits in
                // long (|d| <= 2^53), so we never need the wider rungs.
                long whole = (long)d;
                return NarrowSignedInt(whole);
            }
            float f = (float)d;
            // Boxing the float-branch result preserves the runtime type as
            // System.Single; without the (object) cast the C# ternary unifies
            // to double and the narrowing is silently discarded.
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            return (double)f == d ? (object)f : d;
        }

        System.Globalization.NumberStyles style = System.Globalization.NumberStyles.None;
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;
        if (sbyte.TryParse(text, style, culture, out sbyte sb)) return sb;
        if (short.TryParse(text, style, culture, out short s)) return s;
        if (int.TryParse(text, style, culture, out int i)) return i;
        if (long.TryParse(text, style, culture, out long l)) return l;
        if (ulong.TryParse(text, style, culture, out ulong u)) return u;
        if (Int128.TryParse(text, style, culture, out Int128 i128)) return i128;
        if (UInt128.TryParse(text, style, culture, out UInt128 u128)) return u128;
        throw new FormatException($"Integer literal '{text}' exceeds 128-bit range.");
    }

    private static object NarrowSignedInt(long value)
    {
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue) return (sbyte)value;
        if (value >= short.MinValue && value <= short.MaxValue) return (short)value;
        if (value >= int.MinValue && value <= int.MaxValue) return (int)value;
        return value;
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
            .Or(Token.EqualTo(SqlToken.Analyze))
            .Or(Token.EqualTo(SqlToken.Reindex))
            // STEP / UNIT / COMMENT are reserved as parameter-clause keywords
            // but still permitted as bare function names — existing UDF tests
            // freely name their helpers `step`/`unit`/`comment`. Letting them
            // through here preserves that surface without making the keywords
            // ambiguous inside the parameter parser, which has a fixed sequence.
            .Or(Token.EqualTo(SqlToken.Step))
            .Or(Token.EqualTo(SqlToken.Unit))
            .Or(Token.EqualTo(SqlToken.Comment))
            .Or(Token.EqualTo(SqlToken.Check))
            // REPLACE is a soft keyword used by `CREATE OR REPLACE ...` DDL
            // and `SELECT * REPLACE (...)` projection; neither shape applies
            // in function-call position, so it's safe to surface here as the
            // name of PostgreSQL's `replace(string, from, to)` scalar.
            .Or(Token.EqualTo(SqlToken.Replace));

    private static readonly TokenListParser<SqlToken, (string? Namespace, Superpower.Model.Token<SqlToken> Name)> NamespacedFunctionName =
        (from ns in Token.EqualTo(SqlToken.Identifier)
         from dot in Token.EqualTo(SqlToken.Dot)
         from name in FunctionNameToken
         select ((string?)GetTokenText(ns), name))
        .Try()
        .Or(Token.EqualTo(SqlToken.Identifier).Select(name => ((string?)null, name)))
        // Allow keyword tokens that can also serve as bare function names
        // (matches FunctionNameToken's permissive list for the unqualified
        // case — qualifier path still requires an Identifier namespace).
        .Or(Token.EqualTo(SqlToken.Step).Select(name => ((string?)null, name)))
        .Or(Token.EqualTo(SqlToken.Unit).Select(name => ((string?)null, name)))
        .Or(Token.EqualTo(SqlToken.Comment).Select(name => ((string?)null, name)))
        .Or(Token.EqualTo(SqlToken.Check).Select(name => ((string?)null, name)))
        .Or(Token.EqualTo(SqlToken.Replace).Select(name => ((string?)null, name)));

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
    /// <summary>
    /// One slot in a function call's argument list. <c>Name</c> is non-null
    /// when the argument was supplied as <c>name := expr</c> or
    /// <c>name =&gt; expr</c> (PG-style named argument); null for the
    /// classic positional form. The <c>NamedArgPermuter</c> planner pass
    /// resolves names against the function's signature and rewrites the
    /// call into a fully-positional shape before type resolution.
    /// </summary>
    /// <summary>
    /// Atomic lookahead for the named-argument prefix
    /// <c>identifier (:= | =&gt;)</c>. Wrapping just the prefix in
    /// <c>.Try()</c> commits the parser to the named form as soon as the
    /// operator is seen — failures while parsing the right-hand
    /// expression aren't backtracked (which would otherwise re-attempt
    /// the bare-expression fallback and surface a misleading error
    /// pointing at the outer call rather than the actual failure site).
    /// </summary>
    private static readonly TokenListParser<SqlToken, string> NamedArgumentPrefix =
        (from name in SP.Ref(() => IdentifierOrKeywordAsName!)
         from op in Token.EqualTo(SqlToken.ColonEquals)
             .Or(Token.EqualTo(SqlToken.FatArrow))
         select name).Try();

    private static readonly TokenListParser<SqlToken, (string? Name, Expression Expr)> NamedOrPositionalArgument =
        // Try the named form first: identifier followed by := or =>.
        // The lookahead .Try() sits on the prefix so the expression
        // value is parsed without further backtracking.
        (from name in NamedArgumentPrefix
         from expr in SP.Ref(() => ExpressionParser!)
         select ((string?)name, expr))
        .Or(Token.EqualTo(SqlToken.Star).Select(_ => ((string?)null, (Expression)new LiteralExpression("*"))))
        .Or(SP.Ref(() => ExpressionParser!).Select(e => ((string?)null, e)));

    private static readonly TokenListParser<SqlToken, Expression> FunctionCall =
        from nameTuple in NamespacedFunctionName
        from open in Token.EqualTo(SqlToken.LeftParen)
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from rawArgs in NamedOrPositionalArgument
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
        let bareName = GetTokenText(nameTuple.Name)
        let schemaName = nameTuple.Namespace
        let args = ExtractArgumentExpressions(rawArgs)
        let argNames = ExtractArgumentNames(rawArgs)
        select withinGroup is not null
            ? (Expression)new FunctionCallExpression(
                bareName,
                args,
                OrderBy: null,
                Distinct: distinct.HasValue,
                Span: ToSpan(name),
                WithinGroupOrderBy: withinGroup,
                SchemaName: schemaName,
                ArgumentNames: argNames)
            : windowSpec is not null
                ? BuildWindowCall(bareName, args, argNames, windowSpec, distinct.HasValue, nullHandling, fromLast, ToSpan(name), schemaName)
                : (Expression)new FunctionCallExpression(bareName, args, OrderBy: orderBy, Distinct: distinct.HasValue, Span: ToSpan(name), SchemaName: schemaName, ArgumentNames: argNames);

    /// <summary>
    /// Builds a <see cref="WindowFunctionCallExpression"/>, rejecting
    /// PG-style named arguments — window functions don't carry parameter-
    /// name metadata through to the planner today, so silently dropping
    /// the supplied names would cause a confusing arity-mismatch error
    /// later. Surface the limitation here with a precise message.
    /// </summary>
    private static Expression BuildWindowCall(
        string bareName,
        IReadOnlyList<Expression> args,
        IReadOnlyList<string?>? argNames,
        WindowSpecification windowSpec,
        bool distinct,
        NullHandling nullHandling,
        bool fromLast,
        SourceSpan? span,
        string? schemaName)
    {
        if (argNames is not null)
        {
            throw new InvalidOperationException(
                $"Named arguments (name := value / name => value) are not supported on " +
                $"window function calls. Use positional arguments for '{bareName}(...) OVER (...)'.");
        }
        return new WindowFunctionCallExpression(
            bareName, args, windowSpec,
            Distinct: distinct, NullHandling: nullHandling, FromLast: fromLast,
            Span: span, SchemaName: schemaName);
    }

    /// <summary>
    /// Extracts the expression payload from each parsed argument slot.
    /// Returns a fresh array suitable for storage on
    /// <see cref="FunctionCallExpression.Arguments"/>.
    /// </summary>
    private static Expression[] ExtractArgumentExpressions((string? Name, Expression Expr)[] args)
    {
        Expression[] result = new Expression[args.Length];
        for (int i = 0; i < args.Length; i++) result[i] = args[i].Expr;
        return result;
    }

    /// <summary>
    /// Extracts the parallel parameter-name list from each parsed argument
    /// slot. Returns <see langword="null"/> when every argument is positional
    /// so the common case stores no extra state and downstream visitors keep
    /// the existing fast paths.
    /// </summary>
    private static IReadOnlyList<string?>? ExtractArgumentNames((string? Name, Expression Expr)[] args)
    {
        bool anyNamed = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Name is not null) { anyNamed = true; break; }
        }
        if (!anyNamed) return null;

        string?[] names = new string?[args.Length];
        for (int i = 0; i < args.Length; i++) names[i] = args[i].Name;
        return names;
    }

    /// <summary>CAST( expression AS type ) — accepts scalar names, the
    /// <c>Array&lt;T&gt;</c> wrapper, and the <c>T[]</c> sugar via the shared
    /// <see cref="TypeNameParser"/>.</summary>
    private static readonly TokenListParser<SqlToken, Expression> CastCall =
        from cast in Token.EqualTo(SqlToken.Cast)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from expression in SP.Ref(() => ExpressionParser!)
        from asKw in Token.EqualTo(SqlToken.As)
        from targetType in SP.Ref(() => TypeNameParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (Expression)new CastExpression(expression, targetType, ToSpan(cast, close));

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

    // ───────────────────── PG-style typed temporal literals ─────────────────────

    /// <summary>
    /// PostgreSQL-style typed string literal — <c>DATE 'YYYY-MM-DD'</c>,
    /// <c>TIMESTAMP '...'</c>, <c>TIMESTAMPTZ '...'</c>, <c>TIME '...'</c>,
    /// <c>INTERVAL '...'</c>. Recognised in expression position and lowered to
    /// <c>CAST('...' AS &lt;Kind&gt;)</c> so the existing string→temporal
    /// coercion path produces the typed value at runtime; no engine plumbing
    /// changes.
    /// <para>
    /// The single PG form not yet represented — <c>TIMETZ '...'</c> — is still
    /// recognised here so callers get an explicit "not yet supported"
    /// <see cref="ParseException"/> instead of the misleading "unexpected
    /// string literal after identifier" they would otherwise see.
    /// </para>
    /// <para>
    /// Semantic note: <c>TIMESTAMP '...'</c> produces a <c>Timestamp</c>
    /// (PG <c>timestamp without time zone</c>, naive); <c>TIMESTAMPTZ '...'</c>
    /// produces a <c>TimestampTz</c> (PG <c>timestamp with time zone</c>,
    /// UTC ticks; input offset normalised at construction);
    /// <c>INTERVAL '...'</c> produces an <c>Interval</c>
    /// (calendar-aware months/days/microseconds triple).
    /// </para>
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> TypedTemporalLiteral =
        from prefix in Token.EqualTo(SqlToken.TypeKeyword)
            .Or(Token.EqualTo(SqlToken.Time))
            .Or(Token.EqualTo(SqlToken.Identifier))
            .Where(t => IsTypedTemporalPrefixText(GetTokenText(t)),
                "DATE / TIMESTAMP / TIMESTAMPTZ / TIME / INTERVAL / TIMETZ")
        from literal in Token.EqualTo(SqlToken.StringLiteral)
        from qualifier in IntervalQualifierTail.OptionalOrDefault()
        select BuildTypedTemporalLiteral(prefix, literal, qualifier);

    /// <summary>
    /// Optional trailing qualifier after an <c>INTERVAL '...'</c> literal:
    /// a single unit (<c>YEAR</c>, <c>MONTH</c>, …) or a span
    /// (<c>YEAR TO MONTH</c>, <c>DAY TO SECOND</c>, …). Returns the canonical
    /// PG-cased qualifier text, or <c>null</c> when no qualifier follows.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string?> IntervalQualifierTail =
        (from first in Token.EqualTo(SqlToken.Identifier)
            .Where(t => IsIntervalUnitText(GetTokenText(t)), "interval unit")
         from rest in (
            from to in Token.EqualTo(SqlToken.To)
            from second in Token.EqualTo(SqlToken.Identifier)
                .Where(t => IsIntervalUnitText(GetTokenText(t)), "interval unit")
            select GetTokenText(second))
            .OptionalOrDefault()
         select rest is null
             ? GetTokenText(first).ToUpperInvariant()
             : $"{GetTokenText(first).ToUpperInvariant()} TO {rest.ToUpperInvariant()}"
        ).Try();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="text"/> names a
    /// single interval unit (<c>YEAR</c> / <c>MONTH</c> / <c>DAY</c> /
    /// <c>HOUR</c> / <c>MINUTE</c> / <c>SECOND</c>) — case-insensitive.
    /// </summary>
    private static bool IsIntervalUnitText(string text)
    {
        return text.Equals("YEAR", StringComparison.OrdinalIgnoreCase)
            || text.Equals("MONTH", StringComparison.OrdinalIgnoreCase)
            || text.Equals("DAY", StringComparison.OrdinalIgnoreCase)
            || text.Equals("HOUR", StringComparison.OrdinalIgnoreCase)
            || text.Equals("MINUTE", StringComparison.OrdinalIgnoreCase)
            || text.Equals("SECOND", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recognises the set of PG-style temporal prefixes that this combinator
    /// claims responsibility for, including the not-yet-supported forms.
    /// Matching here means the combinator commits to producing either a
    /// typed-literal expression or an explicit "not yet supported"
    /// <see cref="ParseException"/>.
    /// </summary>
    private static bool IsTypedTemporalPrefixText(string text)
    {
        return text.Equals("DATE", StringComparison.OrdinalIgnoreCase)
            || text.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            || text.Equals("TIMESTAMPTZ", StringComparison.OrdinalIgnoreCase)
            || text.Equals("TIME", StringComparison.OrdinalIgnoreCase)
            || text.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase)
            || text.Equals("TIMETZ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lowers a matched <c>&lt;PREFIX&gt; 'literal'</c> pair to a typed
    /// expression. Supported prefixes lower to a <see cref="CastExpression"/>;
    /// unsupported prefixes throw <see cref="ParseException"/> anchored at
    /// the prefix token's position. An optional trailing
    /// <paramref name="qualifier"/> applies only to <c>INTERVAL</c> and
    /// routes the literal through the <c>interval_qualified(text, qualifier)</c>
    /// scalar function instead of the bare cast.
    /// </summary>
    private static Expression BuildTypedTemporalLiteral(
        Token<SqlToken> prefix,
        Token<SqlToken> literal,
        string? qualifier)
    {
        string text = UnquoteString(literal);
        string kind = GetTokenText(prefix).ToUpperInvariant();
        SourceSpan span = ToSpan(prefix, literal);
        LiteralExpression inner = new(text);

        if (qualifier is not null && kind != "INTERVAL")
        {
            throw new ParseException(
                $"Trailing qualifier '{qualifier}' is only valid on INTERVAL literals.",
                prefix.Position);
        }

        return kind switch
        {
            "DATE" => new CastExpression(inner, "Date", span),
            "TIMESTAMP" => new CastExpression(inner, "Timestamp", span),
            "TIMESTAMPTZ" => new CastExpression(inner, "TimestampTz", span),
            "TIME" => new CastExpression(inner, "Time", span),
            "INTERVAL" => qualifier is null
                ? new CastExpression(inner, "Interval", span)
                : new FunctionCallExpression(
                    "interval_qualified",
                    [inner, new LiteralExpression(qualifier)],
                    Span: span),
            "TIMETZ" => throw new ParseException(
                "TIMETZ (TIME WITH TIME ZONE) literals are not yet supported. " +
                "Use TIME 'literal' for time without time zone.",
                prefix.Position),
            _ => throw new InvalidOperationException(
                $"Unhandled typed-temporal prefix '{kind}'. " +
                $"{nameof(IsTypedTemporalPrefixText)} and {nameof(BuildTypedTemporalLiteral)} are out of sync."),
        };
    }

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
    /// A single struct field: <c>name: expr</c>. Field names may collide
    /// with type-keyword tokens (<c>image</c>, <c>audio</c>, <c>video</c>,
    /// …) because ONNX inputs commonly use those exact names; we accept
    /// <see cref="SqlToken.TypeKeyword"/> in name position so struct
    /// literals like <c>{ image: tensor, num_tokens: n }</c> parse.
    /// </summary>
    private static readonly TokenListParser<SqlToken, StructField> StructFieldParser =
        from name in Token.EqualTo(SqlToken.Identifier)
            .Or(Token.EqualTo(SqlToken.TypeKeyword))
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
            .Or(TypedTemporalLiteral.Try())
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
    /// Primary expression with optional postfix suffixes — <c>expr[i]</c>
    /// index access and <c>expr::TypeName</c> PG-style cast. Suffixes are
    /// applied left-to-right and may interleave freely, so <c>a[0]::int</c>
    /// and <c>a::int[][0]</c> both parse as expected. The <c>::</c> form
    /// lowers to the same <see cref="CastExpression"/> AST as
    /// <c>CAST(expr AS type)</c>; it is pure syntactic sugar.
    /// The disambiguation from array literals is natural: an <c>ArrayLiteral</c> is
    /// consumed entirely by <see cref="PrimaryExpression"/>, and the postfix layer
    /// only applies <em>after</em> a primary has already been matched.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> PostfixPrimary =
        from primary in PrimaryExpression
        from suffixes in (
            (from open in Token.EqualTo(SqlToken.LeftBracket)
             from first in SP.Ref(() => ExpressionParser!)
             from rest in (
                 from comma in Token.EqualTo(SqlToken.Comma)
                 from idx in SP.Ref(() => ExpressionParser!)
                 select idx
             ).Many()
             from close in Token.EqualTo(SqlToken.RightBracket)
             select (Func<Expression, Expression>)(e =>
                 new IndexAccessExpression(
                     e,
                     rest.Length == 0
                         ? (IReadOnlyList<Expression>)new[] { first }
                         : new[] { first }.Concat(rest).ToArray(),
                     ToSpan(open))))
            .Try()
            .Or(
                // Postfix `.field` for deep struct-field access. Lowers
                // to <c>IndexAccessExpression(e, ['field'])</c> so the
                // existing struct-by-name accessor path (which handles
                // <c>struct['field']</c>) covers both forms. The 3-part
                // ColumnReference primary parses up to <c>a.b.c</c>
                // greedily, so this postfix only fires for the 4th+ dot
                // (e.g. <c>c.value.bbox.h</c>) — earlier segments stay
                // in the ColumnReference for schema/table/column
                // resolution.
                (from dot in Token.EqualTo(SqlToken.Dot)
                 from fieldName in PostDotColumnNameToken
                 select (Func<Expression, Expression>)(e =>
                     new IndexAccessExpression(
                         e,
                         (IReadOnlyList<Expression>)new[]
                         {
                             (Expression)new LiteralExpression(GetTokenText(fieldName)),
                         },
                         ToSpan(dot))))
                .Try())
            .Or(
                from cc in Token.EqualTo(SqlToken.DoubleColon)
                from targetType in SP.Ref(() => TypeNameParser!)
                select (Func<Expression, Expression>)(e =>
                    new CastExpression(e, targetType, ToSpan(cc))))
        ).Many()
        select suffixes.Aggregate(primary, (expr, build) => build(expr));

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

    /// <summary>
    /// Builder delegate for the additive level — accepts left and right operand
    /// expressions and returns the combined node. Used to mix <c>+</c>/<c>-</c>
    /// (which produce <see cref="BinaryExpression"/>) with <c>||</c> (which
    /// desugars to a <c>concat(...)</c> function call) at the same precedence.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression, Expression>> AdditiveOp =
        Token.EqualTo(SqlToken.Plus).Select(_ =>
            (Func<Expression, Expression, Expression>)((l, r) =>
                new BinaryExpression(l, BinaryOperator.Add, r)))
        .Or(Token.EqualTo(SqlToken.Minus).Select(_ =>
            (Func<Expression, Expression, Expression>)((l, r) =>
                new BinaryExpression(l, BinaryOperator.Subtract, r))))
        // `a || b` is sugar for `concat_strict(a, b)` — the SQL-92 string-
        // concatenation operator with strict null propagation. Same precedence
        // as +/- and left-associative, so `a || b || c` chains as
        // `concat_strict(concat_strict(a,b), c)`. concat_strict (not concat)
        // is the desugar target because the standard `||` returns NULL when
        // any operand is NULL; `concat()` itself is the PostgreSQL-style
        // null-skipping variant and is invoked explicitly by name.
        .Or(Token.EqualTo(SqlToken.DoublePipe).Select(_ =>
            (Func<Expression, Expression, Expression>)((l, r) =>
                new FunctionCallExpression("concat_strict", [l, r]))));

    /// <summary>Addition, subtraction, and string concatenation (||).</summary>
    private static readonly TokenListParser<SqlToken, Expression> Additive =
        SP.Chain(
            AdditiveOp,
            Multiplicative,
            (build, left, right) => build(left, right));

    /// <summary>
    /// The operand precedence tier shared by every comparison-class operator:
    /// the infix comparisons (<c>=</c>, <c>&lt;</c>, …) and the postfix predicates
    /// (<c>LIKE</c>, <c>ILIKE</c>, <c>REGEXP</c>, <c>@@</c>). It is an
    /// <see cref="Additive"/> expression plus an optional trailing
    /// <c>AT TIME ZONE</c> — sitting between Additive and the comparison
    /// predicates so that <c>ts AT TIME ZONE 'X' = ts AT TIME ZONE 'Y'</c> parses
    /// without parens, and so a comparison operand binds tighter than AND/OR.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> ComparisonOperand =
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
    /// Applied to the result of a <see cref="ComparisonOperand"/> expression.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> Comparison =
        from left in ComparisonOperand
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
            .Or(MatchPostfix.Try())
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
    /// <remarks>
    /// The pattern (and ESCAPE) operands parse at <see cref="ComparisonOperand"/> —
    /// the same precedence level <see cref="ComparisonPostfix"/> uses for its
    /// right operand — so LIKE binds tighter than AND/OR. Using the full
    /// <c>ExpressionParser</c> here would let the pattern greedily swallow a
    /// trailing boolean operator, so <c>x LIKE 'a' OR x LIKE 'b'</c> would
    /// misparse as <c>x LIKE ('a' OR x LIKE 'b')</c>.
    /// </remarks>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> LikePostfix =
        from likeKw in Token.EqualTo(SqlToken.Like)
        from pattern in SP.Ref(() => ComparisonOperand!)
        from escape in Token.EqualTo(SqlToken.Escape)
            .IgnoreThen(SP.Ref(() => ComparisonOperand!))
            .OptionalOrDefault()
        select (Func<Expression, Expression>)(expr =>
            escape is not null
                ? new LikeExpression(expr, pattern, escape, CaseInsensitive: false)
                : new BinaryExpression(expr, BinaryOperator.Like, pattern));

    /// <summary>ILIKE pattern postfix (case-insensitive), with optional ESCAPE clause.</summary>
    /// <remarks>Pattern/ESCAPE operands parse at <see cref="ComparisonOperand"/>; see
    /// <see cref="LikePostfix"/> for why the full expression parser is wrong here.</remarks>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> ILikePostfix =
        from ilikeKw in Token.EqualTo(SqlToken.ILike)
        from pattern in SP.Ref(() => ComparisonOperand!)
        from escape in Token.EqualTo(SqlToken.Escape)
            .IgnoreThen(SP.Ref(() => ComparisonOperand!))
            .OptionalOrDefault()
        select (Func<Expression, Expression>)(expr =>
            escape is not null
                ? new LikeExpression(expr, pattern, escape, CaseInsensitive: true)
                : new BinaryExpression(expr, BinaryOperator.ILike, pattern));

    /// <summary>REGEXP pattern postfix (regular expression matching).</summary>
    /// <remarks>Pattern operand parses at <see cref="ComparisonOperand"/>; see
    /// <see cref="LikePostfix"/> for why the full expression parser is wrong here.</remarks>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> RegexpPostfix =
        from regexpKw in Token.EqualTo(SqlToken.Regexp)
        from pattern in SP.Ref(() => ComparisonOperand!)
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
        from right in SP.Ref(() => ComparisonOperand!)
        select (Func<Expression, Expression>)(left =>
            new BinaryExpression(left, op, right));

    /// <summary>
    /// Full-text match postfix: <c>haystack @@ needle</c> desugars to
    /// <c>tsquery_match(haystack, needle)</c>. Sits at the same precedence as
    /// other comparison postfixes — the result is boolean and binds looser
    /// than AT TIME ZONE / arithmetic.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Func<Expression, Expression>> MatchPostfix =
        from atAt in Token.EqualTo(SqlToken.AtAt)
        from right in SP.Ref(() => ComparisonOperand!)
        select (Func<Expression, Expression>)(left =>
            new FunctionCallExpression("tsquery_match", [left, right]));

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
                func.Span,
                func.WithinGroupOrderBy,
                func.SchemaName),

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
                idx.Indices.Select(i => SubstituteBinding(i, bindingName, replacement)).ToArray(),
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

}
