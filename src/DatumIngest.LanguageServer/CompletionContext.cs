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
            return new CompletionZone(CompletionZoneKind.StatementStart, Prefix: null, TableQualifier: null, VariablesInScope: null);
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
            return new CompletionZone(CompletionZoneKind.StatementStart, Prefix: null, TableQualifier: null, VariablesInScope: null);
        }

        // Procedural variables declared earlier in the fragment. Computed
        // once and propagated to every zone — the provider decides whether
        // to surface them. Block-scope nuances (a variable inside a popped
        // BEGIN/END block isn't actually visible) are deferred; permissive
        // suggestions are friendlier than no suggestions, and at worst the
        // engine will reject the resulting query at run time.
        IReadOnlyList<string> variablesInScope = ExtractVariablesInScope(tokens);

        // Tables / aliases bound by FROM / JOIN — extracted from the *full*
        // SQL text, not just the prefix up to the cursor. The user often
        // edits an existing query (cursor lands inside a function call
        // whose source query already has FROM after the cursor); scoping
        // off the cursor-prefix view alone would miss those tables and
        // wrongly suppress legitimate column completions. Empty list when
        // no FROM/JOIN exists anywhere in the buffer.
        List<TokenInfo> fullTokens = sql.Length == textToCursor.Length
            ? tokens
            : TokenizeSafely(sql);
        IReadOnlyList<string> tablesInScope = ExtractTablesInScope(fullTokens);

        // Check if cursor is in the middle of typing an identifier (for prefix filtering).
        string? prefix = ExtractPrefix(textToCursor, tokens);

        // Check for dot-qualified context: "alias." or "alias.par"
        string? tableQualifier = ExtractTableQualifier(tokens, prefix);
        if (tableQualifier is not null)
        {
            return new CompletionZone(
                CompletionZoneKind.AfterDot, prefix, tableQualifier, variablesInScope, tablesInScope);
        }

        // Walk backwards through tokens to find the governing keyword.
        // hasPrefix tells the walker to skip the trailing prefix-bearing
        // token. Most prefixes correspond to a real last token (Identifier
        // or completed Variable like `@x`); a bare `@` / `$` produces a
        // prefix string but no last token, so we shouldn't skip anything.
        bool prefixIsLastToken = prefix is not null
            && tokens.Count > 0
            && string.Equals(tokens[^1].Text, prefix, StringComparison.OrdinalIgnoreCase);
        CompletionZoneKind zone = ClassifyFromTokens(tokens, hasPrefix: prefixIsLastToken);

        return new CompletionZone(
            zone, prefix, TableQualifier: null, variablesInScope, tablesInScope);
    }

    /// <summary>
    /// Walks the token stream and collects procedural-variable bindings —
    /// names introduced via <c>DECLARE @x</c>, <c>FOR @i = ...</c>,
    /// <c>FOR @row IN (...)</c>, and <c>CATCH @err</c>. Each name is
    /// returned with its <c>@</c> prefix so it can be surfaced verbatim
    /// in completion popups. Duplicates are collapsed (latest binding
    /// wins for display order, but content equality is what matters).
    /// </summary>
    /// <summary>
    /// Walks the token stream and collects every table that appears after a
    /// <c>FROM</c> or <c>JOIN</c> keyword, plus any explicit alias the user
    /// has bound. Used by the column-completion path to suppress noise from
    /// the rest of the catalog when only a couple of tables are actually in
    /// scope (e.g. <c>SELECT abs(|) FROM users</c> should only suggest
    /// <c>users</c>'s columns inside the <c>abs(</c> argument list).
    /// </summary>
    /// <remarks>
    /// Returns an empty list when no FROM/JOIN is present — the caller
    /// (<see cref="CompletionProvider"/>) treats that as "no column scope"
    /// and suppresses column completions entirely. Subquery sources
    /// (<c>FROM (SELECT ...)</c>) are skipped because the inner SELECT's
    /// scope doesn't carry to the outer query.
    /// </remarks>
    private static IReadOnlyList<string> ExtractTablesInScope(List<TokenInfo> tokens)
    {
        if (tokens.Count == 0) return Array.Empty<string>();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = new();

        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            SqlToken k = tokens[i].Kind;
            if (k != SqlToken.From && k != SqlToken.Join) continue;

            // Only handle the simple `<keyword> Identifier [...]` shape.
            // Subquery sources (`FROM (SELECT ...)`) start with a left paren
            // — their inner scope doesn't apply to the outer query, so skip.
            int next = i + 1;
            if (tokens[next].Kind != SqlToken.Identifier) continue;

            string tableName = tokens[next].Text;
            if (string.IsNullOrEmpty(tableName)) continue;
            if (seen.Add(tableName)) ordered.Add(tableName);

            // Capture an optional alias: `FROM users u` or `FROM users AS u`.
            // The alias is also a valid qualifier inside the query body.
            int aliasIdx = next + 1;
            if (aliasIdx < tokens.Count && tokens[aliasIdx].Kind == SqlToken.As)
            {
                aliasIdx++;
            }
            if (aliasIdx < tokens.Count && tokens[aliasIdx].Kind == SqlToken.Identifier)
            {
                string alias = tokens[aliasIdx].Text;
                if (!string.IsNullOrEmpty(alias) && seen.Add(alias))
                {
                    ordered.Add(alias);
                }
            }
        }

        return ordered.Count == 0 ? Array.Empty<string>() : ordered;
    }

    private static IReadOnlyList<string> ExtractVariablesInScope(List<TokenInfo> tokens)
    {
        if (tokens.Count == 0) return Array.Empty<string>();
        // Use a HashSet for dedup + a List to preserve declaration order
        // — readers expect the first @x they see in the popup to be the
        // first one declared, not whatever the hash bucket happens to
        // surface.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = new();

        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            SqlToken k = tokens[i].Kind;
            // The patterns that introduce a binding all have a `@var`
            // immediately following: DECLARE @x ..., FOR @i = ...,
            // FOR @row IN ..., CATCH @err ...
            bool introduces = k == SqlToken.Declare
                || k == SqlToken.For
                || k == SqlToken.Catch;
            if (!introduces) continue;
            if (tokens[i + 1].Kind != SqlToken.Variable) continue;
            string name = tokens[i + 1].Text;
            if (string.IsNullOrEmpty(name)) continue;
            if (seen.Add(name)) ordered.Add(name);
        }
        return ordered;
    }

    /// <summary>
    /// Tokenizes the input, returning an empty list on failure rather than throwing.
    /// </summary>
    /// <remarks>
    /// One retry is attempted when the first pass throws: if the text ends
    /// with a bare <c>@</c> or <c>$</c> (a Variable / Parameter sigil with
    /// no name yet), we re-tokenize with the trailing sigil stripped so the
    /// classifier still sees the preceding tokens. Without this the user
    /// typing "<c>IF @</c>" would land at the empty-tokens early-return
    /// branch (StatementStart) and miss out on procedural-context
    /// completions like the in-scope variable list.
    /// </remarks>
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
            return result;
        }
        catch
        {
            // First pass failed — likely partial input. Try the sigil-strip
            // recovery below; if that also fails, fall through with an
            // empty result, which the classifier handles cleanly.
        }

        if (text.Length > 0 && (text[^1] == '@' || text[^1] == '$'))
        {
            try
            {
                TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(text[..^1]);
                foreach (Token<SqlToken> token in tokens)
                {
                    result.Add(new TokenInfo(token.Kind, token.ToStringValue(), token.Position));
                }
            }
            catch
            {
                // Still failing — leave the result empty.
            }
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

        // Bare `@` or `$` doesn't tokenize on its own (Variable / Parameter
        // need a letter to follow), so the tokenizer's last token is whatever
        // came before — usually a keyword. Return the sigil itself as the
        // prefix so completion filters keep just `@…` / `$…` candidates.
        if (lastChar == '@' || lastChar == '$')
        {
            return lastChar.ToString();
        }

        // The last token is (part of) the word the user is typing. Variable
        // (`@x`) and Parameter (`$p`) tokens are also valid prefixes —
        // without including them, typing `@x` would yield no prefix and
        // the popup would show every variable instead of those starting
        // with `x`.
        TokenInfo lastToken = tokens[^1];
        if (lastToken.Kind == SqlToken.Identifier ||
            lastToken.Kind == SqlToken.Variable ||
            lastToken.Kind == SqlToken.Parameter ||
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

        // Whether the backward walk has crossed an `=` token. Used by the
        // DECLARE classifier to tell type position (`DECLARE @x ⌷`) from
        // initializer position (`DECLARE @x INT32 = ⌷`).
        bool passedEquals = false;

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

                // Check if this is the option list of a CREATE INDEX
                // `... WITH (...)` clause. The token before the LeftParen
                // must be WITH; walking further back must find Index without
                // crossing FROM / JOIN (which would mean we're inside a CTE
                // or other WITH usage).
                if (index > 0 && tokens[index - 1].Kind == SqlToken.With
                    && IsCreateIndexWithOptionsParen(tokens, index - 1))
                {
                    return CompletionZoneKind.InsideCreateIndexWithOptions;
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
                    // Distinguish CREATE FUNCTION / CREATE PROCEDURE
                    // parameter lists from regular function-call argument
                    // lists: the routine cases want type-name completions
                    // for `(@x ⌷` rather than column / function suggestions.
                    if (index >= 2 && (
                        tokens[index - 2].Kind == SqlToken.Function ||
                        tokens[index - 2].Kind == SqlToken.Procedure))
                    {
                        return ClassifyRoutineParamPosition(tokens, leftParenIdx: index, lastTokenIdx: startIndex);
                    }
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
                    // CREATE INDEX has the same `... ON table (cols)` shape
                    // but isn't a join. When the cursor has crossed a paren
                    // group (the column list), look back for `Index` before
                    // any `From` / `Join` / `Lateral` / `Apply` — if found,
                    // this is CREATE INDEX after the column list.
                    if (passedParenGroup)
                    {
                        for (int back = index - 1; back >= 0; back--)
                        {
                            SqlToken backKind = tokens[back].Kind;
                            if (backKind == SqlToken.Index)
                            {
                                return CompletionZoneKind.AfterCreateIndexColumns;
                            }
                            if (backKind is SqlToken.From or SqlToken.Join
                                or SqlToken.Lateral or SqlToken.Apply)
                            {
                                break;
                            }
                        }
                    }
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

                case SqlToken.Equals:
                    // Mark the equals so the DECLARE classifier knows we're
                    // past the binding's "=" (initializer position). Then
                    // fall through to the operator-continue behavior.
                    passedEquals = true;
                    continue;

                case SqlToken.And:
                case SqlToken.Or:
                case SqlToken.Not:
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
                    // CAST(expr AS |) wants a type, not an alias. Detect by
                    // walking back to the enclosing unmatched left paren and
                    // checking whether CAST sits immediately before it.
                    if (IsInsideCastParen(tokens, index))
                    {
                        return CompletionZoneKind.AfterDeclareType;
                    }
                    // No alias typed yet — user is typing an alias name, no completions.
                    return CompletionZoneKind.AfterAs;

                case SqlToken.Returns:
                    // CREATE FUNCTION foo(...) RETURNS | — type position. Same
                    // shape as DECLARE @x |, so reuse the type-completion zone.
                    return CompletionZoneKind.AfterDeclareType;

                case SqlToken.Set:
                    // After UPDATE ... SET — offer columns for assignment.
                    return CompletionZoneKind.AfterUpdateSet;

                // ───────────────────── Procedural control flow ─────────────────────

                case SqlToken.If:
                case SqlToken.While:
                    // IF/WHILE have a predicate, then a body. Right after the
                    // keyword (no content) or mid-predicate (last token is an
                    // operator) → procedural expression position (no columns).
                    // After a complete-looking predicate (content passed, last
                    // token value-like) → body position, where StatementStart's
                    // BEGIN / inner statements are the right offers.
                    return passedContent && lastTokenIsValueLike
                        ? CompletionZoneKind.StatementStart
                        : CompletionZoneKind.ProceduralExpression;

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
                    // DECLARE @name TYPE [= expr]. Three sub-positions:
                    //   1. `DECLARE ⌷` (no content yet) — user is naming
                    //      the variable; suppress completions.
                    //   2. `DECLARE @x ⌷` (content but no `=` seen) — user
                    //      wants the TYPE next.
                    //   3. `DECLARE @x INT32 = ⌷` (passed `=`) — initializer
                    //      expression position; columns out of scope.
                    if (!passedContent) return CompletionZoneKind.AfterAs;
                    if (passedEquals) return CompletionZoneKind.ProceduralExpression;
                    return CompletionZoneKind.AfterDeclareType;

                case SqlToken.Print:
                case SqlToken.Raise:
                    // PRINT / RAISE take an expression argument. Procedural
                    // — no row context, so columns aren't legal here.
                    return CompletionZoneKind.ProceduralExpression;

                // ───────────────────── DDL / DML keywords ─────────────────────

                case SqlToken.Create:
                    return CompletionZoneKind.AfterCreate;

                case SqlToken.Drop:
                    // Walk back to see whether this DROP is the verb of an
                    // ALTER TABLE statement (`ALTER TABLE name DROP …`) — in
                    // which case the user wants COLUMN / CONSTRAINT / IF EXISTS
                    // completions, not top-level DROP TABLE / DROP INDEX
                    // suggestions. Recognise the shape by walking past an
                    // identifier (table name) → TABLE → ALTER.
                    {
                        int back = index - 1;
                        if (back >= 0 && (tokens[back].Kind == SqlToken.Identifier
                                          || IsKeywordToken(tokens[back].Kind)))
                        {
                            back--;
                            if (back >= 0 && tokens[back].Kind == SqlToken.Table)
                            {
                                back--;
                                if (back >= 0 && tokens[back].Kind == SqlToken.Alter)
                                {
                                    return CompletionZoneKind.AfterAlterTableDrop;
                                }
                            }
                        }
                    }
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

                case SqlToken.Reindex:
                    // After REINDEX — offer table names. The optional TABLE
                    // keyword between REINDEX and the table name is handled by
                    // the parser; for completion purposes both forms accept a
                    // table name next.
                    return CompletionZoneKind.AfterFrom;

                case SqlToken.Let:
                    // After LET — user may be typing a binding name or expression.
                    // Return AfterSelect to offer columns and functions for the expression.
                    return CompletionZoneKind.AfterSelect;

                case SqlToken.Index:
                    // After a CREATE INDEX column list closes (cursor has crossed
                    // a `(...)` paren group), offer USING / WITH. Before the
                    // column list (e.g. just-typed `CREATE INDEX `), fall
                    // through to walk back to `Create` and return AfterCreate.
                    // DROP INDEX never crosses a paren so it's unaffected.
                    if (passedParenGroup)
                    {
                        return CompletionZoneKind.AfterCreateIndexColumns;
                    }
                    continue;

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
                    // Contextual `USING` inside a CREATE INDEX shape: not a
                    // dedicated SqlToken, so detected by identifier text. The
                    // CREATE INDEX shape has `Index` somewhere before USING
                    // with no FROM / JOIN between; if we see that, this is
                    // the USING-method position.
                    if (kind == SqlToken.Identifier
                        && string.Equals(tokens[index].Text, "USING", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int back = index - 1; back >= 0; back--)
                        {
                            SqlToken backKind = tokens[back].Kind;
                            if (backKind == SqlToken.Index)
                            {
                                return CompletionZoneKind.AfterCreateIndexUsing;
                            }
                            if (backKind is SqlToken.From or SqlToken.Join
                                or SqlToken.Lateral or SqlToken.Apply)
                            {
                                break;
                            }
                        }
                    }

                    // Identifiers, literals, etc. — mark as content and keep walking back.
                    passedContent = true;
                    continue;
            }
        }

        // No governing keyword found — we're at the start of a statement.
        return CompletionZoneKind.StatementStart;
    }

    /// <summary>
    /// Walks back from <paramref name="fromIndex"/> tracking paren balance to
    /// find the enclosing unmatched left paren. Returns <see langword="true"/>
    /// when that paren is the argument list of a CAST(...) call —
    /// <c>CAST(x AS |)</c> wants type completions, not alias completions.
    /// </summary>
    private static bool IsInsideCastParen(List<TokenInfo> tokens, int fromIndex)
    {
        int depth = 0;
        for (int i = fromIndex - 1; i >= 0; i--)
        {
            SqlToken k = tokens[i].Kind;
            if (k == SqlToken.RightParen)
            {
                depth++;
                continue;
            }
            if (k == SqlToken.LeftParen)
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
                // Unmatched left paren — this is the enclosing argument list.
                return i > 0 && tokens[i - 1].Kind == SqlToken.Cast;
            }
        }
        return false;
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
    /// Checks whether the <c>WITH</c> at <paramref name="withIndex"/> opens
    /// the option list of a <c>CREATE INDEX ... WITH (...)</c> clause (as
    /// opposed to a CTE preamble). The shape requires an <c>Index</c> token
    /// somewhere earlier without an intervening <c>From</c> / <c>Join</c> /
    /// <c>Lateral</c> / <c>Apply</c>.
    /// </summary>
    private static bool IsCreateIndexWithOptionsParen(List<TokenInfo> tokens, int withIndex)
    {
        for (int back = withIndex - 1; back >= 0; back--)
        {
            SqlToken kind = tokens[back].Kind;
            if (kind == SqlToken.Index) return true;
            if (kind is SqlToken.From or SqlToken.Join
                or SqlToken.Lateral or SqlToken.Apply)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Sub-classifier for the cursor sitting inside a CREATE FUNCTION /
    /// CREATE PROCEDURE parameter list. Determines which sub-position the
    /// cursor is in (typing a name, typing a type, typing a default
    /// expression, or just past a comma) and routes to the matching zone.
    /// </summary>
    /// <remarks>
    /// Walks the tokens between the opening '(' and the cursor for the most
    /// recent "anchor" token. Anchors are the elements that mark a phase
    /// transition in the param grammar: <c>,</c> (next slot), <c>@var</c>
    /// (just declared the name), <c>=</c> (now typing default), type or
    /// IS / NOT / NULL keywords (in the post-type modifier zone).
    /// </remarks>
    private static CompletionZoneKind ClassifyRoutineParamPosition(
        List<TokenInfo> tokens, int leftParenIdx, int lastTokenIdx)
    {
        SqlToken? anchor = null;
        for (int i = leftParenIdx + 1; i <= lastTokenIdx; i++)
        {
            SqlToken k = tokens[i].Kind;
            if (k == SqlToken.Variable
                || k == SqlToken.Comma
                || k == SqlToken.Equals
                || k == SqlToken.TypeKeyword
                || k == SqlToken.Identifier
                || k == SqlToken.Is || k == SqlToken.Not || k == SqlToken.Null)
            {
                anchor = k;
            }
        }
        return anchor switch
        {
            // Right after `(` (no anchor) or right after `,` — the user is
            // about to type a `@var` name. Suppress completions; we have
            // nothing useful to suggest for a fresh identifier.
            null or SqlToken.Comma => CompletionZoneKind.AfterAs,
            // After a `@var` token — the type is what comes next.
            SqlToken.Variable => CompletionZoneKind.AfterDeclareType,
            // After `=` — the user is typing the default-value expression.
            SqlToken.Equals => CompletionZoneKind.ProceduralExpression,
            // After type / IS / NOT / NULL — these are post-type modifiers.
            // Default to type completions; we keep showing them in case the
            // user wants to switch the type they just typed.
            _ => CompletionZoneKind.AfterDeclareType,
        };
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
/// <param name="VariablesInScope">
/// Procedural variables declared earlier in the same fragment (DECLARE,
/// FOR loop bindings, CATCH error-variable bindings). Names include the
/// <c>@</c> prefix. Empty when no procedural bindings exist before the
/// cursor. Always populated regardless of zone — the provider decides
/// whether to surface them based on whether the zone is expression-like.
/// </param>
/// <param name="TablesInScope">
/// Tables and aliases bound by FROM / JOIN clauses earlier in the same
/// fragment. Used to scope column completions in expression zones —
/// when empty, no column suggestions surface. <c>null</c> means
/// "scope not extracted" (legacy callers); empty list means "extracted
/// and there's nothing in scope".
/// </param>
public sealed record CompletionZone(
    CompletionZoneKind Kind,
    string? Prefix,
    string? TableQualifier,
    IReadOnlyList<string>? VariablesInScope = null,
    IReadOnlyList<string>? TablesInScope = null);

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

    /// <summary>
    /// Procedural expression context — predicate of <c>IF</c> / <c>WHILE</c>,
    /// bounds of <c>FOR</c>, initializer of <c>DECLARE</c>, argument of
    /// <c>PRINT</c> / <c>RAISE</c>. Same shape as <see cref="Expression"/>
    /// but columns are <em>not</em> offered: at procedural top level there
    /// is no row context, so the only legal data references are <c>@vars</c>
    /// and scalar function calls. (A <c>(SELECT ...)</c> subquery inside the
    /// expression re-enters query context via the paren walk.)
    /// </summary>
    ProceduralExpression,

    /// <summary>Inside OVER clause — offer PARTITION BY, ORDER BY, ROWS BETWEEN.</summary>
    InsideOver,

    /// <summary>Inside EXTRACT( — offer date part field names (YEAR, MONTH, DOW, etc.).</summary>
    InsideExtract,

    /// <summary>After UNION, INTERSECT, or EXCEPT — offer ALL and SELECT.</summary>
    AfterSetOperation,

    // ───────────────────── DDL / DML zones ─────────────────────

    /// <summary>After CREATE — offer TEMP, TEMPORARY, TABLE, INDEX, UNIQUE INDEX, FUNCTION, PROCEDURE, OR REPLACE.</summary>
    AfterCreate,

    /// <summary>After DROP — offer TABLE, INDEX, IF EXISTS.</summary>
    AfterDrop,

    /// <summary>After CREATE [TEMP] TABLE name ( — offer column type names.</summary>
    AfterCreateTableColumns,

    /// <summary>
    /// Type-name position in a procedural binding: right after the variable
    /// name in <c>DECLARE @x ⌷</c>, <c>CREATE FUNCTION foo(@x ⌷</c>, or
    /// <c>CREATE PROCEDURE foo(@x ⌷</c>. Offers SQL type literals
    /// (<c>Int32</c>, <c>Float64</c>, <c>String</c>, …).
    /// </summary>
    AfterDeclareType,

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

    /// <summary>
    /// After <c>ALTER TABLE name DROP</c> — offer <c>COLUMN</c> (for the
    /// drop-column body) and <c>CONSTRAINT</c> (for the drop-constraint
    /// body), plus <c>IF EXISTS</c>.
    /// </summary>
    AfterAlterTableDrop,

    /// <summary>
    /// After a <c>CREATE INDEX name ON table (col, ...)</c> column list closes —
    /// offer <c>USING</c> (to pick an index method) and <c>WITH</c> (to supply
    /// method-specific options).
    /// </summary>
    AfterCreateIndexColumns,

    /// <summary>
    /// After <c>CREATE INDEX ... USING</c> — offer the available index methods
    /// (<c>FTS</c> in v1). The default composite B+Tree has no spelled-out
    /// keyword and is not surfaced.
    /// </summary>
    AfterCreateIndexUsing,

    /// <summary>
    /// Inside the <c>(...)</c> of <c>CREATE INDEX ... WITH (...)</c> — offer
    /// the option keys understood by the chosen method (<c>analyzer</c> for
    /// FTS in v1).
    /// </summary>
    InsideCreateIndexWithOptions,

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
