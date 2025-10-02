using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for the new string scalar functions: upper, lower, trim, ltrim, rtrim,
/// contains, starts_with, ends_with, position, replace, concat, repeat, reverse,
/// left, right, lpad, and rpad.
/// </summary>
public class StringFunctionTests
{
    // ───────────────── UpperFunction ─────────────────

    [Fact]
    public void UpperFunction_ConvertsToUppercase()
    {
        UpperFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal("HELLO", result.AsString());
    }

    [Fact]
    public void UpperFunction_NullInput_ReturnsNull()
    {
        UpperFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── LowerFunction ─────────────────

    [Fact]
    public void LowerFunction_ConvertsToLowercase()
    {
        LowerFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("HELLO")]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void LowerFunction_NullInput_ReturnsNull()
    {
        LowerFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── TrimFunction ─────────────────

    [Fact]
    public void TrimFunction_TrimsWhitespace()
    {
        TrimFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("  hello  ")]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void TrimFunction_WithCharSet_TrimsSpecifiedChars()
    {
        TrimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("yxTomxx"),
            DataValue.FromString("xyz")
        ]);
        Assert.Equal("Tom", result.AsString());
    }

    [Fact]
    public void TrimFunction_NullInput_ReturnsNull()
    {
        TrimFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── LtrimFunction ─────────────────

    [Fact]
    public void LtrimFunction_TrimsLeading()
    {
        LtrimFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("  hello  ")]);
        Assert.Equal("hello  ", result.AsString());
    }

    [Fact]
    public void LtrimFunction_WithCharSet_TrimsSpecifiedChars()
    {
        LtrimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("zzzytest"),
            DataValue.FromString("xyz")
        ]);
        Assert.Equal("test", result.AsString());
    }

    [Fact]
    public void LtrimFunction_NullInput_ReturnsNull()
    {
        LtrimFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RtrimFunction ─────────────────

    [Fact]
    public void RtrimFunction_TrimsTrailing()
    {
        RtrimFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("  hello  ")]);
        Assert.Equal("  hello", result.AsString());
    }

    [Fact]
    public void RtrimFunction_WithCharSet_TrimsSpecifiedChars()
    {
        RtrimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("testxxzx"),
            DataValue.FromString("xyz")
        ]);
        Assert.Equal("test", result.AsString());
    }

    [Fact]
    public void RtrimFunction_NullInput_ReturnsNull()
    {
        RtrimFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ContainsFunction ─────────────────

    [Fact]
    public void ContainsFunction_Found_ReturnsTrue()
    {
        ContainsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello world"),
            DataValue.FromString("world")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ContainsFunction_NotFound_ReturnsFalse()
    {
        ContainsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("xyz")
        ]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void ContainsFunction_NullInput_ReturnsNull()
    {
        ContainsFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("world")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── StartsWithFunction ─────────────────

    [Fact]
    public void StartsWithFunction_Match_ReturnsTrue()
    {
        StartsWithFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("hel")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StartsWithFunction_NoMatch_ReturnsFalse()
    {
        StartsWithFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("xyz")
        ]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void StartsWithFunction_NullInput_ReturnsNull()
    {
        StartsWithFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("hel")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── EndsWithFunction ─────────────────

    [Fact]
    public void EndsWithFunction_Match_ReturnsTrue()
    {
        EndsWithFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("llo")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void EndsWithFunction_NullInput_ReturnsNull()
    {
        EndsWithFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("llo")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── PositionFunction ─────────────────

    [Fact]
    public void PositionFunction_Found_ReturnsIndex()
    {
        PositionFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("ll")
        ]);
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void PositionFunction_NotFound_ReturnsZero()
    {
        PositionFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("xyz")
        ]);
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void PositionFunction_NullInput_ReturnsNull()
    {
        PositionFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("ll")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ReplaceFunction ─────────────────

    [Fact]
    public void ReplaceFunction_ReplacesAll()
    {
        ReplaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("aabaa"),
            DataValue.FromString("aa"),
            DataValue.FromString("x")
        ]);
        Assert.Equal("xbx", result.AsString());
    }

    [Fact]
    public void ReplaceFunction_NullInput_ReturnsNull()
    {
        ReplaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("aa"),
            DataValue.FromString("x")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ConcatFunction ─────────────────

    [Fact]
    public void ConcatFunction_JoinsStrings()
    {
        ConcatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString(" "),
            DataValue.FromString("world")
        ]);
        Assert.Equal("hello world", result.AsString());
    }

    [Fact]
    public void ConcatFunction_NullArgTreatedAsEmpty()
    {
        ConcatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.Null(DataKind.String),
            DataValue.FromString("!")
        ]);
        Assert.Equal("hi!", result.AsString());
    }

    // ───────────────── RepeatFunction ─────────────────

    [Fact]
    public void RepeatFunction_Repeats()
    {
        RepeatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("ab"),
            DataValue.FromFloat32(3)
        ]);
        Assert.Equal("ababab", result.AsString());
    }

    [Fact]
    public void RepeatFunction_NullInput_ReturnsNull()
    {
        RepeatFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromFloat32(3)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ReverseFunction ─────────────────

    [Fact]
    public void ReverseFunction_Reverses()
    {
        ReverseFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal("olleh", result.AsString());
    }

    [Fact]
    public void ReverseFunction_NullInput_ReturnsNull()
    {
        ReverseFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── LeftFunction ─────────────────

    [Fact]
    public void LeftFunction_ReturnsPrefix()
    {
        LeftFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromFloat32(3)
        ]);
        Assert.Equal("hel", result.AsString());
    }

    [Fact]
    public void LeftFunction_NegativeN_ReturnsAllButLastN()
    {
        LeftFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abcde"),
            DataValue.FromFloat32(-2)
        ]);
        Assert.Equal("abc", result.AsString());
    }

    [Fact]
    public void LeftFunction_NegativeN_ExceedsLength_ReturnsEmpty()
    {
        LeftFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("ab"),
            DataValue.FromFloat32(-5)
        ]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void LeftFunction_NullInput_ReturnsNull()
    {
        LeftFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromFloat32(3)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RightFunction ─────────────────

    [Fact]
    public void RightFunction_ReturnsSuffix()
    {
        RightFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromFloat32(3)
        ]);
        Assert.Equal("llo", result.AsString());
    }

    [Fact]
    public void RightFunction_NegativeN_ReturnsAllButFirstN()
    {
        RightFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abcde"),
            DataValue.FromFloat32(-2)
        ]);
        Assert.Equal("cde", result.AsString());
    }

    [Fact]
    public void RightFunction_NegativeN_ExceedsLength_ReturnsEmpty()
    {
        RightFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("ab"),
            DataValue.FromFloat32(-5)
        ]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void RightFunction_NullInput_ReturnsNull()
    {
        RightFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromFloat32(3)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── LpadFunction ─────────────────

    [Fact]
    public void LpadFunction_PadsLeft()
    {
        LpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.FromFloat32(5),
            DataValue.FromString("x")
        ]);
        Assert.Equal("xxxhi", result.AsString());
    }

    [Fact]
    public void LpadFunction_DefaultFill_PadsWithSpaces()
    {
        LpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.FromFloat32(5)
        ]);
        Assert.Equal("   hi", result.AsString());
    }

    [Fact]
    public void LpadFunction_MultiFill_CyclesPadding()
    {
        LpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.FromFloat32(5),
            DataValue.FromString("xy")
        ]);
        Assert.Equal("xyxhi", result.AsString());
    }

    [Fact]
    public void LpadFunction_NullInput_ReturnsNull()
    {
        LpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromFloat32(5),
            DataValue.FromString("x")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RpadFunction ─────────────────

    [Fact]
    public void RpadFunction_PadsRight()
    {
        RpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.FromFloat32(5),
            DataValue.FromString("x")
        ]);
        Assert.Equal("hixxx", result.AsString());
    }

    [Fact]
    public void RpadFunction_DefaultFill_PadsWithSpaces()
    {
        RpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.FromFloat32(5)
        ]);
        Assert.Equal("hi   ", result.AsString());
    }

    [Fact]
    public void RpadFunction_MultiFill_CyclesPadding()
    {
        RpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hi"),
            DataValue.FromFloat32(5),
            DataValue.FromString("xy")
        ]);
        Assert.Equal("hixyx", result.AsString());
    }

    [Fact]
    public void RpadFunction_NullInput_ReturnsNull()
    {
        RpadFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromFloat32(5),
            DataValue.FromString("x")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── Length aliases ─────────────────

    [Fact]
    public void LengthAlias_RegisteredInRegistry()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.Contains("length", registry.ScalarFunctionNames);
        Assert.Contains("char_length", registry.ScalarFunctionNames);
        Assert.Contains("character_length", registry.ScalarFunctionNames);
    }

    // ───────────────── RegexpExtractFunction ─────────────────

    [Fact]
    public void RegexpExtract_FullMatch()
    {
        RegexpExtractFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc123def"),
            DataValue.FromString("\\d+")
        ]);
        Assert.Equal("123", result.AsString());
    }

    [Fact]
    public void RegexpExtract_CaptureGroup()
    {
        RegexpExtractFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("2024-03-26"),
            DataValue.FromString("(\\d{4})-(\\d{2})-(\\d{2})"),
            DataValue.FromFloat32(2)
        ]);
        Assert.Equal("03", result.AsString());
    }

    [Fact]
    public void RegexpExtract_GroupZero_ReturnsFullMatch()
    {
        RegexpExtractFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello world"),
            DataValue.FromString("\\w+"),
            DataValue.FromFloat32(0)
        ]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void RegexpExtract_NoMatch_ReturnsNull()
    {
        RegexpExtractFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("\\d+")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RegexpExtract_NullInput_ReturnsNull()
    {
        RegexpExtractFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("\\d+")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RegexpExtract_NullPattern_ReturnsNull()
    {
        RegexpExtractFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.Null(DataKind.String)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RegexpExtract_GroupOutOfRange_Throws()
    {
        RegexpExtractFunction function = new();
        Assert.Throws<InvalidOperationException>(() =>
            function.Execute([
                DataValue.FromString("abc"),
                DataValue.FromString("(\\w+)"),
                DataValue.FromFloat32(5)
            ]));
    }

    [Fact]
    public void RegexpExtract_InvalidArgCount_Throws()
    {
        RegexpExtractFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String]));
    }

    // ───────────────── OverlayFunction ─────────────────

    [Fact]
    public void Overlay_ReplacesAtPosition()
    {
        OverlayFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Txxxxas"),
            DataValue.FromString("hom"),
            DataValue.FromFloat32(2),
            DataValue.FromFloat32(4)
        ]);
        Assert.Equal("Thomas", result.AsString());
    }

    [Fact]
    public void Overlay_DefaultCount_UsesReplacementLength()
    {
        OverlayFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abcdef"),
            DataValue.FromString("XY"),
            DataValue.FromFloat32(3)
        ]);
        Assert.Equal("abXYef", result.AsString());
    }

    [Fact]
    public void Overlay_NullInput_ReturnsNull()
    {
        OverlayFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("x"),
            DataValue.FromFloat32(1)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── StrposFunction ─────────────────

    [Fact]
    public void Strpos_Found_ReturnsIndex()
    {
        StrposFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("high"),
            DataValue.FromString("ig")
        ]);
        Assert.Equal(2, result.AsInt32());
    }

    [Fact]
    public void Strpos_NotFound_ReturnsZero()
    {
        StrposFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("xyz")
        ]);
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void Strpos_NullInput_ReturnsNull()
    {
        StrposFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("x")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── SubstrAlias ─────────────────

    [Fact]
    public void SubstrAlias_RegisteredInRegistry()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.Contains("substr", registry.ScalarFunctionNames);
    }

    // ───────────────── OctetLengthFunction ─────────────────

    [Fact]
    public void OctetLength_AsciiString()
    {
        OctetLengthFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("jose")]);
        Assert.Equal(4, result.AsInt32());
    }

    [Fact]
    public void OctetLength_Utf8String()
    {
        OctetLengthFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("josé")]);
        Assert.Equal(5, result.AsInt32()); // é is 2 bytes in UTF-8
    }

    [Fact]
    public void OctetLength_NullInput_ReturnsNull()
    {
        OctetLengthFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── BitLengthFunction ─────────────────

    [Fact]
    public void BitLength_AsciiString()
    {
        BitLengthFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("jose")]);
        Assert.Equal(32, result.AsInt32()); // 4 bytes * 8
    }

    [Fact]
    public void BitLength_Utf8String()
    {
        BitLengthFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("josé")]);
        Assert.Equal(40, result.AsInt32()); // 5 bytes * 8
    }

    [Fact]
    public void BitLength_NullInput_ReturnsNull()
    {
        BitLengthFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }
}
