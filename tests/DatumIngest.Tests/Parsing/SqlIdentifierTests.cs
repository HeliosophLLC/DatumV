using Heliosoph.DatumV.Parsing.Tokens;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// Tests for <see cref="SqlIdentifier"/> quoting and unquoting utilities.
/// </summary>
public class SqlIdentifierTests : ServiceTestBase
{
    // ───────────────────── NeedsQuoting ─────────────────────

    [Theory]
    [InlineData("users")]
    [InlineData("my_table")]
    [InlineData("_private")]
    [InlineData("col123")]
    public void BareIdentifierDoesNotNeedQuoting(string name)
    {
        Assert.False(SqlIdentifier.NeedsQuoting(name));
    }

    [Theory]
    [InlineData("adult.data")]
    [InlineData("my table")]
    [InlineData("train-set")]
    [InlineData("123start")]
    [InlineData("")]
    public void SpecialCharactersNeedQuoting(string name)
    {
        Assert.True(SqlIdentifier.NeedsQuoting(name));
    }

    [Theory]
    [InlineData("SELECT")]
    [InlineData("select")]
    [InlineData("from")]
    [InlineData("FROM")]
    [InlineData("WHERE")]
    [InlineData("order")]
    [InlineData("IN")]
    [InlineData("NULL")]
    [InlineData("TRUE")]
    [InlineData("Cast")]
    public void ReservedKeywordsNeedQuoting(string name)
    {
        Assert.True(SqlIdentifier.NeedsQuoting(name));
    }

    // ───────────────────── QuoteIfNeeded ─────────────────────

    [Fact]
    public void QuoteIfNeededReturnsBareNameWhenSafe()
    {
        Assert.Equal("users", SqlIdentifier.QuoteIfNeeded("users"));
    }

    [Fact]
    public void QuoteIfNeededDoubleQuotesDottedName()
    {
        Assert.Equal("\"adult.data\"", SqlIdentifier.QuoteIfNeeded("adult.data"));
    }

    [Fact]
    public void QuoteIfNeededDoubleQuotesKeyword()
    {
        Assert.Equal("\"order\"", SqlIdentifier.QuoteIfNeeded("order"));
    }

    [Fact]
    public void QuoteIfNeededEscapesEmbeddedDoubleQuotes()
    {
        Assert.Equal("\"col\"\"name\"", SqlIdentifier.QuoteIfNeeded("col\"name"));
    }

    // ───────────────────── QuoteQualifiedIfNeeded ─────────────────────

    [Fact]
    public void QuoteQualifiedIfNeededLeavesBareDottedPathUnchanged()
    {
        Assert.Equal("public.users", SqlIdentifier.QuoteQualifiedIfNeeded("public.users"));
    }

    [Fact]
    public void QuoteQualifiedIfNeededQuotesOnlySegmentsThatNeedIt()
    {
        Assert.Equal("public.\"order\"", SqlIdentifier.QuoteQualifiedIfNeeded("public.order"));
    }

    [Fact]
    public void QuoteQualifiedIfNeededRoutesUnqualifiedThroughQuoteIfNeeded()
    {
        Assert.Equal("users", SqlIdentifier.QuoteQualifiedIfNeeded("users"));
        Assert.Equal("\"order\"", SqlIdentifier.QuoteQualifiedIfNeeded("order"));
    }

    // ───────────────────── Unquote ─────────────────────

    [Theory]
    [InlineData("\"adult.data\"", "adult.data")]
    [InlineData("\"col\"\"name\"", "col\"name")]
    public void UnquoteStripsDoubleQuotes(string quoted, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.Unquote(quoted));
    }

    [Theory]
    [InlineData("'adult.data'", "adult.data")]
    [InlineData("'it''s'", "it's")]
    public void UnquoteStripsSingleQuotes(string quoted, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.Unquote(quoted));
    }

    [Fact]
    public void UnquoteReturnsBareNameUnchanged()
    {
        Assert.Equal("users", SqlIdentifier.Unquote("users"));
    }
}
