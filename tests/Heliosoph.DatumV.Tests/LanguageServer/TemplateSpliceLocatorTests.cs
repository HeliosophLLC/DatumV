namespace Heliosoph.DatumV.Tests.LanguageServer;

using Heliosoph.DatumV.LanguageServer;

/// <summary>
/// Unit tests for <see cref="TemplateSpliceLocator"/>. The locator is the
/// load-bearing piece for splice-aware hover and completion — if it gets
/// boundary handling wrong, both features misbehave. Cover the cases the
/// engine tokenizer itself respects: escape sequences, nested braces,
/// single-quoted strings inside splices, multi-line templates, and
/// unterminated splices the user is mid-typing.
/// </summary>
public sealed class TemplateSpliceLocatorTests
{
    private static (bool Found, int BodyStart, int BodyEnd, string Body) Locate(string sql, int cursor)
    {
        bool found = TemplateSpliceLocator.TryLocate(sql, cursor, out var loc);
        return (found, loc.BodyStart, loc.BodyEnd, loc.Body);
    }

    // ───────────────────── Plain SQL — no template ─────────────────────

    [Fact]
    public void TryLocate_NoTemplate_ReturnsFalse()
    {
        (bool found, _, _, _) = Locate("SELECT col FROM users", 7);
        Assert.False(found);
    }

    [Fact]
    public void TryLocate_TemplateWithNoSplice_ReturnsFalse()
    {
        // Plain backtick literal with no `${…}` — locator returns false even
        // when the cursor is inside the template text.
        const string sql = "SELECT `plain text`";
        int cursor = sql.IndexOf("plain", System.StringComparison.Ordinal);
        (bool found, _, _, _) = Locate(sql, cursor);
        Assert.False(found);
    }

    // ───────────────────── Single splice ─────────────────────

    [Fact]
    public void TryLocate_CursorInsideSpliceBody_ReturnsBody()
    {
        // Cursor on the `c` of `col`. The splice body is `col`.
        const string sql = "SELECT `prefix ${col} suffix`";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal);
        (bool found, int start, int end, string body) = Locate(sql, cursor);

