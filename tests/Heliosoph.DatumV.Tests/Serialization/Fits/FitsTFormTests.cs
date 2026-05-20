using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Fits;

namespace Heliosoph.DatumV.Tests.Serialization.Fits;

/// <summary>
/// Unit tests for <see cref="FitsTForm.Parse"/> covering the
/// <c>rTa</c>-syntax inputs the BINTABLE reader produces in v1 — every
/// supported scalar type, repeat-shaped array forms, the special
/// <c>A</c> field-width interpretation, and the explicit refusal of
/// type codes that aren't ready yet (P/Q variable-length, C/M complex,
/// X bit).
/// </summary>
public sealed class FitsTFormTests
{
    [Theory]
    [InlineData("L", 'L', 1, DataKind.Boolean, 1)]
    [InlineData("B", 'B', 1, DataKind.UInt8, 1)]
    [InlineData("I", 'I', 1, DataKind.Int16, 2)]
    [InlineData("J", 'J', 1, DataKind.Int32, 4)]
    [InlineData("K", 'K', 1, DataKind.Int64, 8)]
    [InlineData("E", 'E', 1, DataKind.Float32, 4)]
    [InlineData("D", 'D', 1, DataKind.Float64, 8)]
    public void Parse_BareTypeChar_DefaultsRepeatToOne(
        string tform, char expectedType, int expectedRepeat, DataKind expectedKind, int expectedElementBytes)
    {
        FitsTForm form = FitsTForm.Parse(tform);
        Assert.Equal(expectedType, form.TypeChar);
        Assert.Equal(expectedRepeat, form.Repeat);
        Assert.Equal(expectedKind, form.ElementKind);
        Assert.Equal(expectedElementBytes, form.ElementByteSize);
        Assert.False(form.IsArray);
    }

    [Fact]
    public void Parse_RepeatedNumericType_MarksAsArrayWithRowByteSize()
    {
        FitsTForm form = FitsTForm.Parse("3J");
        Assert.Equal('J', form.TypeChar);
        Assert.Equal(3, form.Repeat);
        Assert.True(form.IsArray);
        Assert.Equal(DataKind.Int32, form.ElementKind);
        Assert.Equal(4 * 3, form.RowByteSize);
    }

    [Fact]
    public void Parse_AString_RepeatIsFieldWidthNotArrayLength()
    {
        // "8A" = an 8-byte string field, NOT an array of 8 single-char strings.
        FitsTForm form = FitsTForm.Parse("8A");
        Assert.Equal('A', form.TypeChar);
        Assert.Equal(8, form.Repeat);
        Assert.False(form.IsArray);
        Assert.Equal(8, form.StringByteWidth);
        Assert.Equal(8, form.RowByteSize);
    }

    [Fact]
    public void Parse_LowercaseInRepeat_StillWorksAfterTrim()
    {
        // FITS headers are conventionally padded with spaces; the parser
        // should tolerate that.
        FitsTForm form = FitsTForm.Parse("  1E   ");
        Assert.Equal('E', form.TypeChar);
        Assert.Equal(1, form.Repeat);
    }

    [Theory]
    [InlineData("PE(99)")]   // variable-length array of Float32, heap-backed
    [InlineData("PQ")]
    [InlineData("QE")]
    public void Parse_VariableLengthArrayTypes_Throw(string tform)
    {
        Assert.Throws<NotSupportedException>(() => FitsTForm.Parse(tform));
    }

    [Theory]
    [InlineData("C")]   // complex single-precision
    [InlineData("M")]   // complex double-precision
    [InlineData("X")]   // bit array
    public void Parse_UnsupportedV1Types_Throw(string tform)
    {
        Assert.Throws<NotSupportedException>(() => FitsTForm.Parse(tform));
    }

    [Theory]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("Z")]
    public void Parse_Malformed_ThrowsArgumentOrFormat(string tform)
    {
        Assert.ThrowsAny<Exception>(() => FitsTForm.Parse(tform));
    }

    [Fact]
    public void Parse_ZeroRepeat_Throws()
    {
        Assert.Throws<FormatException>(() => FitsTForm.Parse("0J"));
    }
}
