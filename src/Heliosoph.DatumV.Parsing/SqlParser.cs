using System.Linq;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Parsing.Tokens;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using SP = Superpower.Parse;

#pragma warning disable CS8603, CS8604, CS8620 // Superpower combinators lack consistent nullable reference type annotations

namespace Heliosoph.DatumV.Parsing;

/// <summary>
/// Parses tokenized SQL into an AST rooted at <see cref="QueryExpression"/>.
/// Uses Superpower's <see cref="TokenListParser{TKind,T}"/> combinators to implement
/// a recursive-descent parser with proper operator precedence.
/// </summary>
public static partial class SqlParser
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
        Expression? limit,
        Expression? offset,
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
        Token.EqualTo(SqlToken.Identifier)
            .Or(Token.EqualTo(SqlToken.TypeKeyword))
            // Parameter-clause keywords (STEP/UNIT/COMMENT/CHECK) — reserved
            // only on UDF/model parameter declarations but otherwise usable as
            // bare names (column aliases, SCAN accumulators, etc.).
            .Or(Token.EqualTo(SqlToken.Step))
            .Or(Token.EqualTo(SqlToken.Unit))
            .Or(Token.EqualTo(SqlToken.Comment))
            .Or(Token.EqualTo(SqlToken.Check));

    /// <summary>
    /// Identifier-or-soft-keyword token in column-reference position. Includes
    /// the parameter-clause soft keywords (STEP/UNIT/COMMENT/CHECK) but
    /// deliberately excludes <see cref="SqlToken.TypeKeyword"/> so that bare
    /// type names (<c>INTEGER</c>, <c>TEXT</c>, …) keep flowing to
    /// <c>TypeLiteral</c> rather than being absorbed as column references.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Token<SqlToken>> ColumnNameToken =
        Token.EqualTo(SqlToken.Identifier)
            .Or(Token.EqualTo(SqlToken.Step))
            .Or(Token.EqualTo(SqlToken.Unit))
            .Or(Token.EqualTo(SqlToken.Comment))
            .Or(Token.EqualTo(SqlToken.Check));

    /// <summary>
    /// Column-name token in post-dot position (after a table qualifier).
    /// Accepts <see cref="ColumnNameToken"/> plus <see cref="SqlToken.TypeKeyword"/>.
    /// Unlike the leading position, there's no ambiguity post-dot: <c>t.Int32</c>
    /// can never be a type literal, so a column literally named <c>video</c> /
    /// <c>image</c> / <c>int32</c> / etc. is unambiguously a column reference.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Token<SqlToken>> PostDotColumnNameToken =
        ColumnNameToken.Or(Token.EqualTo(SqlToken.TypeKeyword));

    // ───────────────────── Public API ─────────────────────

    /// <summary>
    /// Tokenizes <paramref name="sql"/> through Superpower and wraps any
    /// tokenizer-level failure (incomplete quoted identifier, illegal lexeme
    /// like a stray <c>@</c>, etc.) in the Heliosoph.DatumV-flavoured
    /// <see cref="ParseException"/> so the public entry points throw a single
    /// consistent type regardless of which Superpower layer rejected the input.
    /// </summary>
    private static TokenList<SqlToken> TokenizeOrWrap(string sql)
    {
        try
        {
            return SqlTokenizer.Instance.Tokenize(sql);
        }
        catch (Superpower.ParseException ex)
        {
            throw new ParseException(ex.Message, ex.ErrorPosition);
        }
    }

    /// <summary>
    /// Parses a SQL string into a <see cref="QueryExpression"/> AST.
    /// </summary>
    /// <param name="sql">The SQL query text.</param>
    /// <returns>The parsed AST.</returns>
    /// <exception cref="ParseException">Thrown when the input cannot be parsed.</exception>
    public static QueryExpression Parse(string sql)
    {
        TokenList<SqlToken> tokens = TokenizeOrWrap(sql);
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
        TokenList<SqlToken> tokens = TokenizeOrWrap(sql);
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
        TokenList<SqlToken> tokens = TokenizeOrWrap(sql);
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
    /// Parses a SQL string containing one or more semicolon-separated statements,
    /// returning each statement alongside the verbatim source slice it was parsed
    /// from. Source slices are needed by callers that persist the original SQL
    /// (notably <c>CREATE FUNCTION ... BEGIN…END</c> and <c>CREATE PROCEDURE</c>,
    /// whose bodies don't round-trip through any AST formatter) — pass the slice
    /// to the catalog's <c>Plan(Statement, string?)</c> overload so the catalog
    /// file captures the body verbatim.
    /// </summary>
    /// <remarks>
    /// Slices include any leading whitespace and comments captured by the
    /// tokenizer's skip rules, but exclude the trailing semicolon that
    /// separated this statement from the next.
    /// </remarks>
    /// <param name="sql">The SQL text containing one or more statements.</param>
    /// <returns>The list of parsed statements paired with their source slices, in order.</returns>
    /// <exception cref="ParseException">Thrown when the input cannot be parsed.</exception>
    public static IReadOnlyList<(Statement Statement, string SourceText)> ParseBatchWithText(string sql)
    {
        TokenList<SqlToken> tokens = TokenizeOrWrap(sql);
        List<(Statement, string)> result = new();
        TokenListParser<SqlToken, Token<SqlToken>[]> semis =
            Token.EqualTo(SqlToken.Semicolon).Many();

        // Skip leading semicolons (mirrors BatchParser's tolerance of empty statements).
        TokenListParserResult<SqlToken, Token<SqlToken>[]> leadingSemis = semis.TryParse(tokens);
        if (leadingSemis.HasValue) tokens = leadingSemis.Remainder;

        while (!tokens.IsAtEnd)
        {
            // Capture the absolute character position of the first token in
            // the statement. TokenList<T>.Position is a token-stream index,
            // not a source offset — the source offset lives on each token's
            // Span.Position.Absolute.
            int startAbs = tokens.ConsumeToken().Value.Span.Position.Absolute;

            TokenListParserResult<SqlToken, Statement> stmtResult = SingleStatementParser.TryParse(tokens);
            if (!stmtResult.HasValue)
            {
                throw new ParseException(stmtResult.ToString(), stmtResult.ErrorPosition);
            }

            int endAbs = stmtResult.Remainder.IsAtEnd
                ? sql.Length
                : stmtResult.Remainder.ConsumeToken().Value.Span.Position.Absolute;

            string slice = sql.Substring(startAbs, endAbs - startAbs).TrimEnd();
            result.Add((stmtResult.Value, slice));
            tokens = stmtResult.Remainder;

            // Skip the semicolon(s) separating this statement from the next.
            TokenListParserResult<SqlToken, Token<SqlToken>[]> trailingSemis =
                semis.TryParse(tokens);
            if (trailingSemis.HasValue) tokens = trailingSemis.Remainder;
        }

        return result;
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
        // splice parsing) raise Heliosoph.DatumV ParseException directly rather than
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

        // The batch parser failed. Two diagnostic paths from here:
        //
        // (1) SELECT/WITH inputs — fall through to ParseWithRecovery, the
        //     clause-by-clause walker that can surface multiple errors
        //     per query (missing FROM, malformed WHERE, etc.).
        //
        // (2) Anything else (CREATE, INSERT, UPDATE, DELETE, ALTER, ANALYZE,
        //     REINDEX, DROP, CALL, DECLARE, BEGIN, IF, WHILE, FOR, …) —
        //     surface batchResult's error directly. SingleStatementParser
        //     commits to the matching CREATE-* / DML / procedural branch on
        //     a unique prefix and propagates committed failures with the
        //     deepest Remainder.Position, so batchResult.ErrorPosition
        //     already points at the real problem. Routing these through
        //     ParseWithRecovery instead produces a misleading "Expected
        //     SELECT keyword" diagnostic at column 1, which used to make
        //     malformed CREATE MODEL / RETURNS Struct<…> bodies hostile
        //     to debug from the LanguageServer's squiggle alone.
        if (!IsSelectLikeStart(tokens))
        {
            return new ParseResult(
                query: null,
                [new ParseError
                {
                    Message = batchResult.ToString(),
                    Line = batchResult.ErrorPosition.Line,
                    Column = batchResult.ErrorPosition.Column,
                }]);
        }

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
    /// True when the token stream's first significant token is one the
    /// clause-by-clause SELECT recovery walker can usefully process —
    /// a bare <c>SELECT</c>, a <c>WITH</c>-CTE that wraps a SELECT, or
    /// a parenthesized subquery. Returns <see langword="false"/> for
    /// statement-starters like <c>CREATE</c>, <c>INSERT</c>, etc., for
    /// which the batch parser's deep-position error is the right
    /// diagnostic to surface.
    /// </summary>
    private static bool IsSelectLikeStart(TokenList<SqlToken> tokens)
    {
        if (tokens.IsAtEnd) return false;
        SqlToken kind = tokens.ConsumeToken().Value.Kind;
        return kind is SqlToken.Select or SqlToken.With or SqlToken.LeftParen;
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
