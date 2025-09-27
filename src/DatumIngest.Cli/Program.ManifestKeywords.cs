/// <summary>
/// Partial class extension for the top-level Program to expose the manifest
/// keyword list for sync testing. Hard-coded to avoid a project reference to
/// DatumIngest.Editor; a sync test ensures these stay aligned with
/// <c>MonarchGrammarFactory.ClauseKeywords()</c>.
/// </summary>
internal partial class Program
{
    internal static string[] ManifestKeywords() =>
    [
        // Core DML and clause keywords
        "SELECT", "INTO", "FROM", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "LATERAL", "APPLY", "ON", "WHERE", "AND", "OR",
        "NOT", "IN", "BETWEEN", "LIKE", "ILIKE", "REGEXP", "ESCAPE", "IS",
        "AS", "SHARD", "GROUP", "HAVING", "QUALIFY", "ORDER", "BY", "ASC",
        "DESC", "LIMIT", "OFFSET", "CAST", "EXTRACT", "AT", "TIME", "ZONE",

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

        // DatumIngest extensions
        "LET", "SCAN", "INIT", "PIVOT", "UNPIVOT", "FOR", "INCLUDE",

        // ASSERT / DEFINE clause keywords
        "ASSERT", "DEFINE", "MESSAGE", "FAIL", "WARN", "SKIP", "ABORT",

        // DDL keywords
        "CREATE", "TABLE", "TEMP", "TEMPORARY", "DROP", "ALTER", "ADD",
        "COLUMN", "DEFAULT", "PRIMARY", "KEY", "IF", "INDEX",
        "ANALYZE",

        // DML keywords
        "INSERT", "VALUES", "UPDATE", "SET", "DELETE",

        // Boolean and null constants
        "TRUE", "FALSE", "NULL",
    ];
}
