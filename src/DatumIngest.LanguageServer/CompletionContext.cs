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

        // Track whether the cursor sits inside a string, comment, or template
        // splice. Splices are the interesting case — completion inside a
        // ${…} block should behave like a free-floating expression context,
        // so we recursively classify just the splice text.
        CursorContext context = ClassifyCursorContext(textToCursor);
        switch (context.Kind)
        {
            case CursorContextKind.String:
            case CursorContextKind.Comment:
                return new CompletionZone(CompletionZoneKind.InsideStringOrComment, Prefix: null, TableQualifier: null);

            case CursorContextKind.Splice:
                // Re-classify using just the splice's text-to-cursor as if it
                // were standalone SQL. The splice's content is a scalar
                // expression — Expression-zone completions (columns, scalar
                // functions, keywords) are what the user wants.
                string spliceTextToCursor = textToCursor[context.SpliceStartOffset..];
                return ClassifyExpressionContext(spliceTextToCursor);

            case CursorContextKind.Code:
            default:
                break;
        }

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
        CompletionZoneKind zone = ClassifyFromTokens(tokens, hasPrefix: prefix is not null);

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
    private static CompletionZoneKind ClassifyFromTokens(List<TokenInfo> tokens, bool hasPrefix)
    {
        // Track parenthesis nesting to skip function arguments when looking for context.
        int parenthesisDepth = 0;

        // Track whether the backward walk has crossed a balanced (...) group.
        // Used by contextual keyword zones (e.g., TABLESAMPLE) to distinguish
        // "right after the keyword" from "after the keyword's arguments".
        bool passedParenGroup = false;

        // Track whether any content (identifiers, literals, balanced paren groups)
        // has been passed between the cursor and the current position in the walk.
        // Used to distinguish "need a table name" (FROM |) from "have a table,
        // need next clause" (FROM t |). When hasPrefix is true the last token is
        // the partially-typed word and must be skipped so it doesn't count as content.
        bool passedContent = false;
        int startIndex = hasPrefix ? tokens.Count - 2 : tokens.Count - 1;

        // The most recent significant token before the cursor (skipping
        // whatever's being typed). Procedural control-flow detection uses
        // this to tell "IF @x > 1 |" (predicate done — body expected) from
        // "IF @x > |" (predicate continues — expression expected): the
        // first ends on a value-like token, the second on an operator.
        bool lastTokenIsValueLike =
            startIndex >= 0 && IsValueLikeToken(tokens[startIndex].Kind);

        for (int index = startIndex; index >= 0; index--)
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
                    if (parenthesisDepth == 0)
                    {
                        passedParenGroup = true;
                        passedContent = true;
                    }

                    continue;
                }

                // Check if this is an OVER clause paren.
                if (index > 0 && tokens[index - 1].Kind == SqlToken.Over)
                {
                    return CompletionZoneKind.InsideOver;
                }

                // Check if this is an EXTRACT( paren — offer date part field names.
                if (index > 0 && tokens[index - 1].Kind == SqlToken.Extract)
                {
                    return CompletionZoneKind.InsideExtract;
                }

                // Check if this is a CREATE TABLE paren — walk back past the table name
                // to find TABLE [TEMP/TEMPORARY] CREATE.
                if (index > 0 && IsCreateTableParen(tokens, index))
                {
                    return CompletionZoneKind.AfterCreateTableColumns;
                }

                // Check if this is an INSERT INTO table (...) column list.
                if (index > 0 && IsInsertColumnListParen(tokens, index))
                {
                    return CompletionZoneKind.AfterInsertTable;
                }

                // Check if this is a TABLESAMPLE method argument: TABLESAMPLE STRATIFIED(|
                // The identifier before the paren is the method name; if preceded by
                // TABLESAMPLE, we're inside the method argument (percentage or count).
                if (index >= 2
                    && tokens[index - 1].Kind == SqlToken.Identifier
                    && tokens[index - 2].Kind == SqlToken.Tablesample)
                {
                    return CompletionZoneKind.InsideTablesampleArg;
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
                    // Check if preceded by DELETE — offer table names for deletion target.
                    if (index > 0 && tokens[index - 1].Kind == SqlToken.Delete)
                    {
                        return CompletionZoneKind.AfterDeleteFrom;
                    }
                    return passedContent
                        ? CompletionZoneKind.AfterFromSource
                        : CompletionZoneKind.AfterFrom;

                case SqlToken.Join:
                case SqlToken.Lateral:
                case SqlToken.Apply:
                    return passedContent
                        ? CompletionZoneKind.AfterJoinSource
                        : CompletionZoneKind.AfterJoin;

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

                case SqlToken.Define:
                    return CompletionZoneKind.InsideDefineBlock;

                case SqlToken.Assert:
                case SqlToken.Message:
                    return CompletionZoneKind.AfterAssert;

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
                    // Check if preceded by INSERT — offer table names.
                    if (index > 0 && tokens[index - 1].Kind == SqlToken.Insert)
                    {
                        return CompletionZoneKind.AfterInsertInto;
                    }
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
                    // If content was passed (alias already typed), keep walking
                    // to find the governing clause (e.g., FROM t AS u | → AfterFromSource).
                    if (passedContent)
                    {
                        continue;
                    }
                    // No alias typed yet — user is typing an alias name, no completions.
                    return CompletionZoneKind.AfterAs;

                case SqlToken.Set:
                    // After UPDATE ... SET — offer columns for assignment.
                    return CompletionZoneKind.AfterUpdateSet;

                // ───────────────────── Procedural control flow ─────────────────────

                case SqlToken.If:
                case SqlToken.While:
                    // IF/WHILE have a predicate, then a body. Right after the
                    // keyword (no content) or mid-predicate (last token is an
                    // operator) → expression position. After a complete-looking
                    // predicate (content passed, last token value-like) → body
                    // position, where StatementStart's BEGIN / inner statements
                    // are the right offers.
                    return passedContent && lastTokenIsValueLike
                        ? CompletionZoneKind.StatementStart
                        : CompletionZoneKind.Expression;

                case SqlToken.Else:
                case SqlToken.Begin:
                case SqlToken.Try:
                    // ELSE/BEGIN/TRY have no predicate — a statement (or BEGIN
                    // block) follows directly. The right offer is whatever
                    // StatementStart provides.
                    return CompletionZoneKind.StatementStart;

                case SqlToken.Catch:
                    // CATCH @err <stmt>. Once the error variable has been
                    // typed (passedContent), we're at the body position;
                    // otherwise the user is still typing the @err binding
                    // and there's nothing useful to suggest.
                    return passedContent
                        ? CompletionZoneKind.StatementStart
                        : CompletionZoneKind.AfterAs;

                case SqlToken.Declare:
                    // DECLARE @name TYPE [= expr]. After the variable name and
                    // a value-like token (the type or end of an initializer),
                    // the next statement starts. Right after DECLARE itself,
                    // suppress completions — the user is naming a variable.
                    return passedContent && lastTokenIsValueLike
                        ? CompletionZoneKind.Expression
                        : CompletionZoneKind.AfterAs;

                case SqlToken.Print:
                case SqlToken.Raise:
                    // PRINT / RAISE take an expression argument.
                    return CompletionZoneKind.Expression;

                // ───────────────────── DDL / DML keywords ─────────────────────

                case SqlToken.Create:
                    return CompletionZoneKind.AfterCreate;

                case SqlToken.Drop:
                    return CompletionZoneKind.AfterDrop;

                case SqlToken.Table:
                    // Walk back to see if preceded by CREATE [TEMP/TEMPORARY] or DROP or ALTER.
                    for (int back = index - 1; back >= 0; back--)
                    {
                        SqlToken prior = tokens[back].Kind;
                        if (prior is SqlToken.Temp or SqlToken.Temporary)
                            continue;
                        if (prior == SqlToken.Create)
                            return CompletionZoneKind.AfterCreateTableColumns;
                        if (prior == SqlToken.Drop)
                            return CompletionZoneKind.AfterDrop;
                        if (prior == SqlToken.Alter)
                            return CompletionZoneKind.AfterAlterTable;
                        break;
                    }
                    continue;

                case SqlToken.Insert:
                    return CompletionZoneKind.AfterInsertInto;

                case SqlToken.Values:
                    return CompletionZoneKind.Expression;

                case SqlToken.Update:
                    return CompletionZoneKind.AfterUpdate;

                case SqlToken.Delete:
                    return CompletionZoneKind.AfterDeleteFrom;

                case SqlToken.Alter:
                    return CompletionZoneKind.AfterAlterTable;

                case SqlToken.Add:
                    return CompletionZoneKind.AfterAlterTableAdd;

                case SqlToken.Analyze:
                    // After ANALYZE — offer table names.
                    return CompletionZoneKind.AfterFrom;

                case SqlToken.Let:
                    // After LET — user may be typing a binding name or expression.
                    // Return AfterSelect to offer columns and functions for the expression.
                    return CompletionZoneKind.AfterSelect;

                // ───────────────────── Contextual identifier keywords ─────────────────────

                case SqlToken.Tablesample:
                    // TABLESAMPLE is a contextual keyword governing point. If we've crossed
                    // a (...) group, the cursor is after the method arguments (e.g., STRATIFIED(10) |)
                    // and we should suggest ON/REPEATABLE. Otherwise, we're right after TABLESAMPLE
                    // and should suggest method names.
                    return passedParenGroup
                        ? CompletionZoneKind.AfterTablesampleMethodArg
                        : CompletionZoneKind.AfterTablesample;

                default:
                    // Identifiers, literals, etc. — mark as content and keep walking back.
                    passedContent = true;
                    continue;
            }
        }

        // No governing keyword found — we're at the start of a statement.
        return CompletionZoneKind.StatementStart;
    }

    /// <summary>
    /// Checks whether the left-paren at <paramref name="parenIndex"/> belongs to a
    /// CREATE [TEMP|TEMPORARY] TABLE name (...) column definition list.
    /// </summary>
    private static bool IsCreateTableParen(List<TokenInfo> tokens, int parenIndex)
    {
        // Pattern: CREATE [TEMP|TEMPORARY] TABLE [IF NOT EXISTS] name (
        // Walk back from the paren: identifier (table name), then optionally
        // EXISTS/NOT/IF, then TABLE, then optionally TEMP/TEMPORARY, then CREATE.
        int cursor = parenIndex - 1;

        // Skip the table name identifier.
        if (cursor < 0 || (tokens[cursor].Kind != SqlToken.Identifier && !IsKeywordToken(tokens[cursor].Kind)))
            return false;
        cursor--;

        // Skip optional IF NOT EXISTS.
        if (cursor >= 0 && tokens[cursor].Kind == SqlToken.Exists) cursor--;
        if (cursor >= 0 && tokens[cursor].Kind == SqlToken.Not) cursor--;
        if (cursor >= 0 && tokens[cursor].Kind == SqlToken.If) cursor--;

        // Expect TABLE.
        if (cursor < 0 || tokens[cursor].Kind != SqlToken.Table) return false;
        cursor--;

        // Skip optional TEMP/TEMPORARY.
        if (cursor >= 0 && tokens[cursor].Kind is SqlToken.Temp or SqlToken.Temporary) cursor--;

        // Expect CREATE.
        return cursor >= 0 && tokens[cursor].Kind == SqlToken.Create;
    }

    /// <summary>
    /// Checks whether the left-paren at <paramref name="parenIndex"/> belongs to an
    /// INSERT INTO name (...) column list.
    /// </summary>
    private static bool IsInsertColumnListParen(List<TokenInfo> tokens, int parenIndex)
    {
        // Pattern: INSERT INTO name (
        int cursor = parenIndex - 1;

        // Skip the table name identifier.
        if (cursor < 0 || (tokens[cursor].Kind != SqlToken.Identifier && !IsKeywordToken(tokens[cursor].Kind)))
            return false;
        cursor--;

        // Expect INTO.
        if (cursor < 0 || tokens[cursor].Kind != SqlToken.Into) return false;
        cursor--;

        // Expect INSERT.
        return cursor >= 0 && tokens[cursor].Kind == SqlToken.Insert;
    }

    private static bool IsKeywordToken(SqlToken kind)
    {
        return kind < SqlToken.Identifier;
    }

    /// <summary>
    /// Whether <paramref name="kind"/> is a token that can stand at the end
    /// of a complete value expression (identifier, literal, closing paren,
    /// boolean / NULL keyword, variable / parameter, etc.) rather than an
    /// operator that requires more terms to follow.
    /// </summary>
    /// <remarks>
    /// Used to disambiguate procedural control-flow contexts:
    /// <c>IF @x &gt; 1 |</c> ends on a value-like literal (predicate looks
    /// done — body expected), while <c>IF @x &gt; |</c> ends on an operator
    /// (predicate continues — expression expected).
    /// </remarks>
    private static bool IsValueLikeToken(SqlToken kind) => kind switch
    {
        SqlToken.Identifier => true,
        SqlToken.NumberLiteral => true,
        SqlToken.StringLiteral => true,
        SqlToken.TemplateString => true,
        SqlToken.Variable => true,
        SqlToken.Parameter => true,
        SqlToken.True => true,
        SqlToken.False => true,
        SqlToken.Null => true,
        SqlToken.RightParen => true,
        SqlToken.RightBracket => true,
        SqlToken.RightBrace => true,
        _ => false,
    };

    /// <summary>
    /// Scans the text from start to cursor and reports what lexical context
    /// the cursor sits in. Mirrors the tokenizer's grammar (<c>''</c> escape
    /// inside single-quoted strings, <c>--</c> through end of line,
    /// <c>/* ... */</c> block, <c>`…`</c> template strings with <c>${…}</c>
    /// splices) so that what counts as "inside" agrees with how the SQL is
    /// parsed.
    /// </summary>
    private static CursorContext ClassifyCursorContext(string text)
    {
        int end = text.Length;
        int i = 0;
        while (i < end)
        {
            char c = text[i];

            // Line comment: -- through end of line.
            if (c == '-' && i + 1 < end && text[i + 1] == '-')
            {
                i += 2;
                while (i < end && text[i] != '\n') i++;
                if (i >= end) return CursorContext.InComment;
                i++; // consume newline
                continue;
            }

            // Block comment: /* ... */
            if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                i += 2;
                bool closed = false;
                while (i + 1 < end)
                {
                    if (text[i] == '*' && text[i + 1] == '/')
                    {
                        i += 2;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return CursorContext.InComment;
                continue;
            }

            // Single-quoted string with '' escape.
            if (c == '\'')
            {
                i++; // consume opening quote
                bool closed = false;
                while (i < end)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < end && text[i + 1] == '\'')
                        {
                            i += 2; // doubled-quote escape, still inside the string
                            continue;
                        }
                        i++; // consume closing quote
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return CursorContext.InString;
                continue;
            }

            // Backtick-delimited template string. Body is processed character-
            // by-character, with ${…} splices handled specially: the splice
            // body is scanned for a matching close brace; if the cursor sits
            // inside an unclosed splice, we return that location so the
            // caller can re-classify the splice text as a free expression.
            if (c == '`')
            {
                i++; // consume opening backtick
                while (i < end)
                {
                    char tc = text[i];

                    if (tc == '\\' && i + 1 < end)
                    {
                        i += 2;
                        continue;
                    }

                    if (tc == '`')
                    {
                        i++; // consume closing backtick
                        goto template_closed;
                    }

                    if (tc == '$' && i + 1 < end && text[i + 1] == '{')
                    {
                        // Splice body. spliceStart points just past the '{'.
                        int spliceStart = i + 2;
                        i = spliceStart;
                        int depth = 1;
                        while (i < end && depth > 0)
                        {
                            char sc = text[i];
                            if (sc == '\\' && i + 1 < end) { i += 2; continue; }
                            if (sc == '\'')
                            {
                                i++;
                                while (i < end)
                                {
                                    if (text[i] == '\'')
                                    {
                                        if (i + 1 < end && text[i + 1] == '\'') { i += 2; continue; }
                                        i++;
                                        break;
                                    }
                                    i++;
                                }
                                continue;
                            }
                            if (sc == '{') depth++;
                            else if (sc == '}') depth--;
                            if (depth == 0) break; // i still points at the closing '}'
                            i++;
                        }

                        if (depth != 0)
                        {
                            // Cursor sits inside an unclosed splice — caller
                            // handles this as expression context using the
                            // splice text from spliceStart up to the cursor.
                            return CursorContext.InSplice(spliceStart);
                        }

                        i++; // skip past the closing '}'
                        continue;
                    }

                    i++;
                }

                // Reached end-of-text without seeing the closing backtick:
                // cursor is inside the template string body (outside any
                // splice). Treat as in-string.
                return CursorContext.InString;

                template_closed: ;
                continue;
            }

            i++;
        }
        return CursorContext.InCode;
    }

    /// <summary>
    /// Re-classifies <paramref name="spliceText"/> as a free-floating scalar
    /// expression context. Used when the cursor sits inside a template-string
    /// <c>${…}</c> splice — the splice's contents are scalar SQL, so the
    /// completions there should be Expression-zone (columns, scalar
    /// functions, common keywords).
    /// </summary>
    /// <remarks>
    /// We synthesize the splice as the body of a phantom <c>SELECT</c> so the
    /// existing zone classifier walks back to the SELECT keyword and lands
    /// on <see cref="CompletionZoneKind.AfterSelect"/> when the splice is
    /// effectively empty. For a non-empty splice, the regular keyword
    /// (WHERE, AND, etc.) walking still applies, but the practical case is
    /// "user is typing an identifier inside the splice" which falls through
    /// to AfterSelect and surfaces columns/functions.
    /// </remarks>
    private static CompletionZone ClassifyExpressionContext(string spliceText)
    {
        // Wrap as `SELECT <splice>` so the existing classifier sees a valid
        // governing keyword. The 7-character "SELECT " prefix is included in
        // the cursor offset so the recursion lands at the end of the splice.
        const string prefix = "SELECT ";
        string synthetic = prefix + spliceText;
        return Classify(synthetic, synthetic.Length);
    }

    private static bool IsSqlSymbol(char character)
    {
        return character is '(' or ')' or ',' or '.' or '*' or '=' or '<' or '>' or
               '!' or '+' or '-' or '/' or '%' or '^' or '|' or ';' or '\'';
    }
}

