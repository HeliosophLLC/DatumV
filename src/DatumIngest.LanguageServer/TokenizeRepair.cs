namespace DatumIngest.LanguageServer;

/// <summary>
/// Computes a minimal "close the dangling stuff" suffix that, when
/// appended to a tokenizer-failing SQL string, lets the tokenizer
/// succeed. Used by hover and completion so a user mid-typing inside an
/// unterminated <c>${…}</c> splice or backtick template still gets
/// scope-extraction tokens — diagnostics still complain about the
/// unterminated input via their own (unrepaired) parser path.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the engine tokenizer's template/splice scan
/// (<see cref="DatumIngest.Parsing.Tokens.SqlTokenizer"/>) to detect the
/// trailing state at end-of-input: outside any lexical scope, inside a
/// template, inside a splice body, or inside a comment / string. Only
/// the template and splice tails produce a repair; everything else is a
/// no-op because either the tokenizer already handles it (line / block
/// comments, single-quoted strings) or repair would mask a real syntax
/// error the diagnostics path should surface.
/// </para>
/// <para>
/// Append order matters: when the cursor sits inside an unterminated
/// splice we owe both the close brace(s) <em>and</em> the closing
/// backtick — the brace first so the splice closes back into the
/// template, then the backtick to close the template itself. Splices
/// can carry balanced inner braces (struct literals like
/// <c>${ {a: 1}.a }</c>), so the walk tracks brace depth and emits one
/// <c>}</c> per still-open level.
/// </para>
/// </remarks>
internal static class TokenizeRepair
{
    /// <summary>
    /// Returns the close-suffix to append to <paramref name="sql"/> so
    /// the tokenizer succeeds, or <see langword="null"/> when no repair
    /// is needed. The suffix is the empty string when the input ends
    /// cleanly outside any lexical context — callers can short-circuit
    /// on null and skip the retry. Idempotent: applying the returned
    /// suffix and re-calling produces null (no second repair).
    /// </summary>
    public static string? ComputeRepairSuffix(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return null;

        TailState state = ScanTail(sql);
        return state switch
        {
            { Kind: TailKind.Outside } => null,
            { Kind: TailKind.LineComment } => null,
            { Kind: TailKind.BlockComment } => null,
            { Kind: TailKind.String } => null,
            { Kind: TailKind.Template } => "`",
            { Kind: TailKind.Splice, BraceDepth: var d } => new string('}', d) + "`",
            _ => null,
        };
    }

    /// <summary>
    /// Walks <paramref name="sql"/> once, mirroring the tokenizer's
    /// template / splice / string / comment scan, and reports the
    /// lexical scope at end-of-input. The walk doesn't allocate beyond
    /// the returned struct and runs in O(n).
    /// </summary>
    private static TailState ScanTail(string sql)
    {
        int i = 0;
        int n = sql.Length;
        while (i < n)
        {
            char c = sql[i];

            // -- line comment
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                i += 2;
                while (i < n && sql[i] != '\n') i++;
                if (i >= n) return new TailState(TailKind.LineComment, 0);
                i++;
                continue;
            }

            // /* block comment */
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i += 2;
                bool closed = false;
                while (i + 1 < n)
                {
                    if (sql[i] == '*' && sql[i + 1] == '/')
                    {
                        i += 2;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return new TailState(TailKind.BlockComment, 0);
                continue;
            }

            // 'single-quoted string'
            if (c == '\'')
            {
                i++;
                bool closed = false;
                while (i < n)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < n && sql[i + 1] == '\'')
                        {
                            // PG '' escape — still inside the string.
                            i += 2;
                            continue;
                        }
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return new TailState(TailKind.String, 0);
                continue;
            }

            // `template string`
            if (c == '`')
            {
                i++;
                TailState? templateResult = WalkTemplate(sql, ref i);
                if (templateResult is { } hit) return hit;
                continue;
            }

            i++;
        }
        return new TailState(TailKind.Outside, 0);
    }

    /// <summary>
    /// Walks a template body starting at <paramref name="i"/> (one past
    /// the opening backtick). Returns a <see cref="TailState"/> if
    /// end-of-input is reached inside the template (or inside one of
    /// its splices), or <see langword="null"/> when the template closes
    /// cleanly — caller resumes the outer scan in that case.
    /// </summary>
    private static TailState? WalkTemplate(string sql, ref int i)
    {
        int n = sql.Length;
        while (i < n)
        {
            char c = sql[i];

            if (c == '\\' && i + 1 < n)
            {
                i += 2;
                continue;
            }

            if (c == '`')
            {
                i++;
                return null;
            }

            if (c == '$' && i + 1 < n && sql[i + 1] == '{')
            {
                i += 2;
                int braceDepth = 1;
                while (i < n && braceDepth > 0)
                {
                    char inner = sql[i];

                    if (inner == '\\' && i + 1 < n)
                    {
                        i += 2;
                        continue;
                    }

                    // Single-quoted string inside the splice — skip its
                    // body so a stray { or } in a literal doesn't
                    // confuse depth tracking. Mirrors the tokenizer.
                    if (inner == '\'')
                    {
                        i++;
                        while (i < n)
                        {
                            if (sql[i] == '\'')
                            {
                                if (i + 1 < n && sql[i + 1] == '\'')
                                {
                                    i += 2;
                                    continue;
                                }
                                i++;
                                break;
                            }
                            i++;
                        }
                        continue;
                    }

                    if (inner == '{') braceDepth++;
                    else if (inner == '}') braceDepth--;
                    i++;
                }

                if (braceDepth > 0)
                {
                    // End-of-input inside the splice — owe `}` per still-
                    // open brace plus the closing backtick.
                    return new TailState(TailKind.Splice, braceDepth);
                }
                // Splice closed cleanly — resume template scan.
                continue;
            }

            i++;
        }
        // End-of-input inside the template body (outside any splice).
        return new TailState(TailKind.Template, 0);
    }

    private enum TailKind
    {
        Outside,
        LineComment,
        BlockComment,
        String,
        Template,
        Splice,
    }

    private readonly record struct TailState(TailKind Kind, int BraceDepth);
}
