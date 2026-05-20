namespace Heliosoph.DatumV.LanguageServer;

/// <summary>
/// Locates a <c>${…}</c> splice in a backtick-delimited template string and
/// reports whether a given absolute cursor offset lands inside one. Hover
/// and completion both need this — they treat the splice body as a regular
/// SQL expression context once they know where it lives in the outer text.
/// </summary>
/// <remarks>
/// <para>
/// The tokenizer collapses an entire template string (backticks, literal
/// chunks, every splice) into a single
/// <see cref="Heliosoph.DatumV.Parsing.Tokens.SqlToken.TemplateString"/> token,
/// so the LS's cursor-on-token machinery can't reach into a splice on its
/// own. This locator does the secondary walk: it mirrors the tokenizer's
/// template scan
/// (<see cref="Heliosoph.DatumV.Parsing.Tokens.SqlTokenizer"/>) — backtick to
/// backtick, splices on <c>${</c>, brace-depth tracking, single-quoted
/// strings inside splices skipped, backslash escapes consumed — and stops
/// at the first splice that contains the cursor.
/// </para>
/// <para>
/// Cursor semantics inside a splice:
/// </para>
/// <list type="bullet">
///   <item><description>Cursor between <c>${</c> and the first body
///   character → inside (start of splice).</description></item>
///   <item><description>Cursor between the last body character and the
///   closing <c>}</c> → inside (end of splice — matches the position
///   editors place the cursor at after typing the last splice char, so
///   autocomplete works mid-edit).</description></item>
///   <item><description>Cursor strictly past the closing <c>}</c> →
///   NOT inside.</description></item>
/// </list>
/// <para>
/// Unterminated splices (a <c>${</c> with no matching <c>}</c> within the
/// template) extend to the template's closing backtick, so editing the SQL
/// mid-splice still yields a meaningful hover/completion result. Nested
/// backtick templates inside a splice body are not modelled — the engine
/// tokenizer doesn't support them either, so the locator's behaviour is
/// "first inner backtick terminates the outer template", same as the
/// engine.
/// </para>
/// </remarks>
internal static class TemplateSpliceLocator
{
    /// <summary>
    /// Result of a successful splice location: where the splice body sits
    /// in the outer SQL, and the body text itself for downstream
    /// tokenization / classification.
    /// </summary>
    /// <param name="BodyStart">
    /// Absolute offset in the outer SQL of the first character inside
    /// <c>${</c>. For an empty splice <c>${}</c> this equals
    /// <see cref="BodyEnd"/>.
    /// </param>
    /// <param name="BodyEnd">
    /// Absolute offset in the outer SQL one past the last character before
    /// the matching <c>}</c>. For an unterminated splice this is the
    /// outer template's closing-backtick position (or the end of input if
    /// the template itself is unterminated).
    /// </param>
    /// <param name="Body">
    /// The substring <c>sql[BodyStart..BodyEnd]</c>, exposed so callers can
    /// re-tokenize / re-classify without re-slicing.
    /// </param>
    internal readonly record struct SpliceLocation(int BodyStart, int BodyEnd, string Body);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="cursorOffset"/> sits inside
    /// a template-string splice body. The first splice that contains the
    /// cursor wins (cursor can only ever be inside one splice at a time
    /// once nested-template support is added; today the first hit is the
    /// only hit by construction).
    /// </summary>
    public static bool TryLocate(string sql, int cursorOffset, out SpliceLocation location)
    {
        location = default;
        if (string.IsNullOrEmpty(sql) || cursorOffset < 0 || cursorOffset > sql.Length)
        {
            return false;
        }

        int i = 0;
        while (i < sql.Length)
        {
            // Skip everything that isn't a template string. Templates start
            // with an un-escaped backtick — we mirror the engine
            // tokenizer's behaviour and ignore backticks inside any other
            // context (block comments, line comments, single-quoted
            // strings) since none of those introduce a splice anyway.
            if (sql[i] != '`')
            {
                i++;
                continue;
            }

            // Inside a template now. Walk until the closing backtick,
            // detecting splices along the way. Each splice gets its body
            // bounds; if the cursor lands inside one, return.
            int templateEnd = WalkTemplate(sql, i + 1, cursorOffset, out SpliceLocation? hit);
            if (hit is { } found)
            {
                location = found;
                return true;
            }
            // Resume scanning after the template (or at the end of input
            // for an unterminated one).
            i = templateEnd;
        }
        return false;
    }

