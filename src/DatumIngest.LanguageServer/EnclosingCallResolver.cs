using DatumIngest.Parsing.Tokens;
using Superpower.Model;

namespace DatumIngest.LanguageServer;

/// <summary>
/// Token-level walker that identifies the function call enclosing a
/// cursor position. Shared by <see cref="SignatureHelpProvider"/> (which
/// renders the tooltip) and <see cref="CompletionProvider"/> (which
/// offers parameter-name completions for the PG-style <c>name := </c>
/// argument form).
/// </summary>
/// <remarks>
/// The walker mirrors
/// <see cref="SignatureHelpProvider.TryFindEnclosingCall"/>'s shape:
/// scan tokens right-to-left, balance paren depth, surface the
/// unmatched left paren and the identifier immediately before it.
/// Extends that shape by also accumulating per-slot named-argument
/// names (<c>name := …</c> or <c>name =&gt; …</c>) seen earlier in the
/// same call — needed so the completion provider can suppress already-
/// supplied parameter names.
/// </remarks>
internal static class EnclosingCallResolver
{
    /// <summary>
    /// Result of resolving the enclosing call. Empty / default-initialised
    /// when the cursor isn't inside any function-call argument list.
    /// </summary>
    public readonly record struct Result(
        string FunctionName,
        int ActiveParameter,
        IReadOnlyList<string> ArgumentNamesSoFar);

    /// <summary>
    /// Attempts to identify the function call whose argument list contains
    /// <paramref name="offset"/> within <paramref name="sql"/>. Returns
    /// <see langword="false"/> when the cursor isn't inside any call.
    /// </summary>
    public static bool TryResolve(string sql, int offset, out Result result)
    {
        result = default;
        if (offset < 0 || offset > sql.Length) return false;

        if (!TryTokenize(sql[..offset], out List<Token<SqlToken>> tokens)) return false;
        return TryFindEnclosingCall(tokens, out result);
    }

    /// <summary>
    /// Best-effort tokenization that strips a trailing partial sigil
    /// (<c>@</c> / <c>$</c>) so an in-progress identifier doesn't fail
    /// the whole scan. Mirrors
    /// <see cref="SignatureHelpProvider"/>'s <c>TryTokenize</c>.
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

    private static bool TryFindEnclosingCall(
        IReadOnlyList<Token<SqlToken>> tokens, out Result result)
    {
        result = default;

        // Track brackets and braces too so the right-to-left scan doesn't
        // pop the call paren when the cursor sits inside an array or
        // struct literal whose own brackets sit at paren depth 0. Without
        // this, a tokenized `f([{cursor}])` walking RTL would skip past
        // the `[` / `{` as opaque tokens and treat their commas / inner
        // tokens as paren-depth-0 content, miscounting the active slot.
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            SqlToken kind = tokens[i].Kind;
            switch (kind)
            {
                case SqlToken.RightParen:
                    parenDepth++;
                    continue;
                case SqlToken.LeftParen:
                    if (parenDepth > 0) { parenDepth--; continue; }
                    // Unmatched left paren — i is the call's opening paren.
                    return TryReadFunctionContext(tokens, i, out result);
                case SqlToken.RightBracket:
                    bracketDepth++;
                    continue;
                case SqlToken.LeftBracket:
                    if (bracketDepth > 0) bracketDepth--;
                    continue;
                case SqlToken.RightBrace:
                    braceDepth++;
                    continue;
                case SqlToken.LeftBrace:
                    if (braceDepth > 0) braceDepth--;
                    continue;
                default:
                    continue;
            }
        }
        return false;
    }

    private static bool TryReadFunctionContext(
        IReadOnlyList<Token<SqlToken>> tokens, int parenIndex, out Result result)
    {
        result = default;
        if (parenIndex == 0) return false;
        Token<SqlToken> prev = tokens[parenIndex - 1];
        if (!IsCallableNameToken(prev.Kind)) return false;

        string functionName;
        if (parenIndex >= 3
            && tokens[parenIndex - 2].Kind == SqlToken.Dot
            && IsCallableNameToken(tokens[parenIndex - 3].Kind))
        {
            string qualifier = tokens[parenIndex - 3].ToStringValue();
            string member = prev.ToStringValue();
            functionName = $"{qualifier}.{member}";
        }
        else
        {
            functionName = prev.ToStringValue();
        }

        // Walk forward from just past the paren to the end of the token
        // stream, splitting on top-level commas. For each slot, peek at the
        // leading two tokens — if they're `identifier (:= | =>)`, record
        // the identifier as a supplied named argument. The active slot
        // (the one containing the cursor) is the count of completed
        // commas. Track bracket / brace depth alongside paren depth so a
        // call like `f([{a:1}, {b:2}])` doesn't see the literal's inner
        // commas as parameter separators.
        int slotStart = parenIndex + 1;
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        List<string> names = new();
        int commaCount = 0;
        int searchEnd = tokens.Count;
        for (int j = slotStart; j < searchEnd; j++)
        {
            SqlToken kind = tokens[j].Kind;
            if (kind == SqlToken.LeftParen) { parenDepth++; continue; }
            if (kind == SqlToken.RightParen)
            {
                if (parenDepth > 0) { parenDepth--; continue; }
                break;
            }
            if (kind == SqlToken.LeftBracket) { bracketDepth++; continue; }
            if (kind == SqlToken.RightBracket)
            {
                if (bracketDepth > 0) bracketDepth--;
                continue;
            }
            if (kind == SqlToken.LeftBrace) { braceDepth++; continue; }
            if (kind == SqlToken.RightBrace)
            {
                if (braceDepth > 0) braceDepth--;
                continue;
            }
            if (kind == SqlToken.Comma
                && parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0)
            {
                TryCaptureNamedArg(tokens, slotStart, j, names);
                slotStart = j + 1;
                commaCount++;
                continue;
            }
        }
        TryCaptureNamedArg(tokens, slotStart, searchEnd, names);

        result = new Result(functionName, commaCount, names);
        return true;
    }

    /// <summary>
    /// If the slot between <paramref name="start"/> (inclusive) and
    /// <paramref name="end"/> (exclusive) begins with <c>identifier := </c>
    /// or <c>identifier =&gt; </c>, appends the identifier text to
    /// <paramref name="names"/>. Otherwise leaves <paramref name="names"/>
    /// alone — positional and partial / empty slots contribute nothing.
    /// </summary>
    private static void TryCaptureNamedArg(
        IReadOnlyList<Token<SqlToken>> tokens, int start, int end, List<string> names)
    {
        if (end - start < 2) return;
        if (tokens[start].Kind != SqlToken.Identifier) return;
        SqlToken op = tokens[start + 1].Kind;
        if (op != SqlToken.ColonEquals && op != SqlToken.FatArrow) return;
        names.Add(tokens[start].ToStringValue());
    }

    private static bool IsCallableNameToken(SqlToken kind) =>
        kind == SqlToken.Identifier
        || kind == SqlToken.TypeKeyword
        || kind == SqlToken.Cast;
}
