using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for the new PostgreSQL-aligned string functions: concat_ws, split_part,
/// initcap, regexp_replace, translate, ascii, chr, and btrim.
/// </summary>
public class NewStringFunctionTests
{
    // ───────────────── ConcatWsFunction ─────────────────

    [Fact]
    public void ConcatWs_JoinsWithSeparator()
    {
        ConcatWsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString(", "),
            DataValue.FromString("alpha"),
            DataValue.FromString("beta"),
            DataValue.FromString("gamma")
        ]);
        Assert.Equal("alpha, beta, gamma", result.AsString());
    }

    [Fact]
    public void ConcatWs_SkipsNulls()
    {
        ConcatWsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("-"),
            DataValue.FromString("a"),
            DataValue.Null(DataKind.String),
            DataValue.FromString("c")
        ]);
        Assert.Equal("a-c", result.AsString());
    }

    [Fact]
    public void ConcatWs_NullSeparator_ReturnsNull()
    {
        ConcatWsFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("a"),
            DataValue.FromString("b")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ConcatWs_AllNullValues_ReturnsEmpty()
    {
        ConcatWsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString(","),
            DataValue.Null(DataKind.String),
            DataValue.Null(DataKind.String)
        ]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void ConcatWs_SingleValue_NoSeparator()
    {
        ConcatWsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString(","),
            DataValue.FromString("only")
        ]);
        Assert.Equal("only", result.AsString());
    }

    [Fact]
    public void ConcatWs_Validate_TooFewArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConcatWsFunction().ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void ConcatWs_Validate_NonString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConcatWsFunction().ValidateArguments([DataKind.String, DataKind.Float32]));
    }

    // ───────────────── SplitPartFunction ─────────────────

    [Fact]
    public void SplitPart_ExtractsSecondPart()
    {
        SplitPartFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("a.b.c"),
            DataValue.FromString("."),
            DataValue.FromFloat32(2)
        ]);
        Assert.Equal("b", result.AsString());
    }

    [Fact]
    public void SplitPart_FirstPart()
    {
        SplitPartFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("one::two::three"),
            DataValue.FromString("::"),
            DataValue.FromFloat32(1)
        ]);
        Assert.Equal("one", result.AsString());
    }

    [Fact]
    public void SplitPart_OutOfRange_ReturnsEmpty()
    {
        SplitPartFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("a.b"),
            DataValue.FromString("."),
            DataValue.FromFloat32(5)
        ]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void SplitPart_NegativeIndex_CountsFromEnd()
    {
        SplitPartFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("a.b.c"),
            DataValue.FromString("."),
            DataValue.FromFloat32(-1)
        ]);
        Assert.Equal("c", result.AsString());
    }

    [Fact]
    public void SplitPart_ZeroIndex_ReturnsEmpty()
    {
        SplitPartFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("a.b"),
            DataValue.FromString("."),
            DataValue.FromFloat32(0)
        ]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void SplitPart_NullInput_ReturnsNull()
    {
        SplitPartFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("."),
            DataValue.FromFloat32(1)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void SplitPart_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new SplitPartFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    // ───────────────── InitcapFunction ─────────────────

    [Fact]
    public void Initcap_CapitalizesWords()
    {
        InitcapFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello world")]);
        Assert.Equal("Hello World", result.AsString());
    }

    [Fact]
    public void Initcap_AllUppercase_ConvertsToTitleCase()
    {
        InitcapFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("HELLO WORLD")]);
        Assert.Equal("Hello World", result.AsString());
    }

    [Fact]
    public void Initcap_NonAlphanumericSeparators()
    {
        InitcapFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello-world_foo.bar")]);
        Assert.Equal("Hello-World_Foo.Bar", result.AsString());
    }

    [Fact]
    public void Initcap_NullInput_ReturnsNull()
    {
        InitcapFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Initcap_EmptyString_ReturnsEmpty()
    {
        InitcapFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("")]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void Initcap_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new InitcapFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Initcap_Validate_NonString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new InitcapFunction().ValidateArguments([DataKind.Float32]));
    }

    // ───────────────── RegexpReplaceFunction ─────────────────

    [Fact]
    public void RegexpReplace_GlobalByDefault()
    {
        RegexpReplaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc123def456"),
            DataValue.FromString("\\d+"),
            DataValue.FromString("NUM")
        ]);
        Assert.Equal("abcNUMdefNUM", result.AsString());
    }

    [Fact]
    public void RegexpReplace_FirstOnlyWithoutGFlag()
    {
        RegexpReplaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc123def456"),
            DataValue.FromString("\\d+"),
            DataValue.FromString("NUM"),
            DataValue.FromString("i")
        ]);
        Assert.Equal("abcNUMdef456", result.AsString());
    }

    [Fact]
    public void RegexpReplace_CaseInsensitive()
    {
        RegexpReplaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Hello hello HELLO"),
            DataValue.FromString("hello"),
            DataValue.FromString("hi"),
            DataValue.FromString("gi")
        ]);
        Assert.Equal("hi hi hi", result.AsString());
    }

    [Fact]
    public void RegexpReplace_NullInput_ReturnsNull()
    {
        RegexpReplaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("\\d+"),
            DataValue.FromString("x")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RegexpReplace_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new RegexpReplaceFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    // ───────────────── TranslateFunction ─────────────────

    [Fact]
    public void Translate_ReplacesCharacters()
    {
        TranslateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("12345"),
            DataValue.FromString("143"),
            DataValue.FromString("ax")
        ]);
        Assert.Equal("a2x5", result.AsString());
    }

    [Fact]
    public void Translate_DeletesUnmappedFromChars()
    {
        TranslateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("elo"),
            DataValue.FromString("a")
        ]);
        Assert.Equal("ha", result.AsString());
    }

    [Fact]
    public void Translate_NoMatchingChars_ReturnsOriginal()
    {
        TranslateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("xyz"),
            DataValue.FromString("abc")
        ]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Translate_NullInput_ReturnsNull()
    {
        TranslateFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("a"),
            DataValue.FromString("b")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Translate_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new TranslateFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    // ───────────────── AsciiFunction ─────────────────

    [Fact]
    public void Ascii_ReturnsFirstCharCode()
    {
        AsciiFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("A")]);
        Assert.Equal(65f, result.AsFloat32());
    }

    [Fact]
    public void Ascii_MultiCharString_ReturnsFirst()
    {
        AsciiFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(104f, result.AsFloat32());
    }

    [Fact]
    public void Ascii_EmptyString_ReturnsZero()
    {
        AsciiFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("")]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void Ascii_NullInput_ReturnsNull()
    {
        AsciiFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Ascii_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new AsciiFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Ascii_Validate_NonString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new AsciiFunction().ValidateArguments([DataKind.Float32]));
    }

    // ───────────────── ChrFunction ─────────────────

    [Fact]
    public void Chr_ReturnsCharacter()
    {
        ChrFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(65)]);
        Assert.Equal("A", result.AsString());
    }

    [Fact]
    public void Chr_UInt8_Works()
    {
        ChrFunction function = new();
        DataValue result = function.Execute([DataValue.FromUInt8(48)]);
        Assert.Equal("0", result.AsString());
    }

    [Fact]
    public void Chr_NullInput_ReturnsNull()
    {
        ChrFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Chr_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChrFunction().ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void Chr_Validate_NonNumeric_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChrFunction().ValidateArguments([DataKind.String]));
    }

    // ───────────────── BtrimFunction ─────────────────

    [Fact]
    public void Btrim_NoCharSet_TrimsWhitespace()
    {
        BtrimFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("  hello  ")]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Btrim_WithCharSet_TrimsSpecifiedChars()
    {
        BtrimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("xyxtrimyyx"),
            DataValue.FromString("xy")
        ]);
        Assert.Equal("trim", result.AsString());
    }

    [Fact]
    public void Btrim_CharSetNoMatch_ReturnsOriginal()
    {
        BtrimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("xyz")
        ]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Btrim_NullInput_ReturnsNull()
    {
        BtrimFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Btrim_NullCharSet_TrimsWhitespace()
    {
        BtrimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("  hello  "),
            DataValue.Null(DataKind.String)
        ]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Btrim_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new BtrimFunction().ValidateArguments([DataKind.String, DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Btrim_Validate_NonString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new BtrimFunction().ValidateArguments([DataKind.Float32]));
    }

    // ───────────────── RegexpCountFunction ─────────────────

    [Fact]
    public void RegexpCount_CountsAllMatches()
    {
        RegexpCountFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("123456789012"),
            DataValue.FromString("\\d\\d\\d")
        ]);
        Assert.Equal(4f, result.AsFloat32());
    }

    [Fact]
    public void RegexpCount_WithStart()
    {
        RegexpCountFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("123456789012"),
            DataValue.FromString("\\d\\d\\d"),
            DataValue.FromFloat32(2)
        ]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void RegexpCount_CaseInsensitive()
    {
        RegexpCountFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Hello hello HELLO"),
            DataValue.FromString("hello"),
            DataValue.FromFloat32(1),
            DataValue.FromString("i")
        ]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void RegexpCount_NoMatch_ReturnsZero()
    {
        RegexpCountFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("\\d+")
        ]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void RegexpCount_NullInput_ReturnsNull()
    {
        RegexpCountFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("\\d+")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RegexpLikeFunction ─────────────────

    [Fact]
    public void RegexpLike_Match_ReturnsTrue()
    {
        RegexpLikeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Hello World"),
            DataValue.FromString("world$"),
            DataValue.FromString("i")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void RegexpLike_NoMatch_ReturnsFalse()
    {
        RegexpLikeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Hello World"),
            DataValue.FromString("^world")
        ]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void RegexpLike_NullInput_ReturnsNull()
    {
        RegexpLikeFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("\\d+")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RegexpMatchFunction ─────────────────

    [Fact]
    public void RegexpMatch_WithCaptureGroups()
    {
        RegexpMatchFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("foobarbequebaz"),
            DataValue.FromString("(bar)(beque)")
        ]);
        Assert.False(result.IsNull);
        DataValue[] array = result.AsArray();
        Assert.Equal(2, array.Length);
        Assert.Equal("bar", array[0].AsString());
        Assert.Equal("beque", array[1].AsString());
    }

    [Fact]
    public void RegexpMatch_NoCaptureGroups_ReturnsWholeMatch()
    {
        RegexpMatchFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc123def"),
            DataValue.FromString("\\d+")
        ]);
        Assert.False(result.IsNull);
        DataValue[] array = result.AsArray();
        Assert.Single(array);
        Assert.Equal("123", array[0].AsString());
    }

    [Fact]
    public void RegexpMatch_NoMatch_ReturnsNull()
    {
        RegexpMatchFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("\\d+")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RegexpSubstrFunction ─────────────────

    [Fact]
    public void RegexpSubstr_FirstMatch()
    {
        RegexpSubstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc123def456"),
            DataValue.FromString("\\d+")
        ]);
        Assert.Equal("123", result.AsString());
    }

    [Fact]
    public void RegexpSubstr_SecondMatch()
    {
        RegexpSubstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc123def456"),
            DataValue.FromString("\\d+"),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(2)
        ]);
        Assert.Equal("456", result.AsString());
    }

    [Fact]
    public void RegexpSubstr_WithSubexpr()
    {
        RegexpSubstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("ABCDEF"),
            DataValue.FromString("c(.)(..)"),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(1),
            DataValue.FromString("i"),
            DataValue.FromFloat32(2)
        ]);
        Assert.Equal("EF", result.AsString());
    }

    [Fact]
    public void RegexpSubstr_NoMatch_ReturnsNull()
    {
        RegexpSubstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("\\d+")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── RegexpInstrFunction ─────────────────

    [Fact]
    public void RegexpInstr_FindsPosition()
    {
        RegexpInstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("ABCDEF"),
            DataValue.FromString("c(.)(..)"),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(0),
            DataValue.FromString("i")
        ]);
        Assert.Equal(3f, result.AsFloat32());
    }

    [Fact]
    public void RegexpInstr_WithSubexpr()
    {
        RegexpInstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("ABCDEF"),
            DataValue.FromString("c(.)(..)"),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(1),
            DataValue.FromFloat32(0),
            DataValue.FromString("i"),
            DataValue.FromFloat32(2)
        ]);
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void RegexpInstr_NoMatch_ReturnsZero()
    {
        RegexpInstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("\\d+")
        ]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void RegexpInstr_NullInput_ReturnsNull()
    {
        RegexpInstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("\\d+")
        ]);
        Assert.True(result.IsNull);
    }
}
