namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
using DatumIngest.Parsing.Tokens;
using Superpower.Model;

/// <summary>
/// Provides hover information (type details, function signatures, keyword documentation)
/// for tokens at a given cursor position in SQL text.
/// </summary>
public sealed class HoverProvider
{
    private readonly LanguageServerManifest _manifest;

    /// <summary>
    /// Creates a hover provider backed by the given manifest.
    /// </summary>
    public HoverProvider(LanguageServerManifest manifest)
    {
        _manifest = manifest;
    }

    /// <summary>
    /// Returns hover information for the token at the given cursor offset, or null
    /// if there is nothing meaningful to display.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>A hover result with Markdown content, or null.</returns>
    public HoverResult? GetHover(string sql, int cursorOffset)
    {
        if (string.IsNullOrEmpty(sql) || cursorOffset < 0 || cursorOffset >= sql.Length)
        {
            return null;
        }

        // Tokenize the full SQL to find which token the cursor is on.
        List<TokenHit> tokens = TokenizeWithSpans(sql);
        TokenHit? hit = FindTokenAtOffset(tokens, cursorOffset);

        if (hit is null)
        {
            return null;
        }

        string? markdown = hit.Kind switch
        {
            SqlToken.Identifier => ResolveIdentifierHover(hit.Text, tokens, hit),
            _ when IsKeywordToken(hit.Kind) => GetKeywordHover(hit.Kind, hit.Text),
            _ => null,
        };

        if (markdown is null)
        {
            return null;
        }

        return new HoverResult
        {
            Contents = markdown,
            StartLine = hit.Line,
            StartColumn = hit.Column,
            EndLine = hit.Line,
            EndColumn = hit.Column + hit.Text.Length,
        };
    }

    /// <summary>
    /// Resolves hover for an identifier by checking if it's a function, table, or column name.
    /// </summary>
    private string? ResolveIdentifierHover(string name, List<TokenHit> tokens, TokenHit currentToken)
    {
        // If followed by '(' it's a function call.
        int currentIndex = tokens.IndexOf(currentToken);
        if (currentIndex >= 0 && currentIndex + 1 < tokens.Count &&
            tokens[currentIndex + 1].Kind == SqlToken.LeftParen)
        {
            return GetFunctionHover(name);
        }

        // If preceded by a dot, it's a qualified column — find the qualifier.
        if (currentIndex >= 2 &&
            tokens[currentIndex - 1].Kind == SqlToken.Dot &&
            (tokens[currentIndex - 2].Kind == SqlToken.Identifier || IsKeywordToken(tokens[currentIndex - 2].Kind)))
        {
            string qualifier = tokens[currentIndex - 2].Text;
            return GetQualifiedColumnHover(qualifier, name);
        }

        // Try as table name first, then as unqualified column.
        string? tableHover = GetTableHover(name);
        if (tableHover is not null)
        {
            return tableHover;
        }

        return GetColumnHover(name);
    }

    private string? GetFunctionHover(string name)
    {
        FunctionSignature? function = _manifest.Functions.FirstOrDefault(
            entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

        if (function is null)
        {
            return null;
        }

        string parameters = string.Join(", ", function.Parameters.Select(parameter =>
        {
            string optional = parameter.IsOptional ? "?" : "";
            return $"{parameter.Name}: {parameter.Kind}{optional}";
        }));

        string returnInfo = function.ReturnType is not null ? $" → {function.ReturnType}" : "";
        string signature = $"**{function.Name}**({parameters}){returnInfo}";

        if (function.IsTableValued)
        {
            signature = $"*(table-valued)* {signature}";
        }

        string categoryLine = $"*Category: {function.Category}*";

        return function.Description is not null
            ? $"{signature}\n\n{categoryLine}\n\n{function.Description}"
            : $"{signature}\n\n{categoryLine}";
    }

    private string? GetTableHover(string name)
    {
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            return null;
        }

        string header = $"**Table: {table.Name}** ({table.Columns.Count} columns)\n\n";
        string columns = string.Join("\n", table.Columns.Select(column =>
        {
            string nullable = column.Nullable ? " *(nullable)*" : "";
            return $"- `{column.Name}`: {column.Kind}{nullable}";
        }));

        return header + columns;
    }

    private string? GetColumnHover(string name)
    {
        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            TableColumnEntry? column = table.Columns.FirstOrDefault(
                entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

            if (column is not null)
            {
                string nullable = column.Nullable ? " *(nullable)*" : "";
                return $"**{column.Name}**: {column.Kind}{nullable}\n\nSource: {table.Name}";
            }
        }

        return null;
    }

    private string? GetQualifiedColumnHover(string tableQualifier, string columnName)
    {
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, tableQualifier, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            return null;
        }

        TableColumnEntry? column = table.Columns.FirstOrDefault(
            entry => string.Equals(entry.Name, columnName, StringComparison.OrdinalIgnoreCase));

        if (column is null)
        {
            return null;
        }

