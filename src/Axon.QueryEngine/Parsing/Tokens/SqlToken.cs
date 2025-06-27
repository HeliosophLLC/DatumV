namespace Axon.QueryEngine.Parsing.Tokens;

/// <summary>
/// Every distinct token type recognized by the SQL tokenizer.
/// Keywords are case-insensitive at the tokenizer level.
/// </summary>
public enum SqlToken
{
    // ───────────────────── Keywords ─────────────────────

    /// <summary>The SELECT keyword.</summary>
    Select,

    /// <summary>The INTO keyword.</summary>
    Into,

    /// <summary>The FROM keyword.</summary>
    From,

    /// <summary>The JOIN keyword.</summary>
    Join,

    /// <summary>The LEFT keyword (join modifier).</summary>
    Left,

    /// <summary>The RIGHT keyword (join modifier).</summary>
    Right,

    /// <summary>The FULL keyword (join modifier).</summary>
    Full,

    /// <summary>The OUTER keyword (join modifier).</summary>
    Outer,

    /// <summary>The CROSS keyword (join modifier).</summary>
    Cross,

    /// <summary>The INNER keyword (join modifier).</summary>
    Inner,

    /// <summary>The ON keyword (join condition).</summary>
    On,

    /// <summary>The WHERE keyword.</summary>
    Where,

    /// <summary>The AND keyword (logical operator).</summary>
    And,

    /// <summary>The OR keyword (logical operator).</summary>
    Or,

    /// <summary>The NOT keyword (logical negation).</summary>
    Not,

    /// <summary>The IN keyword (set membership).</summary>
    In,

    /// <summary>The BETWEEN keyword (range predicate).</summary>
    Between,

    /// <summary>The LIKE keyword (pattern matching).</summary>
    Like,

    /// <summary>The IS keyword (null testing).</summary>
    Is,

    /// <summary>The NULL keyword (null literal).</summary>
    Null,

    /// <summary>The AS keyword (alias).</summary>
    As,

    /// <summary>The SHARD keyword (sharding clause).</summary>
    Shard,

    /// <summary>The ORDER keyword (ordering clause).</summary>
    Order,

    /// <summary>The BY keyword (used with ORDER and SHARD).</summary>
    By,

    /// <summary>The ASC keyword (ascending sort direction).</summary>
    Asc,

    /// <summary>The DESC keyword (descending sort direction).</summary>
    Desc,

    /// <summary>The LIMIT keyword.</summary>
    Limit,

    /// <summary>The OFFSET keyword.</summary>
    Offset,

    /// <summary>The CAST keyword (explicit type conversion).</summary>
    Cast,

    /// <summary>The TRUE keyword (boolean literal).</summary>
    True,

    /// <summary>The FALSE keyword (boolean literal).</summary>
    False,

    // ───────────────────── Identifiers & Literals ─────────────────────

    /// <summary>An unquoted or bracket-quoted identifier (table or column name).</summary>
    Identifier,

    /// <summary>A single-quoted string literal.</summary>
    StringLiteral,

    /// <summary>A numeric literal (integer or floating-point).</summary>
    NumberLiteral,

    // ───────────────────── Symbols ─────────────────────

    /// <summary>The * (star / wildcard) symbol.</summary>
    Star,

    /// <summary>The , (comma) symbol.</summary>
    Comma,

    /// <summary>The . (dot) symbol for qualified names.</summary>
    Dot,

    /// <summary>The ( (left parenthesis) symbol.</summary>
    LeftParen,

    /// <summary>The ) (right parenthesis) symbol.</summary>
    RightParen,

    /// <summary>The = (equals) comparison operator.</summary>
    Equals,

    /// <summary>The != or &lt;&gt; (not equals) comparison operator.</summary>
    NotEquals,

    /// <summary>The &lt; (less than) comparison operator.</summary>
    LessThan,

    /// <summary>The &gt; (greater than) comparison operator.</summary>
    GreaterThan,

    /// <summary>The &lt;= (less than or equal) comparison operator.</summary>
    LessOrEqual,

    /// <summary>The &gt;= (greater than or equal) comparison operator.</summary>
    GreaterOrEqual,

    /// <summary>The | (pipe) symbol used in certain expressions.</summary>
    Pipe,

    /// <summary>The + (plus) arithmetic operator.</summary>
    Plus,

    /// <summary>The - (minus / unary negation) arithmetic operator.</summary>
    Minus,

    /// <summary>The / (slash / division) arithmetic operator.</summary>
    Slash,
}
