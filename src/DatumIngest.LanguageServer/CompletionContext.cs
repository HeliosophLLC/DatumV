namespace DatumIngest.LanguageServer;

using DatumIngest.Parsing.Tokens;
using Superpower.Model;

/// <summary>
/// Classifies the cursor position within a SQL fragment to determine what kind
/// of completions to offer. Uses tokenization only (no full parse) so it works
/// with incomplete SQL that the user is still typing.
/// </summary>
public static class CompletionContext
{
    /// <summary>
    /// Analyzes the SQL text up to the cursor offset and returns the completion zone.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>The classified completion zone with any relevant context.</returns>
    public static CompletionZone Classify(string sql, int cursorOffset)
    {
        if (string.IsNullOrEmpty(sql) || cursorOffset <= 0)
        {
            return new CompletionZone(CompletionZoneKind.StatementStart, Prefix: null, TableQualifier: null);
        }

        // Only analyze text up to the cursor position.
        string textToCursor = sql[..System.Math.Min(cursorOffset, sql.Length)];

        // Tokenize — the tokenizer may fail on incomplete input, which is fine.
        List<TokenInfo> tokens = TokenizeSafely(textToCursor);

        if (tokens.Count == 0)
        {
            return new CompletionZone(CompletionZoneKind.StatementStart, Prefix: null, TableQualifier: null);
        }

        // Check if cursor is in the middle of typing an identifier (for prefix filtering).
        string? prefix = ExtractPrefix(textToCursor, tokens);

        // Check for dot-qualified context: "alias." or "alias.par"
        string? tableQualifier = ExtractTableQualifier(tokens, prefix);
        if (tableQualifier is not null)
        {
            return new CompletionZone(CompletionZoneKind.AfterDot, prefix, tableQualifier);
        }

        // Walk backwards through tokens to find the governing keyword.
        CompletionZoneKind zone = ClassifyFromTokens(tokens);

        return new CompletionZone(zone, prefix, TableQualifier: null);
    }

    /// <summary>
    /// Tokenizes the input, returning an empty list on failure rather than throwing.
    /// </summary>
    private static List<TokenInfo> TokenizeSafely(string text)
    {
        List<TokenInfo> result = new();
        try
        {
            TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(text);
            foreach (Token<SqlToken> token in tokens)
            {
                result.Add(new TokenInfo(token.Kind, token.ToStringValue(), token.Position));
            }
        }
        catch
        {
            // Incomplete input is expected during editing.
        }

        return result;
    }

    /// <summary>
    /// If the cursor is immediately after (or within) an identifier token, extracts
    /// the partial text as a prefix for filtering completions.
    /// </summary>
    private static string? ExtractPrefix(string textToCursor, List<TokenInfo> tokens)
    {
        if (tokens.Count == 0)
        {
            return null;
        }

        // Check if the last character before the cursor is whitespace or a symbol.
        // If so, there is no prefix — the user just typed a space after a keyword/symbol.
        char lastChar = textToCursor[^1];
        if (char.IsWhiteSpace(lastChar) || IsSqlSymbol(lastChar))
        {
            return null;
        }

        // The last token is (part of) the word the user is typing.
        TokenInfo lastToken = tokens[^1];
        if (lastToken.Kind == SqlToken.Identifier ||
            IsKeywordToken(lastToken.Kind))
        {
            return lastToken.Text;
        }

        return null;
    }

