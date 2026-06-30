namespace Heliosoph.DatumV.Manifest;

/// <summary>
/// Canonical lists of SQL keywords, type names, date-part names, and built-in
/// function names recognized by the Heliosoph.DatumV dialect. Used as a single source
/// of truth for Monaco's <c>MonarchGrammarFactory</c>, the catalog-driven
/// <c>LanguageServerManifest</c> builder, and any future tooling that needs to
/// surface the dialect surface area without re-parsing tokens.
/// </summary>
/// <remarks>
/// The shell's <c>ShellHighlighter</c> deliberately does not consume these lists —
/// it switches on parser <c>SqlToken</c> values directly, which is the
/// authoritative source for what the tokenizer recognizes. These string lists
/// exist for surfaces (Monarch, manifest) that need names without running the
/// tokenizer.
/// </remarks>
public static class SqlKeywordRegistry
{
    /// <summary>
    /// SQL clause and operator keywords. <c>TRUE</c>, <c>FALSE</c>, and
    /// <c>NULL</c> are intentionally excluded — they live in
    /// <see cref="BoolNullKeywords"/> so themes can color them distinctly.
    /// </summary>
    public static IReadOnlyList<string> ClauseKeywords { get; } =
    [
        // Core DML and clause keywords
        "SELECT", "INTO", "FROM", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "LATERAL", "APPLY", "ON", "WHERE", "AND", "OR",
        "NOT", "IN", "BETWEEN", "LIKE", "ILIKE", "REGEXP", "ESCAPE", "IS",
        "AS", "GROUP", "HAVING", "QUALIFY", "ORDER", "BY", "ASC",
        "DESC", "LIMIT", "OFFSET", "CAST", "EXTRACT", "AT", "TIME", "ZONE",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "LOCALTIME", "LOCALTIMESTAMP",

        // Conditional expressions
        "CASE", "WHEN", "THEN", "ELSE", "END",

        // Window function keywords
        "OVER", "PARTITION", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING",
        "FOLLOWING", "CURRENT",

        // Modifiers
        "EXISTS", "DISTINCT", "IGNORE", "RESPECT", "NULLS",

        // Common Table Expressions
        "WITH", "RECURSIVE", "MATERIALIZED",

        // Set operations
        "UNION", "ALL", "INTERSECT", "EXCEPT",

        // Heliosoph.DatumV extensions
        "LET", "SCAN", "INIT", "PIVOT", "UNPIVOT", "FOR", "INCLUDE",

        // ASSERT / DEFINE clause keywords
        "ASSERT", "DEFINE", "MESSAGE", "FAIL", "WARN", "SKIP", "ABORT",

        // DDL keywords
        "CREATE", "TABLE", "TEMP", "TEMPORARY", "DROP", "ALTER", "ADD",
        "COLUMN", "DEFAULT", "PRIMARY", "KEY", "IF", "INDEX",
        "FUNCTION", "PROCEDURE", "RETURNS", "RETURN", "PURE", "REPLACE",
        "ANALYZE", "REINDEX",

        // DML keywords
        "INSERT", "VALUES", "RETURNING", "UPDATE", "SET", "DELETE",

        // Export
        "COPY",

        // Direct invocation
        "CALL",

        // Procedural-batch keywords. BEGIN / END pair as block delimiters
        // (END is already in the conditional-expression list above).
        // FOR / IF / ELSE are reused from the clause and conditional lists.
        "BEGIN", "WHILE", "DECLARE", "TO",
        "BREAK", "CONTINUE", "PRINT",
        "TRY", "CATCH", "FINALLY", "RAISE",
        "APPEND", "RESERVE",
    ];

    /// <summary>Boolean and null literal keywords.</summary>
    public static IReadOnlyList<string> BoolNullKeywords { get; } =
        ["TRUE", "FALSE", "NULL"];

    /// <summary>
    /// Date-part field names used with <c>EXTRACT(field FROM source)</c> and
    /// <c>date_part('field', source)</c>. Names that overlap with SQL keywords
    /// or built-in functions (e.g. <c>YEAR</c>, <c>MONTH</c>) are excluded —
    /// they already get keyword or function coloring via earlier case rules.
    /// </summary>
    public static IReadOnlyList<string> DatePartKeywords { get; } =
    [
        "DOW", "DOY", "ISODOW", "ISOYEAR",
        "EPOCH", "JULIAN",
        "CENTURY", "DECADE", "MILLENNIUM",
        "MICROSECOND", "MILLISECOND",
        "TIMEZONE", "TIMEZONE_HOUR", "TIMEZONE_MINUTE",
    ];

    /// <summary>
    /// Column data type names. These are tokenized as <c>type.identifier</c>
    /// in Monarch so editor themes can color them distinctly from keywords
    /// and plain identifiers.
    /// </summary>
    public static IReadOnlyList<string> TypeKeywords { get; } =
    [
        "Unknown",
        "Type",
        "Boolean",
        "UInt8", "UInt16", "UInt32", "UInt64", "UInt128",
        "Int8", "Int16", "Int32", "Int64", "Int128",
        "Float16", "Float32", "Float64", "Decimal",
        "Date", "Time", "Timestamp", "TimestampTz", "Duration",
        "String", "Uuid",
        "Image", "Audio", "Video", "Json", "Struct",
        "PointCloud", "Mesh",
        // Array is a type-position wrapper keyword (Array<T>). The lexer
        // tokenises it as a plain identifier — the parser's recursive
        // TypeNameParser does the wrapper recognition — but the editor's
        // Monarch highlighter consults this list to colour the word with
        // the same theme entry as scalar type names. List<T> (the body-local
        // growable accumulator) is the same shape and gets the same colour.
        "Array",
        "List",
    ];

}
