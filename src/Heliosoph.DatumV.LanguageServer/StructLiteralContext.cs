using Heliosoph.DatumV.Parsing.Tokens;
using Superpower.Model;

namespace Heliosoph.DatumV.LanguageServer;

/// <summary>
/// Detects when a cursor sits inside a <c>{ field: value, … }</c> struct
/// literal that is itself an argument (directly or wrapped in an array
/// literal) of a function call, and reports the field-name slots already
/// committed in that literal. Drives struct-field completion inside
/// model calls like <c>models.fn([{ role: 'user', |</c> where the next
/// suggestion should be the next field name of <c>ChatMessage</c>, and
/// string-literal enum completion when the cursor sits inside a quoted
/// value (e.g. <c>{ role: '|' }</c>).
/// </summary>
internal static class StructLiteralContext
{
    /// <summary>
    /// Result of resolving the cursor's struct-literal context. Empty /
    /// default-initialised when the cursor isn't inside a struct literal
    /// that is part of an enclosing call.
    /// </summary>
    /// <param name="Call">Enclosing function call (name + active param + named args).</param>
    /// <param name="FieldNamesSoFar">Names of fields already committed (<c>name:</c>) in the literal.</param>
    /// <param name="IsAfterColon">True when the cursor sits past the in-progress field's <c>:</c> — value position.</param>
    /// <param name="ActiveFieldName">
    /// Name of the field whose value the cursor is editing. Non-<see langword="null"/>
    /// only when <paramref name="IsAfterColon"/> is true.
    /// </param>
    public readonly record struct Result(
        EnclosingCallResolver.Result Call,
        IReadOnlyList<string> FieldNamesSoFar,
        bool IsAfterColon,
        string? ActiveFieldName);

