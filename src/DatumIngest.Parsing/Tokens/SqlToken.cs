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

    /// <summary>The LATERAL keyword (lateral join modifier).</summary>
    Lateral,

    /// <summary>The APPLY keyword (T-SQL style lateral join).</summary>
    Apply,

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

    /// <summary>The LIKE keyword (case-sensitive pattern matching).</summary>
    Like,

    /// <summary>The ILIKE keyword (case-insensitive pattern matching).</summary>
    ILike,

    /// <summary>The REGEXP keyword (regular expression matching).</summary>
    Regexp,

    /// <summary>The ESCAPE keyword (escape character for LIKE/ILIKE patterns).</summary>
    Escape,

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

    /// <summary>The QUALIFY keyword (post-window-function filter).</summary>
    Qualify,

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

    /// <summary>The WITHIN keyword (ordered-set aggregate syntax: WITHIN GROUP).</summary>
    Within,

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

    /// <summary>The IGNORE keyword (null handling modifier for value window functions).</summary>
    Ignore,

    /// <summary>The RESPECT keyword (null handling modifier for value window functions).</summary>
    Respect,

    /// <summary>The NULLS keyword (used with IGNORE/RESPECT for null handling).</summary>
    Nulls,

    /// <summary>The WITH keyword (common table expression preamble).</summary>
    With,

    /// <summary>The RECURSIVE keyword (recursive CTE modifier).</summary>
    Recursive,

    /// <summary>The MATERIALIZED keyword (CTE materialization hint).</summary>
    Materialized,

    /// <summary>The UNION keyword (set operation).</summary>
    Union,

    /// <summary>The ALL keyword (used with UNION ALL, INTERSECT ALL, EXCEPT ALL).</summary>
    All,

    /// <summary>The INTERSECT keyword (set operation).</summary>
    Intersect,

    /// <summary>The EXCEPT keyword (set operation).</summary>
    Except,

    /// <summary>The REPLACE keyword (wildcard column replacement).</summary>
    Replace,

    /// <summary>The LET keyword (named memoized binding in SELECT).</summary>
    Let,

    /// <summary>The PIVOT keyword (row-to-column reshaping).</summary>
    Pivot,

    /// <summary>The UNPIVOT keyword (column-to-row reshaping).</summary>
    Unpivot,

    /// <summary>The FOR keyword (used in PIVOT/UNPIVOT to identify the pivot column).</summary>
    For,

    /// <summary>The INCLUDE keyword (used with UNPIVOT INCLUDE NULLS).</summary>
    Include,

    /// <summary>The TABLESAMPLE keyword (row sampling).</summary>
    Tablesample,

    /// <summary>The REPEATABLE keyword (deterministic sampling seed).</summary>
    Repeatable,

    // ───────────────────── DDL / DML Keywords ─────────────────────

    /// <summary>The CREATE keyword (DDL table creation).</summary>
    Create,

    /// <summary>The TABLE keyword (DDL table reference).</summary>
    Table,

    /// <summary>The TEMP keyword (temporary table modifier).</summary>
    Temp,

    /// <summary>The TEMPORARY keyword (temporary table modifier, synonym for TEMP).</summary>
    Temporary,

    /// <summary>The DROP keyword (DDL table removal).</summary>
    Drop,

    /// <summary>The INSERT keyword (DML row insertion).</summary>
    Insert,

    /// <summary>The VALUES keyword (DML literal row values).</summary>
    Values,

    /// <summary>The UPDATE keyword (DML row mutation).</summary>
    Update,

    /// <summary>The SET keyword (UPDATE assignment list).</summary>
    Set,

    /// <summary>The DELETE keyword (DML row deletion).</summary>
    Delete,

    /// <summary>The ANALYZE keyword (statistics and index rebuild).</summary>
    Analyze,

    /// <summary>The ALTER keyword (DDL table modification).</summary>
    Alter,

    /// <summary>The ADD keyword (ALTER TABLE column addition).</summary>
    Add,

    /// <summary>The COLUMN keyword (ALTER TABLE column specifier).</summary>
    Column,

    /// <summary>The DEFAULT keyword (column default value).</summary>
    Default,

    /// <summary>The PRIMARY keyword (primary key constraint).</summary>
    Primary,

    /// <summary>The KEY keyword (primary key constraint).</summary>
    Key,

    /// <summary>The IF keyword (conditional DDL guard).</summary>
    If,

    // ───────────────────── Identifiers & Literals ─────────────────────

    /// <summary>An unquoted or double-quoted identifier (table or column name).</summary>
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

    /// <summary>The ; (semicolon) statement terminator.</summary>
    Semicolon,

    /// <summary>The -&gt; (arrow) operator for lambda expressions.</summary>
    Arrow,

    /// <summary>The [ (left bracket) delimiter for array literals.</summary>
    LeftBracket,

    /// <summary>The ] (right bracket) delimiter for array literals and index access.</summary>
    RightBracket,

    /// <summary>The { (left brace) delimiter for struct literals.</summary>
    LeftBrace,

    /// <summary>The } (right brace) delimiter for struct literals.</summary>
    RightBrace,

    /// <summary>The : (colon) separator for struct field key–value pairs.</summary>
    Colon,

    /// <summary>The <c>ASSERT</c> keyword for row-level invariant checks.</summary>
    Assert,

    /// <summary>The <c>MESSAGE</c> keyword for providing a custom assertion failure message.</summary>
    Message,

    /// <summary>The <c>DEFINE</c> keyword for grouping LET bindings and ASSERT clauses into a block.</summary>
    Define,

    /// <summary>The <c>SCAN</c> keyword for fold/prefix-scan expressions over ordered partitions.</summary>
    Scan,

    /// <summary>The <c>INIT</c> keyword specifying the initial accumulator value for a SCAN expression.</summary>
    Init,

    /// <summary>The <c>AT</c> keyword (used in AT TIME ZONE expressions).</summary>
    At,

    /// <summary>The <c>TIME</c> keyword (used in AT TIME ZONE expressions).</summary>
    Time,

    /// <summary>The <c>ZONE</c> keyword (used in AT TIME ZONE expressions).</summary>
    Zone,
}
