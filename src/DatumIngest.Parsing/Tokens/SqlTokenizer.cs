using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace Heliosoph.DatumV.Parsing.Tokens;

/// <summary>
/// Tokenizes SQL input text into a sequence of <see cref="SqlToken"/> values.
/// Keywords are matched case-insensitively. Multi-character operators are
/// matched before their single-character prefixes.
/// </summary>
public static class SqlTokenizer
{
    /// <summary>
    /// Recognizes a single-quoted SQL string literal with <c>''</c> escape sequences.
    /// </summary>
    private static readonly TextParser<Unit> StringLiteralToken =
        from open in Character.EqualTo('\'')
        from content in Span.EqualTo("''").Value(Unit.Value).Try()
            .Or(Character.Except('\'').Value(Unit.Value))
            .IgnoreMany()
        from close in Character.EqualTo('\'')
        select Unit.Value;

    /// <summary>
    /// Recognizes a double-quoted identifier such as <c>"My Column"</c>,
    /// with <c>""</c> as the escape sequence for an embedded double-quote.
    /// </summary>
    private static readonly TextParser<Unit> DoubleQuotedIdentifierToken =
        from open in Character.EqualTo('"')
        from content in Span.EqualTo("\"\"").Value(Unit.Value).Try()
            .Or(Character.Except('"').Value(Unit.Value))
            .IgnoreMany()
        from close in Character.EqualTo('"')
        select Unit.Value;

    /// <summary>
    /// Recognizes an integer or floating-point number (without leading sign).
    /// The sign is a separate Minus token, handled by the parser.
    /// </summary>
    private static readonly TextParser<Unit> NumberToken =
        from whole in Numerics.Natural
        from fraction in Character.EqualTo('.')
            .IgnoreThen(Numerics.Natural)
            .OptionalOrDefault()
        select Unit.Value;

    /// <summary>
    /// Recognizes a <c>--</c> line comment through to end-of-line or end-of-input.
    /// </summary>
    private static readonly TextParser<Unit> LineCommentToken =
        from start in Span.EqualTo("--")
        from body in Character.Except('\n').IgnoreMany()
        select Unit.Value;

    /// <summary>
    /// Recognizes a backtick-delimited template string literal:
    /// <c>`text ${expr} more text`</c>.
    /// <para>
    /// The tokenizer treats the entire <c>`…`</c> span (including any
    /// <c>${…}</c> splices) as a single token. The parser later walks the
    /// captured text, parses each splice's contents as an expression, and
    /// lowers the whole thing to a <c>concat(…)</c> call.
    /// </para>
    /// <para>
    /// Escapes inside the string body: <c>\`</c> for a literal backtick,
    /// <c>\$</c> for a literal dollar (suppresses splice detection),
    /// <c>\\</c> for a literal backslash. Splice braces are tracked with
    /// nesting, so <c>${ {a: 1}.a }</c> works without prematurely closing.
    /// An unterminated string causes the parser to fail; the tokenizer is
    /// not the place to recover from that.
    /// </para>
    /// </summary>
    private static readonly TextParser<Unit> TemplateStringToken = input =>
    {
        if (input.Length < 1 || input[0] != '`')
        {
            return Result.Empty<Unit>(input, "template string");
        }

        int i = 1;
        while (i < input.Length)
        {
            char c = input[i];

            if (c == '\\' && i + 1 < input.Length)
            {
                // Escape: skip the next character whatever it is.
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int consumed = i + 1;
                return Result.Value(Unit.Value, input, input.Skip(consumed));
            }

            if (c == '$' && i + 1 < input.Length && input[i + 1] == '{')
            {
                // Splice: skip into the body and find the matching close brace,
                // tracking nesting so struct literals inside splices don't end
                // the splice prematurely.
                i += 2;
                int braceDepth = 1;
                while (i < input.Length && braceDepth > 0)
                {
                    char inner = input[i];

                    if (inner == '\\' && i + 1 < input.Length)
                    {
                        i += 2;
                        continue;
                    }

                    // Single-quoted string inside the splice — skip its body
                    // so a stray { or } in a literal doesn't confuse depth.
                    if (inner == '\'')
                    {
                        i++;
                        while (i < input.Length)
                        {
                            if (input[i] == '\'')
                            {
                                if (i + 1 < input.Length && input[i + 1] == '\'')
                                {
                                    i += 2;
                                    continue;
                                }
                                i++;
                                break;
                            }
                            i++;
                        }
                        continue;
                    }

                    if (inner == '{')
                    {
                        braceDepth++;
                    }
                    else if (inner == '}')
                    {
                        braceDepth--;
                    }
                    i++;
                }
                continue;
            }

            i++;
        }

        return Result.Empty<Unit>(input, "unterminated template string");
    };

