namespace Heliosoph.DatumV.Tests.LanguageServer;

using Heliosoph.DatumV.LanguageServer;
using Heliosoph.DatumV.Parsing.Tokens;

/// <summary>
/// Unit tests for <see cref="TokenizeRepair"/>. The repair runs only when
/// hover / completion fail to tokenize otherwise, and its output must
/// (a) be the minimum needed to let the tokenizer succeed, (b) not mask
/// real syntax errors handled by the diagnostics path, and (c) be
/// idempotent — applying it once produces a tokenizable string.
/// </summary>
public sealed class TokenizeRepairTests
{
    private static string? Repair(string sql) => TokenizeRepair.ComputeRepairSuffix(sql);

    // ───────────────────── No repair needed ─────────────────────

    [Fact]
    public void Repair_PlainSql_ReturnsNull()
    {
        Assert.Null(Repair("SELECT col FROM t"));
    }

    [Fact]
    public void Repair_EmptyString_ReturnsNull()
    {
        Assert.Null(Repair(""));
    }

    [Fact]
    public void Repair_TerminatedTemplate_ReturnsNull()
    {
        Assert.Null(Repair("SELECT `complete`"));
    }

    [Fact]
    public void Repair_TerminatedTemplateWithSplice_ReturnsNull()
    {
        Assert.Null(Repair("SELECT `prefix ${col} suffix`"));
    }

    [Fact]
    public void Repair_TerminatedTemplateMultipleSplices_ReturnsNull()
    {
        Assert.Null(Repair("SELECT `${a} and ${b}`"));
    }

    // ───────────────────── Template tail ─────────────────────

    [Fact]
    public void Repair_UnterminatedTemplate_AppendsBacktick()
    {
        Assert.Equal("`", Repair("SELECT `text"));
    }

    [Fact]
    public void Repair_TemplateEndsAfterClosedSplice_AppendsBacktick()
    {
        Assert.Equal("`", Repair("SELECT `prefix ${col} suffix"));
    }

    // ───────────────────── Splice tail ─────────────────────

    [Fact]
    public void Repair_UnterminatedSplice_AppendsBraceAndBacktick()
    {
        Assert.Equal("}`", Repair("SELECT `text ${col"));
    }

    [Fact]
    public void Repair_UnterminatedSpliceAtEof_OnlyDollarBrace()
    {
        // User typed `${` and stopped — no body characters yet.
        Assert.Equal("}`", Repair("SELECT `${"));
    }

    [Fact]
    public void Repair_UnterminatedSpliceWithNestedStruct_ClosesAllOpenBraces()
    {
        // `${ {a: 1` — depth is 2 (splice + struct literal). Suffix
        // closes the struct first, then the splice, then the template.
        Assert.Equal("}}`", Repair("SELECT `text ${ {a: 1"));
    }

    [Fact]
    public void Repair_UnterminatedSpliceWithBalancedInnerBraces_OneClose()
    {
        // `${ {a: 1}` — struct literal already closed; only the splice
        // remains open.
        Assert.Equal("}`", Repair("SELECT `text ${ {a: 1}"));
    }

    // ───────────────────── String / comment red herrings ─────────────────────

    [Fact]
    public void Repair_StringContainingBacktick_DoesNotConfuse()
    {
        // The backtick lives inside a single-quoted string — must not
        // be treated as a template opener.
        Assert.Null(Repair("SELECT 'has ` in it' FROM t"));
    }

    [Fact]
    public void Repair_LineCommentContainingTemplateLooking_DoesNotConfuse()
    {
        // `--` runs to EOL — anything after it is ignored.
        Assert.Null(Repair("SELECT col -- `not a template` more"));
    }

    [Fact]
    public void Repair_BlockCommentContainingTemplateLooking_DoesNotConfuse()
    {
        Assert.Null(Repair("SELECT col /* `not a template` */ FROM t"));
    }

    [Fact]
    public void Repair_SingleQuotedStringInSpliceContainingBrace_DoesNotConfuseDepth()
    {
        // `'}'` inside a splice — depth tracking must skip the string.
        // Splice closes cleanly; no repair needed.
        Assert.Null(Repair("SELECT `${ concat('}', col) } y`"));
    }

    [Fact]
    public void Repair_UnterminatedSingleQuotedString_ReturnsNull()
    {
        // We don't repair unterminated strings — they're a real syntax
        // error the diagnostics path will surface, and "guess what kind
        // of string it is" repair would risk masking it.
        Assert.Null(Repair("SELECT 'unterminated"));
    }

    // ───────────────────── Idempotence ─────────────────────

    [Theory]
    [InlineData("SELECT `text")]
    [InlineData("SELECT `text ${col")]
    [InlineData("SELECT `text ${ {a: 1")]
    [InlineData("SELECT `${")]
    public void Repair_AppendingSuffix_MakesInputTokenizable(string sql)
    {
        string? suffix = Repair(sql);
        Assert.NotNull(suffix);
        string repaired = sql + suffix;

        // Repaired input must tokenize without exception.
        Exception? caught = Record.Exception(() => SqlTokenizer.Instance.Tokenize(repaired));
        Assert.Null(caught);

        // Idempotent: a second pass over the now-clean input returns null.
        Assert.Null(Repair(repaired));
    }

    // ───────────────────── Escape handling ─────────────────────

    [Fact]
    public void Repair_EscapedDollarBraceInTemplate_NotASplice()
    {
        // `\${` is a literal pair, not a splice opener — must not push
        // splice state. Template still needs a closing backtick.
        Assert.Equal("`", Repair("SELECT `prefix \\${literal} more"));
    }

    [Fact]
    public void Repair_EscapedBacktickInTemplate_DoesNotCloseEarly()
    {
        // `\`` is a literal backtick inside the template — must not be
        // mistaken for the closing one. Template needs a real close.
        Assert.Equal("`", Repair("SELECT `pre \\` mid"));
    }
}