    /// <summary>
    /// Resolves the struct-literal context at <paramref name="cursorOffset"/>.
    /// Returns <see langword="false"/> when the cursor isn't inside a
    /// <c>{ … }</c> literal or when no enclosing call surrounds that literal.
    /// </summary>
    /// <remarks>
    /// Tokenises the full SQL (not just the prefix to the cursor) so a
    /// cursor sitting inside a closed string literal — <c>{ role: '|' }</c>
    /// where the closing quote already exists — still produces a clean
    /// token stream. The walk filters to tokens whose absolute position is
    /// before the cursor.
    /// </remarks>
    public static bool TryResolve(string sql, int cursorOffset, out Result result)
    {
        result = default;
        if (cursorOffset < 0 || cursorOffset > sql.Length) return false;
        if (!TryTokenize(sql, out List<Token<SqlToken>> allTokens) || allTokens.Count == 0)
        {
            // Fall back to prefix tokenization — if the full SQL is
            // unrecoverable but the prefix isn't, the user is still mid-edit
            // and the prefix tokens are enough for struct-literal detection.
            if (!TryTokenize(sql[..cursorOffset], out allTokens) || allTokens.Count == 0)
            {
                return false;
            }
        }

        // Truncate to tokens that begin strictly before the cursor.
        // (A string literal the cursor sits INSIDE starts before the cursor
        // and is included, which is fine — we treat it as an opaque token.)
        int beforeCursorCount = 0;
        while (beforeCursorCount < allTokens.Count
            && allTokens[beforeCursorCount].Position.Absolute < cursorOffset)
        {
            beforeCursorCount++;
        }
        if (beforeCursorCount == 0) return false;

        // Right-to-left scan for the innermost unmatched left brace. If we
        // hit an unmatched left paren first, the cursor isn't inside a
        // struct literal at all (just somewhere in the call's arg list).
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        int openBraceIndex = -1;
        for (int i = beforeCursorCount - 1; i >= 0; i--)
        {
            SqlToken k = allTokens[i].Kind;
            if (k == SqlToken.RightParen) { parenDepth++; continue; }
            if (k == SqlToken.LeftParen)
            {
                if (parenDepth > 0) { parenDepth--; continue; }
                return false; // hit the call paren before any unclosed brace
            }
            if (k == SqlToken.RightBracket) { bracketDepth++; continue; }
            if (k == SqlToken.LeftBracket)
            {
                if (bracketDepth > 0) bracketDepth--;
                continue;
            }
            if (k == SqlToken.RightBrace) { braceDepth++; continue; }
            if (k == SqlToken.LeftBrace)
            {
                if (braceDepth > 0) { braceDepth--; continue; }
                openBraceIndex = i;
                break;
            }
        }
        if (openBraceIndex < 0) return false;

        // Find the enclosing call's open paren by continuing the RTL scan
        // past the open brace. This is inlined rather than delegating to
        // EnclosingCallResolver because the cursor may sit inside an
        // unterminated-prefix construct (e.g. a string literal) that the
        // prefix-tokenizing helper can't parse; we already have a clean
        // truncated view of the full-SQL tokens in `allTokens` and can
        // reuse it directly.
        int parenDepth2 = 0;
        int bracketDepth2 = 0;
        int braceDepth2 = 0;
        int callParenIndex = -1;
        for (int i = openBraceIndex - 1; i >= 0; i--)
        {
            SqlToken k = allTokens[i].Kind;
            if (k == SqlToken.RightParen) { parenDepth2++; continue; }
            if (k == SqlToken.LeftParen)
            {
                if (parenDepth2 > 0) { parenDepth2--; continue; }
                callParenIndex = i;
                break;
            }
            if (k == SqlToken.RightBracket) { bracketDepth2++; continue; }
            if (k == SqlToken.LeftBracket)
            {
                if (bracketDepth2 > 0) bracketDepth2--;
                continue;
            }
            if (k == SqlToken.RightBrace) { braceDepth2++; continue; }
            if (k == SqlToken.LeftBrace)
            {
                if (braceDepth2 > 0) braceDepth2--;
                continue;
            }
        }
        if (callParenIndex < 0 || callParenIndex == 0) return false;

        // Call name is the token immediately preceding the paren, with an
        // optional `qualifier.` prefix (e.g. `models.chat`).
        Token<SqlToken> nameToken = allTokens[callParenIndex - 1];
        if (!IsCallableNameToken(nameToken.Kind)) return false;
        string functionName;
        if (callParenIndex >= 3
            && allTokens[callParenIndex - 2].Kind == SqlToken.Dot
            && IsCallableNameToken(allTokens[callParenIndex - 3].Kind))
        {
            functionName = $"{allTokens[callParenIndex - 3].ToStringValue()}.{nameToken.ToStringValue()}";
        }
        else
        {
            functionName = nameToken.ToStringValue();
        }

        // Active parameter index = top-level commas between the call's
        // paren and the cursor, tracking paren / bracket / brace depth so
        // nested literals don't miscount.
        int activeParameter = 0;
        int ap = 0, ab = 0, abr = 0;
        for (int j = callParenIndex + 1; j < beforeCursorCount; j++)
        {
            SqlToken k = allTokens[j].Kind;
            if (k == SqlToken.LeftParen) { ap++; continue; }
            if (k == SqlToken.RightParen) { if (ap > 0) ap--; continue; }
            if (k == SqlToken.LeftBracket) { ab++; continue; }
            if (k == SqlToken.RightBracket) { if (ab > 0) ab--; continue; }
            if (k == SqlToken.LeftBrace) { abr++; continue; }
            if (k == SqlToken.RightBrace) { if (abr > 0) abr--; continue; }
            if (k == SqlToken.Comma && ap == 0 && ab == 0 && abr == 0)
            {
                activeParameter++;
            }
        }

        EnclosingCallResolver.Result call = new(
            functionName,
            activeParameter,
            Array.Empty<string>());

        // Walk forward through the struct body's tokens, tracking nested
        // depth so commas inside a nested call / array / struct don't
        // split a field. Each completed slot contributes a field name
        // when it begins with `Identifier Colon`.
        List<string> fieldNames = new();
        int slotStart = openBraceIndex + 1;
        int fp = 0, fb = 0, fbr = 0;
        for (int j = slotStart; j < beforeCursorCount; j++)
        {
            SqlToken k = allTokens[j].Kind;
            if (k == SqlToken.LeftParen) { fp++; continue; }
            if (k == SqlToken.RightParen) { if (fp > 0) fp--; continue; }
            if (k == SqlToken.LeftBracket) { fb++; continue; }
            if (k == SqlToken.RightBracket) { if (fb > 0) fb--; continue; }
            if (k == SqlToken.LeftBrace) { fbr++; continue; }
            if (k == SqlToken.RightBrace) { if (fbr > 0) fbr--; continue; }
            if (k == SqlToken.Comma && fp == 0 && fb == 0 && fbr == 0)
            {
                TryCaptureFieldName(allTokens, slotStart, j, fieldNames);
                slotStart = j + 1;
                continue;
            }
        }

        // Inspect the in-progress (last) slot for a top-level colon. When
        // present, the user has committed to a field name and is now
        // editing the value; capture the name so we don't re-suggest it,
        // record it as the active field, and signal `IsAfterColon` so the
        // caller can fall through to value-position completion.
        bool isAfterColon = false;
        string? activeFieldName = null;
        int cp = 0, cb = 0, cbr = 0;
        for (int j = slotStart; j < beforeCursorCount; j++)
        {
            SqlToken k = allTokens[j].Kind;
            if (k == SqlToken.LeftParen) { cp++; continue; }
            if (k == SqlToken.RightParen) { if (cp > 0) cp--; continue; }
            if (k == SqlToken.LeftBracket) { cb++; continue; }
            if (k == SqlToken.RightBracket) { if (cb > 0) cb--; continue; }
            if (k == SqlToken.LeftBrace) { cbr++; continue; }
            if (k == SqlToken.RightBrace) { if (cbr > 0) cbr--; continue; }
            if (k == SqlToken.Colon && cp == 0 && cb == 0 && cbr == 0)
            {
                isAfterColon = true;
                if (j > slotStart && allTokens[slotStart].Kind == SqlToken.Identifier)
                {
                    activeFieldName = allTokens[slotStart].ToStringValue();
                    fieldNames.Add(activeFieldName);
                }
                break;
            }
        }

        result = new Result(call, fieldNames, isAfterColon, activeFieldName);
        return true;
    }