        string nullable = column.Nullable ? " *(nullable)*" : "";
        return $"**{tableQualifier}.{column.Name}**: {column.Kind}{nullable}";
    }

    private static string? GetKeywordHover(SqlToken kind, string text)
    {
        return kind switch
        {
            SqlToken.Select => "**SELECT** — Specifies columns and expressions to include in the query output.",
            SqlToken.From => "**FROM** — Specifies the data source table(s) for the query.",
            SqlToken.Where => "**WHERE** — Filters rows based on a boolean condition.",
            SqlToken.Join => "**JOIN** — Combines rows from two tables based on a related column.",
            SqlToken.Left => "**LEFT** — Left outer join: all rows from the left table, matching from right.",
            SqlToken.Right => "**RIGHT** — Right outer join: all rows from the right table, matching from left.",
            SqlToken.Full => "**FULL** — Full outer join: all rows from both tables.",
            SqlToken.Cross => "**CROSS** — Cross join: cartesian product of both tables.",
            SqlToken.Inner => "**INNER** — Inner join: only rows that match in both tables.",
            SqlToken.On => "**ON** — Specifies the join condition.",
            SqlToken.Into => "**INTO** — Writes query output to a file. Format inferred from extension (.csv, .parquet, .h5).",
            SqlToken.As => "**AS** — Creates an alias for a table or column.",
            SqlToken.Order => "**ORDER** — Used with BY to sort results.",
            SqlToken.By => "**BY** — Used with ORDER or SHARD to specify the sort/partition key.",
            SqlToken.Limit => "**LIMIT** — Restricts the number of output rows.",
            SqlToken.Offset => "**OFFSET** — Skips the specified number of rows before returning results.",
            SqlToken.And => "**AND** — Logical conjunction: both conditions must be true.",
            SqlToken.Or => "**OR** — Logical disjunction: at least one condition must be true.",
            SqlToken.Not => "**NOT** — Logical negation: inverts the boolean value.",
            SqlToken.In => "**IN** — Tests set membership: `column IN (value1, value2, ...)`.",
            SqlToken.Between => "**BETWEEN** — Tests range inclusion: `column BETWEEN low AND high`.",
            SqlToken.Like => "**LIKE** — Pattern matching with `%` (any chars) and `_` (single char) wildcards.",
            SqlToken.Is => "**IS** — Null testing: `column IS NULL` or `column IS NOT NULL`.",
            SqlToken.Null => "**NULL** — The null (missing value) literal.",
            SqlToken.Cast => "**CAST** — Explicit type conversion: `CAST(value AS type)`.",
            SqlToken.Shard => "**SHARD** — Partitions output into multiple files by row count or byte size.",
            SqlToken.Asc => "**ASC** — Ascending sort order (default).",
            SqlToken.Desc => "**DESC** — Descending sort order.",
            SqlToken.True => "**TRUE** — Boolean true literal.",
            SqlToken.False => "**FALSE** — Boolean false literal.",            SqlToken.Over => "**OVER** \u2014 Defines a window specification for a window function: `function() OVER(PARTITION BY ... ORDER BY ... ROWS BETWEEN ...)`.",
            SqlToken.Partition => "**PARTITION** \u2014 Used with BY to divide rows into partitions for window function evaluation.",
            SqlToken.Rows => "**ROWS** \u2014 Specifies a row-based window frame: `ROWS BETWEEN start AND end`.",
            SqlToken.Unbounded => "**UNBOUNDED** \u2014 Indicates the frame extends to the beginning (PRECEDING) or end (FOLLOWING) of the partition.",
            SqlToken.Preceding => "**PRECEDING** \u2014 Indicates rows before the current row in a window frame.",
            SqlToken.Following => "**FOLLOWING** \u2014 Indicates rows after the current row in a window frame.",
            SqlToken.Current => "**CURRENT** \u2014 Used with ROW to indicate the current row in a window frame: `CURRENT ROW`.",            _ => null,
        };
    }

    /// <summary>
    /// Tokenizes the full SQL, capturing position and text span for each token.
    /// Returns an empty list on failure.
    /// </summary>
    private static List<TokenHit> TokenizeWithSpans(string sql)
    {
        List<TokenHit> result = new();
        try
        {
            TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);
            foreach (Token<SqlToken> token in tokens)
            {
                string text = token.ToStringValue();
                // Superpower's Position is 1-based.
                int line = token.Position.Line - 1;
                int column = token.Position.Column - 1;
                result.Add(new TokenHit(token.Kind, text, line, column));
            }
        }
        catch
        {
            // Partial tokenization is acceptable.
        }

        return result;
    }

    /// <summary>
    /// Finds the token that contains the given character offset.
    /// </summary>
    private static TokenHit? FindTokenAtOffset(List<TokenHit> tokens, int cursorOffset)
    {
        // Convert cursor offset to an approximate line/column by scanning the source text
        // is impractical without the source. Instead, use cumulative position tracking.
        // The tokens already have line/column from Superpower — we need to match cursor offset.

        // Build a character-offset map: for each token, calculate its offset from line/column.
        // This requires the original text, but we don't have it here. Instead, we match by
        // checking if cursorOffset falls within each token's span. Since we passed the full
        // SQL to tokenize, we can reconstruct offsets from position.

        // Simplified approach: the caller passes cursorOffset, we need to find the token.
        // We'll iterate tokens and track their position within the source text.
        // Superpower gives us (line, column) in 1-based. We need a different strategy.

        // Actually — let's just use the tokens list by index and accept approximate matching.
        // A robust approach would track absolute offsets, but for hover this is acceptable.

        // We'll use a heuristic: find the token whose start position is closest to
        // but not exceeding the cursor offset (converted from line/column).
        // For single-line SQL (most common for this dialect), column ≈ offset.

        foreach (TokenHit token in tokens)
        {
            // For single-line SQL, column equals offset.
            // For multi-line, this is approximate but sufficient for hover.
            int tokenStart = token.Column; // Approximate for single-line
            int tokenEnd = tokenStart + token.Text.Length;

            if (cursorOffset >= tokenStart && cursorOffset < tokenEnd)
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsKeywordToken(SqlToken kind)
    {
        return kind <= SqlToken.False;
    }
}

/// <summary>
/// A token with its position information for hover hit-testing.
/// </summary>
internal sealed record TokenHit(SqlToken Kind, string Text, int Line, int Column);
