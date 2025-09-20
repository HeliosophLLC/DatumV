namespace DatumIngest.Editor;

/// <summary>
/// Builds the Monarch grammar definition for the DatumIngest SQL dialect.
/// Monarch is Monaco Editor's built-in client-side tokenizer format; the
/// resulting object is serialized to JSON and used by the browser to provide
/// syntax highlighting without a server round-trip.
/// </summary>
internal static class MonarchGrammarFactory
{
    /// <summary>
    /// Constructs and returns the Monarch grammar as an anonymous object graph.
    /// The caller serializes this to JSON and sends it to the client.
    /// </summary>
    /// <remarks>
    /// Token type names follow Monaco's standard naming conventions so they map
    /// automatically to editor theme colors without additional configuration:
    /// <list type="bullet">
    ///   <item><c>keyword</c> — SQL clause and operator keywords (blue in most themes)</item>
    ///   <item><c>keyword.constant</c> — TRUE, FALSE, NULL (distinct color in most themes)</item>
    ///   <item><c>string</c> — single-quoted string literals</item>
    ///   <item><c>number</c> — integer and floating-point literals</item>
    ///   <item><c>variable</c> — named parameter placeholders ($name)</item>
    ///   <item><c>comment</c> — line comments (--) and block comments (/* */)</item>
    ///   <item><c>operator</c> — arithmetic and comparison symbols</item>
    ///   <item><c>delimiter</c> — commas, parentheses, dots</item>
    ///   <item><c>identifier</c> — unquoted and double-quoted identifiers (default)</item>
    /// </list>
    /// Token rules are intentionally ordered: multi-character operators before
    /// single-character ones, literals before identifiers, identifiers last so
    /// keyword matching via the <c>@keywords</c> case table takes precedence.
    /// </remarks>
    internal static object Build() => new
    {
        defaultToken = "identifier",
        ignoreCase = true,
        keywords = ClauseKeywords(),
        boolNullKeywords = new[] { "TRUE", "FALSE", "NULL" },
        tokenizer = new
        {
            root = new object[]
            {
                // Whitespace and comments are delegated to a sub-state so they
                // work regardless of position in the input.
                new { @include = "@whitespace" },

                // Single-quoted string literals. '' is the escape sequence for a
                // literal single quote inside a string.
                new[] { @"'([^'\\]|'')*'", "string" },

                // Numeric literals: integer, decimal, and scientific notation.
                new[] { @"\d+(\.\d*)?([eE][+-]?\d+)?", "number" },

                // Named parameter placeholders: $identifier
                new[] { @"\$[a-zA-Z_]\w*", "variable" },

                // Double-quoted identifiers: "column name". "" is the escape sequence.
                new[] { @"""([^""\\]|"""")*""", "identifier" },

                // Unquoted identifiers and keywords. The @keywords and @boolNullKeywords
                // case tables are checked first; anything else is a plain identifier.
                new object[]
                {
                    @"[a-zA-Z_]\w*",
                    new
                    {
                        cases = new Dictionary<string, string>
                        {
                            ["@boolNullKeywords"] = "keyword.constant",
                            ["@keywords"] = "keyword",
                            ["@default"] = "identifier",
                        }
                    }
                },

                // Multi-character comparison operators must precede single-character ones.
                new[] { @"[!<>]=|<>", "operator" },
                new[] { @"[<>=]", "operator" },

                // Arithmetic and bitwise operators.
                new[] { @"[+\-*/%^|]", "operator" },

                // Punctuation delimiters.
                new[] { @"[,.()\[\]]", "delimiter" },
            },

            whitespace = new object[]
            {
                new[] { @"[ \t\r\n]+", "white" },
                // Line comments.
                new[] { @"--.*$", "comment" },
                // Block comments: transition to the @blockComment sub-state.
                new[] { @"/\*", "comment", "@blockComment" },
            },

            // Block comment sub-state: consume everything until */.
            blockComment = new object[]
            {
                new[] { @"\*/", "comment", "@pop" },
                new[] { @".", "comment" },
            },
        },
    };

    /// <summary>
    /// Returns the full list of SQL clause and operator keywords. TRUE, FALSE,
    /// and NULL are intentionally excluded — they are in <c>boolNullKeywords</c>
    /// so themes can color them distinctly from clause keywords.
    /// </summary>
    private static string[] ClauseKeywords() =>
    [
        // Core DML and clause keywords
        "SELECT", "INTO", "FROM", "JOIN", "LEFT", "RIGHT", "FULL", "OUTER",
        "CROSS", "INNER", "LATERAL", "APPLY", "ON", "WHERE", "AND", "OR",
        "NOT", "IN", "BETWEEN", "LIKE", "ILIKE", "REGEXP", "ESCAPE", "IS",
        "AS", "SHARD", "GROUP", "HAVING", "QUALIFY", "ORDER", "BY", "ASC",
        "DESC", "LIMIT", "OFFSET", "CAST",

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
        "LET", "PIVOT", "UNPIVOT", "FOR", "INCLUDE",

        // ASSERT / DEFINE clause keywords
        "ASSERT", "DEFINE", "MESSAGE", "FAIL", "WARN", "SKIP", "ABORT",
    ];
}
