using DatumIngest.Parsing.Tokens;
using Superpower.Model;

namespace DatumIngest.LanguageServer;

/// <summary>
/// Detects when a cursor sits inside a <c>{ field: value, … }</c> struct
/// literal that is itself an argument (directly or wrapped in an array
/// literal) of a function call, and reports the field-name slots already
/// committed in that literal. Drives struct-field completion inside
/// model calls like <c>models.fn([{ role: 'user', |</c> where the next
/// suggestion should be the next field name of <c>ChatMessage</c>.
/// </summary>
internal static class StructLiteralContext
{
    /// <summary>
    /// Result of resolving the cursor's struct-literal context. Empty /
    /// default-initialised when the cursor isn't inside a struct literal
    /// that is part of an enclosing call.
    /// </summary>
    public readonly record struct Result(
        EnclosingCallResolver.Result Call,
        IReadOnlyList<string> FieldNamesSoFar,
        bool IsAfterColon);

    /// <summary>
    /// Resolves the struct-literal context at <paramref name="cursorOffset"/>.
    /// Returns <see langword="false"/> when the cursor isn't inside a
    /// <c>{ … }</c> literal or when no enclosing call surrounds that literal.
    /// </summary>
    public static bool TryResolve(string sql, int cursorOffset, out Result result)
    {
        result = default;
        if (cursorOffset < 0 || cursorOffset > sql.Length) return false;
        if (!TryTokenize(sql[..cursorOffset], out List<Token<SqlToken>> tokens)) return false;
        if (tokens.Count == 0) return false;

        // Right-to-left scan for the innermost unmatched left brace. If we
        // hit an unmatched left paren first, the cursor isn't inside a
        // struct literal at all (just somewhere in the call's arg list).
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        int openBraceIndex = -1;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            SqlToken k = tokens[i].Kind;
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

        if (!EnclosingCallResolver.TryResolve(sql, cursorOffset, out EnclosingCallResolver.Result call))
        {
            return false;
        }

        // Walk forward through the struct body's tokens, tracking nested
        // depth so commas inside a nested call / array / struct don't
        // split a field. Each completed slot contributes a field name
        // when it begins with `Identifier Colon`. The in-progress slot
        // (after the last top-level comma) contributes its name only
        // when the cursor is past its colon — `IsAfterColon`.
        List<string> fieldNames = new();
        bool isAfterColon = false;
        int slotStart = openBraceIndex + 1;
        int fp = 0, fb = 0, fbr = 0;
        for (int j = slotStart; j < tokens.Count; j++)
        {
            SqlToken k = tokens[j].Kind;
            if (k == SqlToken.LeftParen) { fp++; continue; }
            if (k == SqlToken.RightParen) { if (fp > 0) fp--; continue; }
            if (k == SqlToken.LeftBracket) { fb++; continue; }
            if (k == SqlToken.RightBracket) { if (fb > 0) fb--; continue; }
            if (k == SqlToken.LeftBrace) { fbr++; continue; }
            if (k == SqlToken.RightBrace) { if (fbr > 0) fbr--; continue; }
            if (k == SqlToken.Comma && fp == 0 && fb == 0 && fbr == 0)
            {
                TryCaptureFieldName(tokens, slotStart, j, fieldNames);
                slotStart = j + 1;
                continue;
            }
        }

        // Inspect the in-progress (last) slot for a top-level colon. When
        // present, the user has committed to a field name and is now
        // editing the value; capture the name so we don't re-suggest it,
        // and signal `IsAfterColon` so the caller can fall through to
        // expression completion rather than offering more field names.
        int cp = 0, cb = 0, cbr = 0;
        for (int j = slotStart; j < tokens.Count; j++)
        {
            SqlToken k = tokens[j].Kind;
            if (k == SqlToken.LeftParen) { cp++; continue; }
            if (k == SqlToken.RightParen) { if (cp > 0) cp--; continue; }
            if (k == SqlToken.LeftBracket) { cb++; continue; }
            if (k == SqlToken.RightBracket) { if (cb > 0) cb--; continue; }
            if (k == SqlToken.LeftBrace) { cbr++; continue; }
            if (k == SqlToken.RightBrace) { if (cbr > 0) cbr--; continue; }
            if (k == SqlToken.Colon && cp == 0 && cb == 0 && cbr == 0)
            {
                isAfterColon = true;
                TryCaptureFieldName(tokens, slotStart, j + 1, fieldNames);
                break;
            }
        }

        result = new Result(call, fieldNames, isAfterColon);
        return true;
    }

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
    private static bool TryTokenize(string prefix, out List<Token<SqlToken>> tokens)
    {
        tokens = new List<Token<SqlToken>>();
        try
        {
            foreach (Token<SqlToken> t in SqlTokenizer.Instance.Tokenize(prefix))
            {
                tokens.Add(t);
            }
            return true;
        }
        catch
        {
        }

        if (prefix.Length > 0 && (prefix[^1] == '@' || prefix[^1] == '$'))
        {
            tokens = new List<Token<SqlToken>>();
            try
            {
                foreach (Token<SqlToken> t in SqlTokenizer.Instance.Tokenize(prefix[..^1]))
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
