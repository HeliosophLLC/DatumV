using DatumIngest.Manifest;

namespace DatumIngest.LanguageServer;

/// <summary>
/// Builds the Monarch grammar definition for the DatumIngest SQL dialect.
/// Monarch is Monaco Editor's built-in client-side tokenizer format; the
/// resulting object is serialized to JSON and used by the browser to provide
/// syntax highlighting without a server round-trip.
/// </summary>
public static class MonarchGrammarFactory
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
    ///   <item><c>type.identifier</c> — column data type names (Int32, Float64, String, etc.)</item>
    ///   <item><c>predefined.function</c> — built-in function names (count, sum, abs, etc.)</item>
    ///   <item><c>identifier</c> — unquoted and double-quoted identifiers (default)</item>
    /// </list>
    /// Token rules are intentionally ordered: multi-character operators before
    /// single-character ones, literals before identifiers, identifiers last so
    /// keyword matching via the <c>@keywords</c> case table takes precedence.
    /// </remarks>
    public static object Build() => new
    {
        defaultToken = "identifier",
        ignoreCase = true,
        keywords = SqlKeywordRegistry.ClauseKeywords,
        boolNullKeywords = SqlKeywordRegistry.BoolNullKeywords,
        typeKeywords = SqlKeywordRegistry.TypeKeywords,
        datePartKeywords = SqlKeywordRegistry.DatePartKeywords,
        builtinFunctions = SqlKeywordRegistry.BuiltinFunctions,
        tokenizer = new
        {
            root = new object[]
            {
                // Whitespace and comments are delegated to a sub-state so they
                // work regardless of position in the input.
                new { @include = "@whitespace" },

                // Single-quoted string literals. Transitions into a sub-state
                // so the string highlight survives across newlines — Monarch
                // tokenizes one line at a time, and a single-line regex
                // `'([^'\\]|'')*'` would unhighlight everything past the
                // first newline (re-tokenizing it as code). The state-based
                // form keeps the string class alive until the closing quote.
                new[] { @"'", "string", "@singleQuotedString" },

                // Backtick-delimited template strings: transition into a sub-state
                // that highlights the body as a string and ${…} splices as
                // delimited expression regions. The string class lights up the
                // theme color even before the closing backtick is typed.
                new[] { @"`", "string", "@templateString" },

                // Numeric literals: integer, decimal, and scientific notation.
                new[] { @"\d+(\.\d*)?([eE][+-]?\d+)?", "number" },

                // Named parameter placeholders: $identifier
                new[] { @"\$[a-zA-Z_]\w*", "variable" },

                // Double-quoted identifiers: "column name". Same multi-line
                // story as single-quoted strings — use a sub-state so the
                // identifier class persists across newlines (rare in practice
                // but consistent and crash-free if someone does it).
                new[] { @"""", "identifier", "@doubleQuotedIdentifier" },

                // TABLESAMPLE transitions to a sub-state that highlights the method
                // name (BERNOULLI, SYSTEM, STRATIFIED, BALANCED) as a keyword.
                // Without this, method names are colored as plain identifiers since
                // they are parsed as contextual identifiers, not reserved keywords.
                // The negative lookahead prevents matching a longer identifier like
                // TABLESAMPLEFOO.
                new[] { @"TABLESAMPLE(?!\w)", "keyword", "@tablesampleMethod" },

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
                            ["@typeKeywords"] = "type.identifier",
                            ["@datePartKeywords"] = "attribute.name",
                            ["@keywords"] = "keyword",
                            ["@builtinFunctions"] = "predefined.function",
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

            // Single-quoted string body sub-state. SQL escapes a literal
            // single quote by doubling it (''); we recognise that BEFORE
            // the lone-quote close rule so the doubled-quote stays in
            // string class. Everything else on the line is body content.
            // Newlines are matched implicitly by Monarch persisting the
            // state across line boundaries.
            singleQuotedString = new object[]
            {
                new[] { @"''", "string" },
                new[] { @"'", "string", "@pop" },
                new[] { @"[^']+", "string" },
            },

            // Double-quoted identifier body sub-state. Mirrors the single-
            // quoted string state, with "" as the escape and the identifier
            // class instead of the string class.
            doubleQuotedIdentifier = new object[]
            {
                new[] { @"""""", "identifier" },
                new[] { @"""", "identifier", "@pop" },
                new[] { @"[^""]+", "identifier" },
            },

            // Template-string body sub-state. Highlights the body as a string,
            // recognizes \-escapes, and transitions into @templateSplice when
            // it sees ${. Pops back to root on the closing backtick.
            templateString = new object[]
            {
                new[] { @"\\.", "string.escape" },
                new[] { @"\$\{", "delimiter.bracket", "@templateSplice" },
                new[] { @"`", "string", "@pop" },
                new[] { @"[^`\\$]+", "string" },
                // Lone $ that isn't followed by { — keep it as part of the
                // string body so highlighting doesn't break.
                new[] { @"\$", "string" },
            },

            // Template-string splice sub-state. Tokenized like normal SQL
            // (delegates to root) so identifiers, numbers, and operators
            // pick up their usual colors. Pops back to @templateString on
            // the closing brace.
            templateSplice = new object[]
            {
                new[] { @"\}", "delimiter.bracket", "@pop" },
                // Reuse the root tokenizer for splice contents — strings,
                // numbers, identifiers, operators all behave the same.
                new { @include = "@root" },
            },

            // TABLESAMPLE method sub-state: highlights the method name that follows
            // TABLESAMPLE as a keyword, then pops back to root. Handles contextual
            // identifiers (BERNOULLI, SYSTEM, STRATIFIED, BALANCED) that aren't
            // reserved keywords. The empty-match fallback ensures non-method tokens
            // are re-processed by root state rules.
            tablesampleMethod = new object[]
            {
                new[] { @"[ \t\r\n]+", "white" },
                new[] { @"(?:BERNOULLI|SYSTEM|STRATIFIED|BALANCED)(?!\w)", "keyword", "@pop" },
                new[] { @"", "", "@pop" },
            },
        },
    };

}