    /// <summary>
    /// Recognizes a <c>/* ... */</c> block comment. Nesting is not supported.
    /// </summary>
    private static readonly TextParser<Unit> BlockCommentToken = input =>
    {
        if (input.Length < 4 || input[0] != '/' || input[1] != '*')
            return Result.Empty<Unit>(input, "block comment");

        for (int i = 2; i <= input.Length - 2; i++)
        {
            if (input[i] == '*' && input[i + 1] == '/')
            {
                int consumed = i + 2;
                return Result.Value(Unit.Value, input, input.Skip(consumed));
            }
        }

        return Result.Empty<Unit>(input, "unterminated block comment");
    };

    /// <summary>
    /// Recognizes a named parameter placeholder: <c>$</c> followed by a C-style identifier.
    /// The full span (including the <c>$</c> prefix) is captured as a single token.
    /// </summary>
    private static readonly TextParser<Unit> ParameterToken =
        from dollar in Character.EqualTo('$')
        from first in Character.Letter.Or(Character.EqualTo('_'))
        from rest in Character.LetterOrDigit.Or(Character.EqualTo('_')).IgnoreMany()
        select Unit.Value;

    /// <summary>
    /// Recognizes a SQL Server-style temporary-table identifier prefix: <c>#</c> followed
    /// by a C-style identifier. The full span (including <c>#</c>) is emitted as a single
    /// <see cref="SqlToken.Identifier"/> token, preserving the prefix in the table name.
    /// </summary>
    private static readonly TextParser<Unit> TempTableIdentifierToken =
        from hash in Character.EqualTo('#')
        from first in Character.Letter.Or(Character.EqualTo('_'))
        from rest in Character.LetterOrDigit.Or(Character.EqualTo('_')).IgnoreMany()
        select Unit.Value;

    /// <summary>
    /// Recognizes a catalog-model identifier with an optional <c>@&lt;digits&gt;</c>
    /// version-pin suffix: <c>foo</c>, <c>foo_meters</c>, <c>foo@20260529</c>.
    /// The <c>@</c> is allowed only when followed by at least one digit so
    /// stand-alone <c>@</c> remains unrecognised at this layer (preserving
    /// future room for a session-variable form). Emitted as a single
    /// <see cref="SqlToken.Identifier"/> so call sites that consume the
    /// identifier text (function-name resolution, the pre-flight model
    /// reference walk, catalog vocabulary lookups) see one suffixed
    /// string and don't need to round-trip through the parser to
    /// reassemble the pin.
    /// </summary>
    private static readonly TextParser<Unit> IdentifierPinSuffix =
        (from at in Character.EqualTo('@')
         from digit0 in Character.Digit
         from digits in Character.Digit.IgnoreMany()
         select Unit.Value).Try();

    private static readonly TextParser<Unit> IdentifierWithPinToken =
        from first in Character.Letter.Or(Character.EqualTo('_'))
        from rest in Character.LetterOrDigit.Or(Character.EqualTo('_')).IgnoreMany()
        from pin in IdentifierPinSuffix.OptionalOrDefault()
        select Unit.Value;

    /// <summary>The singleton tokenizer instance.</summary>
    public static Tokenizer<SqlToken> Instance { get; } =
        new TokenizerBuilder<SqlToken>()

            // Whitespace — discarded, not emitted as tokens
            .Ignore(Span.WhiteSpace)

            // Comments — discarded, must precede operator matches that share
            // the same prefix characters (- and /)
            .Ignore(LineCommentToken)
            .Ignore(BlockCommentToken)