    /// <summary>
    /// Walks the body of a template string starting at
    /// <paramref name="bodyStart"/> (the character right after the opening
    /// backtick), tracking splices and matching their <c>}</c>. If the
    /// cursor lands inside a splice body, returns it via
    /// <paramref name="hit"/>. The returned <c>int</c> is the position
    /// past the closing backtick (or the end of input for an unterminated
    /// template) so the outer scan can resume.
    /// </summary>
    private static int WalkTemplate(string sql, int bodyStart, int cursorOffset, out SpliceLocation? hit)
    {
        hit = null;
        int i = bodyStart;
        while (i < sql.Length)
        {
            char c = sql[i];

            // Escape sequence — consume the next char regardless. Matches
            // the tokenizer's permissive rule (the lowerer interprets
            // specific escapes like \` \$ \\ at parse time; here we just
            // skip so we don't false-positive a splice on `\${`).
            if (c == '\\' && i + 1 < sql.Length)
            {
                i += 2;
                continue;
            }

            // Closing backtick — template ends here.
            if (c == '`')
            {
                return i + 1;
            }

            // Splice start.
            if (c == '$' && i + 1 < sql.Length && sql[i + 1] == '{')
            {
                int spliceBodyStart = i + 2;
                int spliceBodyEnd = FindSpliceEnd(sql, spliceBodyStart);

                // Cursor-inside-splice rule: inclusive on both ends. Editor
                // cursor offsets are "between characters", so cursor ==
                // BodyEnd means "just before the `}`" (or "at EOF" for an
                // unterminated splice) — both are still meaningfully
                // inside the splice. Cursor == BodyEnd + 1 (just after the
                // `}`) is the next-position-out and not inside.
                if (cursorOffset >= spliceBodyStart && cursorOffset <= spliceBodyEnd)
                {
                    hit = new SpliceLocation(
                        BodyStart: spliceBodyStart,
                        BodyEnd: spliceBodyEnd,
                        Body: sql[spliceBodyStart..spliceBodyEnd]);
                    return -1; // sentinel: caller short-circuits on `hit`.
                }

                // Resume past the splice (`}` if terminated, EOF otherwise).
                bool isUnterminated = spliceBodyEnd >= sql.Length || sql[spliceBodyEnd] != '}';
                i = isUnterminated ? spliceBodyEnd : spliceBodyEnd + 1;
                continue;
            }

            i++;
        }

        // Unterminated template: return end-of-input so the outer scan
        // stops cleanly.
        return sql.Length;
    }

    /// <summary>
    /// Mirrors the tokenizer's splice-body walk: tracks brace depth, skips
    /// backslash escapes, and skips entire single-quoted string contents
    /// (so a literal <c>'}'</c> inside a splice doesn't terminate it).
    /// Returns the absolute offset of the matching <c>}</c> when found, or
    /// the end of input when the splice is unterminated.
    /// </summary>
    private static int FindSpliceEnd(string sql, int bodyStart)
    {
        int i = bodyStart;
        int braceDepth = 1;
        while (i < sql.Length && braceDepth > 0)
        {
            char c = sql[i];

            if (c == '\\' && i + 1 < sql.Length)
            {
                i += 2;
                continue;
            }

            // Single-quoted string — skip its contents wholesale so a
            // stray { or } in a literal can't confuse depth tracking.
            // SQL '' is a single-quote escape inside a string body
            // (PG semantics), mirrored from the engine tokenizer.
            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
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

            if (c == '{')
            {
                braceDepth++;
            }
            else if (c == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                {
                    return i;
                }
            }
            i++;
        }
        return sql.Length;
    }
}
