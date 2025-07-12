using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace DatumQuery.Parsing.Tokens;

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
    /// Recognizes a bracket-quoted identifier such as <c>[My Column]</c>.
    /// </summary>
    private static readonly TextParser<Unit> BracketQuotedIdentifierToken =
        from open in Character.EqualTo('[')
        from content in Character.Except(']').AtLeastOnce()
        from close in Character.EqualTo(']')
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

    /// <summary>The singleton tokenizer instance.</summary>
    public static Tokenizer<SqlToken> Instance { get; } =
        new TokenizerBuilder<SqlToken>()

            // Whitespace — discarded, not emitted as tokens
            .Ignore(Span.WhiteSpace)

            // Multi-character symbols must come before their single-char prefixes
            .Match(Span.EqualTo("<="), SqlToken.LessOrEqual)
            .Match(Span.EqualTo(">="), SqlToken.GreaterOrEqual)
            .Match(Span.EqualTo("!="), SqlToken.NotEquals)
            .Match(Span.EqualTo("<>"), SqlToken.NotEquals)

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

            // String literals (before keywords and identifiers)
            .Match(StringLiteralToken, SqlToken.StringLiteral)

            // Bracket-quoted identifiers (before keywords)
            .Match(BracketQuotedIdentifierToken, SqlToken.Identifier)

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
            .Match(Span.EqualToIgnoreCase("ON"), SqlToken.On, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("WHERE"), SqlToken.Where, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("BETWEEN"), SqlToken.Between, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("AND"), SqlToken.And, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ORDER"), SqlToken.Order, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("OFFSET"), SqlToken.Offset, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("OR"), SqlToken.Or, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("NOT"), SqlToken.Not, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("NULL"), SqlToken.Null, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IN"), SqlToken.In, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LIKE"), SqlToken.Like, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("IS"), SqlToken.Is, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("AS"), SqlToken.As, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("SHARD"), SqlToken.Shard, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("BY"), SqlToken.By, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("ASC"), SqlToken.Asc, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("DESC"), SqlToken.Desc, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("LIMIT"), SqlToken.Limit, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("CAST"), SqlToken.Cast, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("TRUE"), SqlToken.True, requireDelimiters: true)
            .Match(Span.EqualToIgnoreCase("FALSE"), SqlToken.False, requireDelimiters: true)

            // Numeric literals
            .Match(NumberToken, SqlToken.NumberLiteral, requireDelimiters: true)

            // Generic identifiers — last, so keywords take priority
            .Match(Identifier.CStyle, SqlToken.Identifier, requireDelimiters: true)

            .Build();
}