            // Multi-character symbols must come before their single-char prefixes
            .Match(Span.EqualTo("->"), SqlToken.Arrow)
            .Match(Span.EqualTo("<="), SqlToken.LessOrEqual)
            .Match(Span.EqualTo(">="), SqlToken.GreaterOrEqual)
            .Match(Span.EqualTo("!="), SqlToken.NotEquals)
            .Match(Span.EqualTo("<>"), SqlToken.NotEquals)
            .Match(Span.EqualTo("||"), SqlToken.DoublePipe)
            .Match(Span.EqualTo("@@"), SqlToken.AtAt)
            .Match(Span.EqualTo("::"), SqlToken.DoubleColon)
            .Match(Span.EqualTo(":="), SqlToken.ColonEquals)
            .Match(Span.EqualTo("=>"), SqlToken.FatArrow)

            // Single-character symbols
            .Match(Character.EqualTo('*'), SqlToken.Star)
            .Match(Character.EqualTo(','), SqlToken.Comma)
            .Match(Character.EqualTo('.'), SqlToken.Dot)
            .Match(Character.EqualTo('('), SqlToken.LeftParen)
            .Match(Character.EqualTo(')'), SqlToken.RightParen)
            .Match(Character.EqualTo('='), SqlToken.Equals)
            .Match(Character.EqualTo('<'), SqlToken.LessThan)
            .Match(Character.EqualTo('>'), SqlToken.GreaterThan)
            .Match(Character.EqualTo('|'), SqlToken.Pipe)
            .Match(Character.EqualTo('+'), SqlToken.Plus)
            .Match(Character.EqualTo('-'), SqlToken.Minus)
            .Match(Character.EqualTo('/'), SqlToken.Slash)
            .Match(Character.EqualTo('%'), SqlToken.Percent)
            .Match(Character.EqualTo('^'), SqlToken.Caret)
            .Match(Character.EqualTo('['), SqlToken.LeftBracket)
            .Match(Character.EqualTo(']'), SqlToken.RightBracket)
            .Match(Character.EqualTo('{'), SqlToken.LeftBrace)
            .Match(Character.EqualTo('}'), SqlToken.RightBrace)
            .Match(Character.EqualTo(':'), SqlToken.Colon)

            // String literals (before keywords and identifiers)
            .Match(StringLiteralToken, SqlToken.StringLiteral)

            // Backtick-delimited template strings: `text ${expr} more`.
            // The full span (including splices) is captured as one token; the
            // parser splits on ${…} boundaries and lowers to concat(…).
            .Match(TemplateStringToken, SqlToken.TemplateString)

            // Double-quoted identifiers (before keywords)
            .Match(DoubleQuotedIdentifierToken, SqlToken.Identifier)

