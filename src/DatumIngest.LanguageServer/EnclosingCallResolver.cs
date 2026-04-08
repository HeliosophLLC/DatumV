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

        int depth = 0;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            SqlToken kind = tokens[i].Kind;
            switch (kind)
            {
                case SqlToken.RightParen:
                    depth++;
                    continue;
                case SqlToken.LeftParen:
                    if (depth > 0) { depth--; continue; }
                    // Unmatched left paren — i is the call's opening paren.
                    return TryReadFunctionContext(tokens, i, out result);
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
        // commas.
        int slotStart = parenIndex + 1;
        int depth = 0;
        List<string> names = new();
        int commaCount = 0;
        int searchEnd = tokens.Count;
        for (int j = slotStart; j < searchEnd; j++)
        {
            SqlToken kind = tokens[j].Kind;
            if (kind == SqlToken.LeftParen) { depth++; continue; }
            if (kind == SqlToken.RightParen)
            {
                if (depth > 0) { depth--; continue; }
                break;
            }
            if (kind == SqlToken.Comma && depth == 0)
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