    /// <summary>
    /// Detects "alias." or "alias.partial" patterns for qualified column completion.
    /// </summary>
    private static string? ExtractTableQualifier(List<TokenInfo> tokens, string? prefix)
    {
        // Pattern: Identifier, Dot, [Identifier]
        // If prefix is set, the pattern is: Identifier, Dot, Identifier(prefix)
        // If no prefix, the pattern is: Identifier, Dot (cursor right after dot)

        if (prefix is not null && tokens.Count >= 3)
        {
            TokenInfo beforePrefix = tokens[^2]; // should be Dot
            TokenInfo qualifier = tokens[^3]; // should be Identifier
            if (beforePrefix.Kind == SqlToken.Dot &&
                (qualifier.Kind == SqlToken.Identifier || IsKeywordToken(qualifier.Kind)))
            {
                return qualifier.Text;
            }
        }
        else if (prefix is null && tokens.Count >= 2)
        {
            TokenInfo lastToken = tokens[^1]; // should be Dot
            TokenInfo qualifier = tokens[^2]; // should be Identifier
            if (lastToken.Kind == SqlToken.Dot &&
                (qualifier.Kind == SqlToken.Identifier || IsKeywordToken(qualifier.Kind)))
            {
                return qualifier.Text;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the token list to classify the cursor position by finding the nearest
    /// governing keyword context.
    /// </summary>
    private static CompletionZoneKind ClassifyFromTokens(List<TokenInfo> tokens)
    {
        // Track parenthesis nesting to skip function arguments when looking for context.
        int parenthesisDepth = 0;

        for (int index = tokens.Count - 1; index >= 0; index--)
        {
            SqlToken kind = tokens[index].Kind;

            if (kind == SqlToken.RightParen)
            {
                parenthesisDepth++;
                continue;
            }

            if (kind == SqlToken.LeftParen)
            {
                if (parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                    continue;
                }

                // Check if this is an OVER clause paren.
                if (index > 0 && tokens[index - 1].Kind == SqlToken.Over)
                {
                    return CompletionZoneKind.InsideOver;
                }

                // We're inside a function call or subquery — check what precedes the paren.
                if (index > 0 && tokens[index - 1].Kind == SqlToken.Identifier)
                {
                    return CompletionZoneKind.InFunctionArguments;
                }

                // Subquery paren or IN (...) — treat as expression context.
                return CompletionZoneKind.Expression;
            }

            // Skip tokens inside nested parens.
            if (parenthesisDepth > 0)
            {
                continue;
            }

            switch (kind)
            {
                case SqlToken.Select:
                    return CompletionZoneKind.AfterSelect;

                case SqlToken.From:
                    return CompletionZoneKind.AfterFrom;

                case SqlToken.Join:
                    return CompletionZoneKind.AfterJoin;

                case SqlToken.On:
                    return CompletionZoneKind.AfterOn;

                case SqlToken.Where:
                    return CompletionZoneKind.AfterWhere;

                case SqlToken.Group:
                    // "GROUP BY" — check for BY token following.
                    if (index + 1 < tokens.Count && tokens[index + 1].Kind == SqlToken.By)
                    {
                        return CompletionZoneKind.AfterGroupBy;
                    }
                    return CompletionZoneKind.AfterGroupBy;

                case SqlToken.Having:
                    return CompletionZoneKind.AfterHaving;

                case SqlToken.Qualify:
                    return CompletionZoneKind.AfterQualify;

                case SqlToken.Order:
                    // "ORDER BY" — check for BY token following.
                    if (index + 1 < tokens.Count && tokens[index + 1].Kind == SqlToken.By)
                    {
                        return CompletionZoneKind.AfterOrderBy;
                    }
                    return CompletionZoneKind.AfterOrderBy;

                case SqlToken.By:
                    // Could be ORDER BY, GROUP BY, or SHARD ... BY — walk back to see.
                    if (index > 0 && tokens[index - 1].Kind == SqlToken.Order)
                    {
                        return CompletionZoneKind.AfterOrderBy;
                    }
                    if (index > 0 && tokens[index - 1].Kind == SqlToken.Group)
                    {
                        return CompletionZoneKind.AfterGroupBy;
                    }
                    return CompletionZoneKind.Expression;

                case SqlToken.Into:
                    return CompletionZoneKind.AfterInto;

                case SqlToken.Union:
                case SqlToken.Intersect:
                case SqlToken.Except:
                    return CompletionZoneKind.AfterSetOperation;

                case SqlToken.And:
                case SqlToken.Or:
                case SqlToken.Not:
                case SqlToken.Equals:
                case SqlToken.NotEquals:
                case SqlToken.LessThan:
                case SqlToken.GreaterThan:
                case SqlToken.LessOrEqual:
                case SqlToken.GreaterOrEqual:
                case SqlToken.Like:
                case SqlToken.Between:
                case SqlToken.In:
                case SqlToken.Is:
                case SqlToken.Plus:
                case SqlToken.Minus:
                case SqlToken.Star:
                case SqlToken.Slash:
                case SqlToken.Percent:
                case SqlToken.Caret:
                case SqlToken.Comma:
                    // Operator/separator — keep walking back to find the governing keyword.
                    continue;

                case SqlToken.As:
                    // After AS — user is typing an alias, no completions from schema.
                    return CompletionZoneKind.AfterAs;

                default:
                    // Identifiers, literals, etc. — keep walking back.
                    continue;
            }
        }

        // No governing keyword found — we're at the start of a statement.
        return CompletionZoneKind.StatementStart;
    }

    private static bool IsKeywordToken(SqlToken kind)
    {
        return kind <= SqlToken.False;
    }

    private static bool IsSqlSymbol(char character)
    {
        return character is '(' or ')' or ',' or '.' or '*' or '=' or '<' or '>' or
               '!' or '+' or '-' or '/' or '%' or '^' or '|' or ';' or '\'';
    }
}

/// <summary>
/// A classified cursor position within a SQL fragment.
/// </summary>
/// <param name="Kind">The type of completion zone.</param>
/// <param name="Prefix">The partial text already typed for filtering, or null if at a boundary.</param>
/// <param name="TableQualifier">The table alias before a dot for qualified column completions, or null.</param>
public sealed record CompletionZone(CompletionZoneKind Kind, string? Prefix, string? TableQualifier);

/// <summary>
/// The kind of SQL context the cursor is in, determining which completions to offer.
/// </summary>
public enum CompletionZoneKind
{
    /// <summary>Before any keyword — offer SELECT and other statement-starting keywords.</summary>
    StatementStart,

    /// <summary>After SELECT — offer columns, functions, *, table.*.</summary>
    AfterSelect,

    /// <summary>After FROM — offer table names.</summary>
    AfterFrom,

    /// <summary>After JOIN — offer table names.</summary>
    AfterJoin,

    /// <summary>After ON — offer columns for join conditions.</summary>
    AfterOn,

    /// <summary>After WHERE — offer columns, functions, operators.</summary>
    AfterWhere,

    /// <summary>After ORDER BY — offer columns.</summary>
    AfterOrderBy,

    /// <summary>After GROUP BY — offer columns for grouping keys.</summary>
    AfterGroupBy,

    /// <summary>After HAVING — offer columns, aggregate functions, operators.</summary>
    AfterHaving,

    /// <summary>After QUALIFY — offer columns, window functions, aggregate functions, operators.</summary>
    AfterQualify,

    /// <summary>After INTO — offer file path (no schema completions).</summary>
    AfterInto,

    /// <summary>After AS — user is typing an alias (no schema completions).</summary>
    AfterAs,

    /// <summary>Inside function call parentheses — offer columns, functions, literals.</summary>
    InFunctionArguments,

    /// <summary>After a dot — offer columns from the qualified table alias.</summary>
    AfterDot,

    /// <summary>General expression context — offer columns, functions, operators.</summary>
    Expression,

    /// <summary>Inside OVER clause — offer PARTITION BY, ORDER BY, ROWS BETWEEN.</summary>
    InsideOver,

    /// <summary>After UNION, INTERSECT, or EXCEPT — offer ALL and SELECT.</summary>
    AfterSetOperation,
}