            // Keywords — case-insensitive, with delimiter checks to prevent
            // partial matches (e.g. INNER does not match as IN + NER).
            // Longer keywords that share a prefix with shorter ones come first
            // for clarity, although requireDelimiters makes this safe either way.
            .Match(Span.EqualToIgnoreCase("SELECT"), SqlToken.Select, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INTO"), SqlToken.Into, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FROM"), SqlToken.From, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INNER"), SqlToken.Inner, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("JOIN"), SqlToken.Join, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LEFT"), SqlToken.Left, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("RIGHT"), SqlToken.Right, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FULL"), SqlToken.Full, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("OUTER"), SqlToken.Outer, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CROSS"), SqlToken.Cross, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LATERAL"), SqlToken.Lateral, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("APPLY"), SqlToken.Apply, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ON"), SqlToken.On, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("WHERE"), SqlToken.Where, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("BETWEEN"), SqlToken.Between, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("AND"), SqlToken.And, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("GROUP"), SqlToken.Group, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("HAVING"), SqlToken.Having, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ORDER"), SqlToken.Order, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("OFFSET"), SqlToken.Offset, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("OR"), SqlToken.Or, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("NOT"), SqlToken.Not, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("NULL"), SqlToken.Null, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IN"), SqlToken.In, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ILIKE"), SqlToken.ILike, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LIKE"), SqlToken.Like, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("REGEXP"), SqlToken.Regexp, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ESCAPE"), SqlToken.Escape, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IS"), SqlToken.Is, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("AS"), SqlToken.As, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("SHARD"), SqlToken.Shard, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("BY"), SqlToken.By, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ASC"), SqlToken.Asc, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DESC"), SqlToken.Desc, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LIMIT"), SqlToken.Limit, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CAST"), SqlToken.Cast, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("EXTRACT"), SqlToken.Extract, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TRUE"), SqlToken.True, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FALSE"), SqlToken.False, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CASE"), SqlToken.Case, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("WHEN"), SqlToken.When, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("THEN"), SqlToken.Then, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ELSE"), SqlToken.Else, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("END"), SqlToken.End, requireDelimiters: true)

            // Window function keywords
            .Match(Span.EqualToIgnoreCase("OVER"), SqlToken.Over, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("PARTITION"), SqlToken.Partition, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("WITHIN"), SqlToken.Within, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ROWS"), SqlToken.Rows, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UNBOUNDED"), SqlToken.Unbounded, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("PRECEDING"), SqlToken.Preceding, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FOLLOWING"), SqlToken.Following, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CURRENT_TIMESTAMP"), SqlToken.CurrentTimestamp, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CURRENT_DATE"), SqlToken.CurrentDate, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CURRENT_TIME"), SqlToken.CurrentTime, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CURRENT"), SqlToken.Current, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LOCALTIMESTAMP"), SqlToken.LocalTimestamp, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LOCALTIME"), SqlToken.LocalTime, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DISTINCT"), SqlToken.Distinct, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IGNORE"), SqlToken.Ignore, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("RESPECT"), SqlToken.Respect, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("NULLS"), SqlToken.Nulls, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("EXISTS"), SqlToken.Exists, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("QUALIFY"), SqlToken.Qualify, requireDelimiters: true)

            // CTE keywords
            .Match(Span.EqualToIgnoreCase("WITH"), SqlToken.With, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("RECURSIVE"), SqlToken.Recursive, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("MATERIALIZED"), SqlToken.Materialized, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UNION"), SqlToken.Union, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INTERSECT"), SqlToken.Intersect, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("EXCEPT"), SqlToken.Except, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("REPLACE"), SqlToken.Replace, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ALL"), SqlToken.All, requireDelimiters: true)

            // LET / SCAN binding keywords
            .Match(Span.EqualToIgnoreCase("LET"), SqlToken.Let, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("SCAN"), SqlToken.Scan, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INIT"), SqlToken.Init, requireDelimiters: true)

            // AT TIME ZONE keywords
            .Match(Span.EqualToIgnoreCase("ZONE"), SqlToken.Zone, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TIME"), SqlToken.Time, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("AT"), SqlToken.At, requireDelimiters: true)

            // ASSERT / MESSAGE / DEFINE keywords
            .Match(Span.EqualToIgnoreCase("ASSERT"), SqlToken.Assert, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("MESSAGE"), SqlToken.Message, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DEFINE"), SqlToken.Define, requireDelimiters: true)

            // PIVOT / UNPIVOT keywords
            .Match(Span.EqualToIgnoreCase("PIVOT"), SqlToken.Pivot, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UNPIVOT"), SqlToken.Unpivot, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FOR"), SqlToken.For, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INCLUDE"), SqlToken.Include, requireDelimiters: true)

            // TABLESAMPLE keywords
            .Match(Span.EqualToIgnoreCase("TABLESAMPLE"), SqlToken.Tablesample, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("REPEATABLE"), SqlToken.Repeatable, requireDelimiters: true)

            // DDL / DML keywords
            .Match(Span.EqualToIgnoreCase("CREATE"), SqlToken.Create, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TABLE"), SqlToken.Table, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TEMPORARY"), SqlToken.Temporary, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TEMP"), SqlToken.Temp, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DROP"), SqlToken.Drop, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INSERT"), SqlToken.Insert, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("VALUES"), SqlToken.Values, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("RETURNING"), SqlToken.Returning, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UPDATE"), SqlToken.Update, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("SET"), SqlToken.Set, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DELETE"), SqlToken.Delete, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ANALYZE"), SqlToken.Analyze, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("REINDEX"), SqlToken.Reindex, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INDEX"), SqlToken.Index, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UNIQUE"), SqlToken.Unique, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ALTER"), SqlToken.Alter, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ADD"), SqlToken.Add, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("COLUMN"), SqlToken.Column, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CONSTRAINT"), SqlToken.Constraint, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DEFAULT"), SqlToken.Default, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CHECK"), SqlToken.Check, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("STEP"), SqlToken.Step, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UNIT"), SqlToken.Unit, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("COMMENT"), SqlToken.Comment, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("PRIMARY"), SqlToken.Primary, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("KEY"), SqlToken.Key, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IDENTITY"), SqlToken.Identity, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("GENERATED"), SqlToken.Generated, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ALWAYS"), SqlToken.Always, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IF"), SqlToken.If, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FUNCTION"), SqlToken.Function, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("PROCEDURE"), SqlToken.Procedure, requireDelimiters: true)
            // MODEL is intentionally NOT a hard keyword — it's recognized
            // contextually inside CreateModelPrefix / DropModelParser via the
            // same Identifier+Where pattern used for USING. Keeping `model`
            // as a usable identifier matters: existing tests and user
            // schemas freely use it as a column or table name (e.g.
            // `CREATE TABLE model (...)`, `JOIN model AS m`). A hard
            // keyword here was a regression — see the failing
            // UpdateValidationTests / DdlParsingTests that surfaced it.
            .Match(Span.EqualToIgnoreCase("RETURNS"), SqlToken.Returns, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("RETURN"), SqlToken.Return, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("PURE"), SqlToken.Pure, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CALL"), SqlToken.Call, requireDelimiters: true)

            // Procedural keywords (BEGIN/END/IF/ELSE/WHILE/FOR/DECLARE/SET/TO/BREAK/CONTINUE).
            // BEGIN, WHILE, DECLARE, TO, BREAK, CONTINUE are added here; END/IF/ELSE/FOR/SET
            // are already declared above for other uses (CASE, IF NOT EXISTS, FOR/SET in
            // PIVOT/UPDATE) but reuse the same tokens for procedural code.
            .Match(Span.EqualToIgnoreCase("BEGIN"), SqlToken.Begin, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("WHILE"), SqlToken.While, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DECLARE"), SqlToken.Declare, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TO"), SqlToken.To, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("BREAK"), SqlToken.Break, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CONTINUE"), SqlToken.Continue, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("PRINT"), SqlToken.Print, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TRY"), SqlToken.Try, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CATCH"), SqlToken.Catch, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FINALLY"), SqlToken.Finally, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("RAISE"), SqlToken.Raise, requireDelimiters: true)

            // Type keywords — reserved names for DataKind type literals
            .Match(Span.EqualToIgnoreCase("BOOLEAN"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UINT8"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UINT16"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UINT32"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UINT64"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UINT128"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INT8"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INT16"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INT32"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INT64"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("INT128"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FLOAT16"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FLOAT32"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FLOAT64"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DECIMAL"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("STRING"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DATE"), SqlToken.TypeKeyword, requireDelimiters: true)
            // PG-canonical: TIMESTAMP (no tz), TIMESTAMPTZ (with tz).
            // Multi-word forms `TIMESTAMP WITH TIME ZONE` / `TIMESTAMP WITHOUT
            // TIME ZONE` are recognised at the parser layer (see TypeAnnotation
            // combinator) by composing this TIMESTAMP token with the WITH / WITHOUT
            // / TIME / ZONE word tokens that surround it.
            .Match(Span.EqualToIgnoreCase("TIMESTAMPTZ"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TIMESTAMP"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DURATION"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("UUID"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IMAGE"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("AUDIO"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("VIDEO"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("JSON"), SqlToken.TypeKeyword, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("STRUCT"), SqlToken.TypeKeyword, requireDelimiters: true)

            // Semicolon statement terminator
            .Match(Character.EqualTo(';'), SqlToken.Semicolon)

            // Named parameter placeholders ($name) — before numeric literals
            // and identifiers so the $ prefix is not treated as unexpected input.
            .Match(ParameterToken, SqlToken.Parameter)

            // Temp-table identifier prefix (#name) — before generic identifiers
            // so the # is captured as part of the name rather than rejected.
            .Match(TempTableIdentifierToken, SqlToken.Identifier)

            // Numeric literals
            .Match(NumberToken, SqlToken.NumberLiteral, requireDelimiters: true)

            // Generic identifiers — last, so keywords take priority. The
            // pin-suffix variant accepts an optional `@<digits>` tail so a
            // catalog-model version pin (`foo@20260529`) tokenises as one
            // identifier rather than splitting at the `@`.
            .Match(IdentifierWithPinToken, SqlToken.Identifier, requireDelimiters: true)

            .Build();
}