        Assert.True(found);
        Assert.Equal("col", body);
        Assert.Equal(sql.IndexOf("col", System.StringComparison.Ordinal), start);
        Assert.Equal(start + "col".Length, end);
    }

    [Fact]
    public void TryLocate_CursorOnDollarSign_ReturnsFalse()
    {
        // Cursor on `$` of `${` — not inside the body.
        const string sql = "SELECT `x ${col} y`";
        int cursor = sql.IndexOf('$');
        (bool found, _, _, _) = Locate(sql, cursor);
        Assert.False(found);
    }

    [Fact]
    public void TryLocate_CursorPastClosingBrace_ReturnsFalse()
    {
        // Cursor one past the `}` (between `}` and ` y`). Outside the
        // splice body — not inside.
        const string sql = "SELECT `x ${col} y`";
        int cursor = sql.IndexOf('}') + 1;
        (bool found, _, _, _) = Locate(sql, cursor);
        Assert.False(found);
    }

    [Fact]
    public void TryLocate_CursorJustBeforeClosingBrace_ReturnsInside()
    {
        // Cursor between `l` and `}` — editors place the cursor here after
        // the user types the last splice character. Must be treated as
        // inside so autocomplete works mid-typing. This offset equals the
        // index of `}` in absolute terms; cursors are "between characters"
        // so offset-of-`}` means "just before the `}`".
        const string sql = "SELECT `x ${col} y`";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal) + "col".Length;
        Assert.Equal(sql.IndexOf('}'), cursor); // sanity-check the rule
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Equal("col", body);
    }

    [Fact]
    public void TryLocate_CursorAtBodyStart_ReturnsInside()
    {
        // Cursor between `{` and `c` — at the very start of the body.
        const string sql = "SELECT `x ${col} y`";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal);
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Equal("col", body);
    }

    // ───────────────────── Empty splice ─────────────────────

    [Fact]
    public void TryLocate_EmptySpliceMidEdit_ReturnsEmptyBody()
    {
        // User just typed `${` and is about to fill in the splice. Cursor
        // sits between `{` and `}`. Locator should return the empty body
        // so completion can offer suggestions in that position.
        const string sql = "SELECT `x ${} y`";
        int cursor = sql.IndexOf("${", System.StringComparison.Ordinal) + 2;
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Equal("", body);
    }

    // ───────────────────── Multiple splices ─────────────────────

    [Fact]
    public void TryLocate_CursorInSecondSplice_PicksSecond()
    {
        // Two splices `${a}` and `${b}` — cursor inside the second.
        const string sql = "SELECT `${a} and ${b}`";
        int cursor = sql.LastIndexOf("b", System.StringComparison.Ordinal);
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Equal("b", body);
    }

    [Fact]
    public void TryLocate_CursorBetweenSplices_ReturnsFalse()
    {
        // Cursor on the literal text between two splices — not inside any
        // splice. The locator should return false (hover/completion fall
        // back to the generic template-string behaviour).
        const string sql = "SELECT `${a} between ${b}`";
        int cursor = sql.IndexOf("between", System.StringComparison.Ordinal);
        (bool found, _, _, _) = Locate(sql, cursor);
        Assert.False(found);
    }

    // ───────────────────── Brace-nested struct literal ─────────────────────

    [Fact]
    public void TryLocate_NestedStructLiteralInSplice_FindsOuterEnd()
    {
        // `${ {a: 1}.a }` — the inner `{…}` is a struct literal. The
        // locator must NOT close on the inner `}`; the splice body is the
        // full ` {a: 1}.a ` text.
        const string sql = "SELECT `x ${ {a: 1}.a } y`";
        int cursor = sql.IndexOf("a: 1", System.StringComparison.Ordinal);
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Contains("{a: 1}", body);
        Assert.Contains(".a", body);
    }

    // ───────────────────── Single-quoted string in splice ─────────────────────

    [Fact]
    public void TryLocate_SingleQuotedStringWithBraceInSplice_DoesNotConfuseDepth()
    {
        // The literal `'}'` inside the splice must NOT close the splice
        // early. Cursor lands after the string, still inside the splice
        // body.
        const string sql = "SELECT `x ${ concat('}', col) } y`";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal);
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Contains("concat('}', col)", body);
    }

    [Fact]
    public void TryLocate_DoubledSingleQuoteEscape_StaysInString()
    {
        // PG-style `''` doubled-quote escape inside a single-quoted
        // string. The string contains a `}` literal; depth tracking must
        // remain at 1 across the whole quoted run.
        const string sql = "SELECT `x ${ 'a''b}' || col } y`";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal);
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Contains("col", body);
    }

    // ───────────────────── Backslash escapes ─────────────────────

    [Fact]
    public void TryLocate_EscapedDollarBrace_NotASplice()
    {
        // `\${not_a_splice}` is literal `${not_a_splice}` per the
        // tokenizer's escape rules — the locator must treat the leading
        // `\$` as a literal pair and NOT enter a splice. Cursor on
        // `not_a_splice` is in the literal chunk, not a splice body.
        const string sql = "SELECT `x \\${not_a_splice} y`";
        int cursor = sql.IndexOf("not_a_splice", System.StringComparison.Ordinal);
        (bool found, _, _, _) = Locate(sql, cursor);
        Assert.False(found);
    }

    [Fact]
    public void TryLocate_EscapedBacktickInTemplate_DoesNotCloseEarly()
    {
        // Backtick escaped with `\` inside the template body — must not
        // be treated as the template's close. The splice that follows is
        // still reachable.
        const string sql = "SELECT `pre \\` mid ${col} post`";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal);
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Equal("col", body);
    }

    // ───────────────────── Multi-line ─────────────────────

    [Fact]
    public void TryLocate_MultiLineSpliceBody_ReturnsCorrectBounds()
    {
        // Template + splice span newlines. The locator works on absolute
        // offsets so the row/column don't matter; just verify the bounds
        // include the line break.
        const string sql = "SELECT `\nstart ${\n  col\n} end\n`";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal);
        (bool found, int start, int end, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Contains("col", body);
        Assert.Contains("\n", body);
        Assert.Equal(end - start, body.Length);
    }

    // ───────────────────── Unterminated ─────────────────────

    [Fact]
    public void TryLocate_UnterminatedSplice_BodyRunsToEndOfTemplate()
    {
        // `${col` with no matching `}` before EOF. Locator extends the
        // body to end-of-input so hover/completion still fire while the
        // user is mid-edit.
        const string sql = "SELECT `prefix ${col";
        int cursor = sql.IndexOf("col", System.StringComparison.Ordinal);
        (bool found, _, int end, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Equal("col", body);
        Assert.Equal(sql.Length, end);
    }

    [Fact]
    public void TryLocate_CursorAtEofInsideUnterminatedSplice_ReturnsInside()
    {
        // User is mid-typing — cursor sits at end of input, still inside
        // the unterminated splice. Must be treated as inside.
        const string sql = "SELECT `prefix ${col";
        int cursor = sql.Length;
        (bool found, _, _, string body) = Locate(sql, cursor);
        Assert.True(found);
        Assert.Equal("col", body);
    }

    // ───────────────────── Outside templates ─────────────────────

    [Fact]
    public void TryLocate_DollarBraceOutsideTemplate_Ignored()
    {
        // `${…}` in plain SQL (outside backticks) isn't a splice — it's
        // just text. Locator returns false.
        const string sql = "SELECT ${not_a_splice} FROM t";
        int cursor = sql.IndexOf("not_a_splice", System.StringComparison.Ordinal);
        (bool found, _, _, _) = Locate(sql, cursor);
        Assert.False(found);
    }

    [Fact]
    public void TryLocate_CursorBeforeAnyTemplate_ReturnsFalse()
    {
        const string sql = "SELECT col FROM t WHERE x = `splice ${y}`";
        (bool found, _, _, _) = Locate(sql, 0);
        Assert.False(found);
    }
}
