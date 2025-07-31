namespace DatumIngest.Parsing.Tokens;

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

    /// <summary>The GROUP keyword (grouping clause).</summary>
    Group,

    /// <summary>The HAVING keyword (post-aggregation filter).</summary>
    Having,

    /// <summary>The ORDER keyword (ordering clause).</summary>
    Order,

    /// <summary>The BY keyword (used with ORDER, GROUP, and SHARD).</summary>
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

    /// <summary>The CASE keyword (conditional expression).</summary>
    Case,

    /// <summary>The WHEN keyword (conditional branch).</summary>
    When,

    /// <summary>The THEN keyword (conditional result).</summary>
    Then,

    /// <summary>The ELSE keyword (conditional default).</summary>
    Else,

    /// <summary>The END keyword (conditional terminator).</summary>
    End,

    /// <summary>The OVER keyword (window specification).</summary>
    Over,

    /// <summary>The PARTITION keyword (window partitioning).</summary>
    Partition,

    /// <summary>The ROWS keyword (window frame type).</summary>
    Rows,

    /// <summary>The RANGE keyword (window frame type, reserved for future use).</summary>
    Range,

    /// <summary>The UNBOUNDED keyword (window frame bound).</summary>
    Unbounded,

    /// <summary>The PRECEDING keyword (window frame direction).</summary>
    Preceding,

    /// <summary>The FOLLOWING keyword (window frame direction).</summary>
    Following,

    /// <summary>The CURRENT keyword (used with ROW for window frame bound).</summary>
    Current,

    /// <summary>The EXISTS keyword (existence predicate).</summary>
    Exists,

    /// <summary>The DISTINCT keyword (duplicate elimination).</summary>
    Distinct,

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

    /// <summary>The % (percent / modulo) arithmetic operator.</summary>
    Percent,

    /// <summary>The ^ (caret / power) arithmetic operator.</summary>
    Caret,

    /// <summary>A named parameter placeholder such as <c>$threshold</c>.</summary>
    Parameter,
}