    private static bool IsCallableNameToken(SqlToken kind) =>
        kind == SqlToken.Identifier
        || kind == SqlToken.TypeKeyword
        || kind == SqlToken.Cast;

    /// <summary>
    /// If the slot between <paramref name="start"/> (inclusive) and
    /// <paramref name="end"/> (exclusive) begins with <c>identifier :</c>,
    /// appends the identifier text to <paramref name="names"/>. Anything
    /// else (positional placeholder, partial / empty slot) is ignored —
    /// we only record committed name : value pairs.
    /// </summary>
    private static void TryCaptureFieldName(
        IReadOnlyList<Token<SqlToken>> tokens, int start, int end, List<string> names)
    {
        if (end - start < 2) return;
        if (tokens[start].Kind != SqlToken.Identifier) return;
        if (tokens[start + 1].Kind != SqlToken.Colon) return;
        names.Add(tokens[start].ToStringValue());
    }

    /// <summary>
    /// Best-effort tokenization that strips a trailing partial sigil so an
    /// in-progress identifier doesn't fail the whole scan. Mirrors
    /// <see cref="EnclosingCallResolver"/>'s repair.
    /// </summary>
    private static bool TryTokenize(string text, out List<Token<SqlToken>> tokens)
    {
        tokens = new List<Token<SqlToken>>();
        try
        {
            foreach (Token<SqlToken> t in SqlTokenizer.Instance.Tokenize(text))
            {
                tokens.Add(t);
            }
            return true;
        }
        catch
        {
        }

        if (text.Length > 0 && (text[^1] == '@' || text[^1] == '$'))
        {
            tokens = new List<Token<SqlToken>>();
            try
            {
                foreach (Token<SqlToken> t in SqlTokenizer.Instance.Tokenize(text[..^1]))
                {
                    tokens.Add(t);
                }
                return true;
            }
            catch
            {
            }
        }

        return false;
    }
}
