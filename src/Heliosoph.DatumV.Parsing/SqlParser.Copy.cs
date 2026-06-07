using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Parsing.Tokens;
using Superpower;
using Superpower.Parsers;
using SP = Superpower.Parse;

#pragma warning disable CS8603, CS8604, CS8620 // Superpower combinators lack consistent nullable reference type annotations

namespace Heliosoph.DatumV.Parsing;

public static partial class SqlParser
{
    // ───────────────────── COPY statement ─────────────────────
    //
    // COPY (query) TO 'path' [WITH] (option, ...)
    //
    // <option> ::= <ident> <value>
    // <value>  ::= <string-literal> | <number-literal> | <identifier>
    //
    // FORMAT is the only key special-cased by the planner; everything else is
    // interpreted per-format. Identifier values fold to string literals so
    // option blocks read naturally (`FORMAT parquet, COMPRESSION zstd`) without
    // forcing quotes on bare names.

    // Static-init note: references to partial-class fields from other source
    // files (IdentifierLike / QueryExpressionParser) are wrapped in SP.Ref so
    // they resolve at parse time, not at field-init time. Otherwise the
    // Superpower combinator captures whichever sibling-file field happens to
    // still be null when this file's initializers run.
    private static readonly TokenListParser<SqlToken, Expression> CopyOptionValueParser =
        Token.EqualTo(SqlToken.StringLiteral)
            .Select(token => (Expression)new LiteralExpression(UnquoteString(token)))
            .Or(Token.EqualTo(SqlToken.NumberLiteral)
                .Select(token =>
                {
                    string raw = token.ToStringValue();
                    return (Expression)new LiteralExpression(
                        long.TryParse(raw, out long asLong) ? (object)asLong : double.Parse(raw));
                }))
            .Or(Token.EqualTo(SqlToken.Identifier)
                .Select(token => (Expression)new LiteralExpression(GetTokenText(token))));

    private static readonly TokenListParser<SqlToken, CopyOption> CopyOptionParser =
        from key in Token.EqualTo(SqlToken.Identifier)
        from value in CopyOptionValueParser
        select new CopyOption(GetTokenText(key), value);

    // When the option block is present it must contain at least one option —
    // an empty `()` says nothing and is rejected with a parse error so users
    // get one canonical form for "no options" (omit the block entirely).
    private static readonly TokenListParser<SqlToken, IReadOnlyList<CopyOption>> CopyOptionListParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from first in CopyOptionParser
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from opt in CopyOptionParser
            select opt
        ).Many()
        from close in Token.EqualTo(SqlToken.RightParen)
        select (IReadOnlyList<CopyOption>)(new[] { first }.Concat(rest).ToArray());

    /// <summary>
    /// <c>COPY (query) TO 'path' [ [WITH] (FORMAT x, opt v, ...) ]</c>.
    /// The trailing option block is optional — when absent the planner infers
    /// the format from the target path's extension, matching DuckDB and
    /// Postgres which both accept a bare <c>COPY (q) TO 'p'</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CopyStatementParser =
        from copyKw in Token.EqualTo(SqlToken.Copy)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from source in SP.Ref(() => QueryExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        from toKw in Token.EqualTo(SqlToken.To)
        from path in Token.EqualTo(SqlToken.StringLiteral)
        from withKw in Token.EqualTo(SqlToken.With).OptionalOrDefault()
        from options in CopyOptionListParser.OptionalOrDefault(Array.Empty<CopyOption>())
        select (Statement)new CopyStatement(source, UnquoteString(path), options);
}