/// <summary>
/// Classifies the lexical context a cursor sits in within partial SQL text:
/// regular code, an unclosed string, an unclosed comment, or inside a
/// <c>${…}</c> splice of a backtick-delimited template string.
/// </summary>
internal enum CursorContextKind
{
    /// <summary>Cursor is in regular SQL code.</summary>
    Code,

    /// <summary>Cursor is inside an unclosed string literal (single-quoted or backtick body).</summary>
    String,

    /// <summary>Cursor is inside an unclosed line or block comment.</summary>
    Comment,

    /// <summary>Cursor is inside an unclosed <c>${…}</c> splice of a template string.</summary>
    Splice,
}

/// <summary>
/// Result of <see cref="CompletionContext"/>'s lexical scan: which context
/// the cursor sits in, plus the splice-start offset when applicable.
/// </summary>
internal readonly record struct CursorContext(CursorContextKind Kind, int SpliceStartOffset)
{
    public static CursorContext InCode => new(CursorContextKind.Code, 0);
    public static CursorContext InString => new(CursorContextKind.String, 0);
    public static CursorContext InComment => new(CursorContextKind.Comment, 0);
    public static CursorContext InSplice(int spliceStartOffset) =>
        new(CursorContextKind.Splice, spliceStartOffset);
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

    /// <summary>After FROM source [alias] — offer next-clause keywords (WHERE, JOIN, GROUP BY, etc.).</summary>
    AfterFromSource,

    /// <summary>After JOIN — offer table names.</summary>
    AfterJoin,

    /// <summary>After JOIN source [alias] — offer ON and next-clause keywords.</summary>
    AfterJoinSource,

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

    /// <summary>After ASSERT — offer columns, functions, and operators for a predicate expression.</summary>
    AfterAssert,

    /// <summary>Inside a DEFINE block — offer LET and ASSERT declarations.</summary>
    InsideDefineBlock,

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

    /// <summary>Inside EXTRACT( — offer date part field names (YEAR, MONTH, DOW, etc.).</summary>
    InsideExtract,

    /// <summary>After UNION, INTERSECT, or EXCEPT — offer ALL and SELECT.</summary>
    AfterSetOperation,

    // ───────────────────── DDL / DML zones ─────────────────────

    /// <summary>After CREATE — offer TEMP, TEMPORARY, TABLE, INDEX.</summary>
    AfterCreate,

    /// <summary>After DROP — offer TABLE, INDEX, IF EXISTS.</summary>
    AfterDrop,

    /// <summary>After CREATE [TEMP] TABLE name ( — offer column type names.</summary>
    AfterCreateTableColumns,

    /// <summary>After INSERT INTO — offer table names.</summary>
    AfterInsertInto,

    /// <summary>After INSERT INTO name — offer column list or VALUES/SELECT.</summary>
    AfterInsertTable,

    /// <summary>After UPDATE — offer table names.</summary>
    AfterUpdate,

    /// <summary>After UPDATE name SET — offer columns for assignment.</summary>
    AfterUpdateSet,

    /// <summary>After DELETE FROM — offer table names.</summary>
    AfterDeleteFrom,

    /// <summary>After ALTER TABLE — offer table names.</summary>
    AfterAlterTable,

    /// <summary>After ALTER TABLE name ADD — offer COLUMN keyword and column type context.</summary>
    AfterAlterTableAdd,

    // ───────────────────── Contextual identifier zones ─────────────────────

    /// <summary>After TABLESAMPLE — offer sampling method names (BERNOULLI, SYSTEM, STRATIFIED, BALANCED).</summary>
    AfterTablesample,

    /// <summary>After TABLESAMPLE method(arg) — offer ON (for STRATIFIED/BALANCED) and REPEATABLE.</summary>
    AfterTablesampleMethodArg,

    /// <summary>Inside TABLESAMPLE method argument parens — indicate expected argument type (percentage or count).</summary>
    InsideTablesampleArg,

    /// <summary>Cursor sits inside an unclosed string literal or comment — no completions are appropriate.</summary>
    InsideStringOrComment,
}
