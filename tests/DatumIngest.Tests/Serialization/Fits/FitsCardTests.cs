using System.Text;
using DatumIngest.Serialization.Fits;

namespace DatumIngest.Tests.Serialization.Fits;

/// <summary>
/// Unit tests for <see cref="FitsCard.Parse"/> covering the value-card and
/// comment-card forms: integer / float / quoted-string / logical values,
/// embedded-slash handling inside quoted strings, FITS' single-quote
/// doubling escape, the bare <c>END</c> sentinel, and <c>COMMENT</c> /
/// <c>HISTORY</c> rows.
/// </summary>
public sealed class FitsCardTests
{
    private static byte[] Card(string text) => Encoding.ASCII.GetBytes(text.PadRight(80));

    [Fact]
    public void Parse_IntegerValueCard_ExtractsKeywordAndValue()
    {
        FitsCard card = FitsCard.Parse(Card("BITPIX  =                   16"));
        Assert.Equal("BITPIX", card.Keyword);
        Assert.Equal("16", card.RawValue);
        Assert.Null(card.Comment);
        Assert.Equal(16, card.AsInt64());
    }

    [Fact]
    public void Parse_IntegerValueCardWithComment_SplitsValueAndComment()
    {
        FitsCard card = FitsCard.Parse(Card("NAXIS   =                    2 / number of axes"));
        Assert.Equal("NAXIS", card.Keyword);
        Assert.Equal("2", card.RawValue);
        Assert.Equal("number of axes", card.Comment);
        Assert.Equal(2, card.AsInt64());
    }

    [Fact]
    public void Parse_FloatValueCard_AsDoubleHandlesFitsExponent()
    {
        // FITS allows "D" as the exponent marker for double-precision values.
        FitsCard card = FitsCard.Parse(Card("BZERO   =          1.234567D03"));
        Assert.Equal("1.234567D03", card.RawValue);
        Assert.NotNull(card.AsDouble());
        Assert.Equal(1234.567, card.AsDouble()!.Value, 6);
    }

    [Fact]
    public void Parse_QuotedStringValue_StripsQuotesAndPadding()
    {
        FitsCard card = FitsCard.Parse(Card("EXTNAME = 'SCI     '           / extension name"));
        Assert.Equal("EXTNAME", card.Keyword);
        Assert.Equal("SCI", card.RawValue);
        Assert.Equal("extension name", card.Comment);
    }

    [Fact]
    public void Parse_QuotedStringWithEmbeddedSlash_DoesNotTreatSlashAsCommentMarker()
    {
        // The / inside the quotes is part of the value; the comment starts
        // at the / AFTER the closing quote.
        FitsCard card = FitsCard.Parse(Card("ORIGIN  = 'foo/bar  '          / catalog code"));
        Assert.Equal("foo/bar", card.RawValue);
        Assert.Equal("catalog code", card.Comment);
    }

    [Fact]
    public void Parse_QuotedStringWithEscapedQuote_UndoesDoubling()
    {
        // FITS standard: a literal single quote inside a string is written
        // as two consecutive single quotes.
        FitsCard card = FitsCard.Parse(Card("OBJECT  = 'O''Brien   '"));
        Assert.Equal("O'Brien", card.RawValue);
    }

    [Fact]
    public void Parse_LogicalValueCard_AsBooleanReturnsTrueOrFalse()
    {
        FitsCard simpleT = FitsCard.Parse(Card("SIMPLE  =                    T"));
        Assert.True(simpleT.AsBoolean());

        FitsCard extendF = FitsCard.Parse(Card("EXTEND  =                    F"));
        Assert.False(extendF.AsBoolean());
    }

    [Fact]
    public void Parse_EndCard_FlaggedByIsEnd()
    {
        FitsCard card = FitsCard.Parse(Card("END"));
        Assert.True(card.IsEnd);
        Assert.Null(card.RawValue);
    }

    [Fact]
    public void Parse_CommentCard_KeywordIsCommentAndBodyInComment()
    {
        FitsCard card = FitsCard.Parse(Card("COMMENT This is a header comment line."));
        Assert.Equal("COMMENT", card.Keyword);
        Assert.Null(card.RawValue);
        Assert.Equal("This is a header comment line.", card.Comment);
    }

    [Fact]
    public void Parse_HistoryCard_KeywordIsHistory()
    {
        FitsCard card = FitsCard.Parse(Card("HISTORY processed 2026-06-06"));
        Assert.Equal("HISTORY", card.Keyword);
        Assert.Equal("processed 2026-06-06", card.Comment);
    }

    [Fact]
    public void Parse_WrongLength_Throws()
    {
        byte[] tooShort = Encoding.ASCII.GetBytes("BITPIX  =                   16");
        Assert.Throws<ArgumentException>(() => FitsCard.Parse(tooShort));
    }

    [Fact]
    public void AsDouble_OnNonNumeric_ReturnsNull()
    {
        FitsCard card = FitsCard.Parse(Card("XTENSION= 'IMAGE   '"));
        Assert.Null(card.AsDouble());
    }
}
