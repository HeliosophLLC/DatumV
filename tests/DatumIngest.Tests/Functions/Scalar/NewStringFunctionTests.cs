using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for the new PostgreSQL-aligned string functions: concat_ws, split_part,
/// initcap, regexp_replace, translate, ascii, chr, and btrim.
/// </summary>
public class NewStringFunctionTests : ServiceTestBase
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
        Assert.Equal(65, result.AsInt32());
    }

    [Fact]
    public void Ascii_MultiCharString_ReturnsFirst()
    {
        AsciiFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(104, result.AsInt32());
    }

    [Fact]
    public void Ascii_EmptyString_ReturnsZero()
    {
        AsciiFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("")]);
        Assert.Equal(0, result.AsInt32());
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
        Assert.Throws<FunctionArgumentException>(() =>
            new AsciiFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Ascii_Validate_NonString_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() =>
            new AsciiFunction().ValidateArguments([DataKind.Int32]));
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
        Assert.Throws<FunctionArgumentException>(() =>
            new ChrFunction().ValidateArguments([DataKind.Int32, DataKind.Int32]));
    }

    [Fact]
    public void Chr_Validate_NonNumeric_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() =>
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
        Assert.Equal(4, result.AsInt32());
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
        Assert.Equal(3, result.AsInt32());
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
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void RegexpCount_NoMatch_ReturnsZero()
    {
        RegexpCountFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("\\d+")
        ]);
        Assert.Equal(0, result.AsInt32());
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
        Assert.Equal(3, result.AsInt32());
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
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void RegexpInstr_NoMatch_ReturnsZero()
    {
        RegexpInstrFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromString("\\d+")
        ]);
        Assert.Equal(0, result.AsInt32());
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

    // ───────────────── FormatFunction ─────────────────

    [Fact]
    public void Format_BasicSubstitution()
    {
        FormatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Hello %s"),
            DataValue.FromString("World")
        ]);
        Assert.Equal("Hello World", result.AsString());
    }

    [Fact]
    public void Format_Positional()
    {
        FormatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Hello %s, %1$s"),
            DataValue.FromString("World")
        ]);
        Assert.Equal("Hello World, World", result.AsString());
    }

    [Fact]
    public void Format_EscapedPercent()
    {
        FormatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("100%%")
        ]);
        Assert.Equal("100%", result.AsString());
    }

    [Fact]
    public void Format_IdentQuoting()
    {
        FormatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("SELECT * FROM %I"),
            DataValue.FromString("my table")
        ]);
        Assert.Equal("SELECT * FROM \"my table\"", result.AsString());
    }

    [Fact]
    public void Format_LiteralQuoting()
    {
        FormatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("WHERE name = %L"),
            DataValue.FromString("O'Reilly")
        ]);
        Assert.Equal("WHERE name = 'O''Reilly'", result.AsString());
    }

    [Fact]
    public void Format_NullInput_ReturnsNull()
    {
        FormatFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── StringToArrayFunction ─────────────────

    [Fact]
    public void StringToArray_BasicSplit()
    {
        StringToArrayFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("a,b,c"),
            DataValue.FromString(",")
        ]);
        DataValue[] arr = result.AsArray();
        Assert.Equal(3, arr.Length);
        Assert.Equal("a", arr[0].AsString());
        Assert.Equal("b", arr[1].AsString());
        Assert.Equal("c", arr[2].AsString());
    }

    [Fact]
    public void StringToArray_WithNullString()
    {
        StringToArrayFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("xx~~yy~~zz"),
            DataValue.FromString("~~"),
            DataValue.FromString("yy")
        ]);
        DataValue[] arr = result.AsArray();
        Assert.Equal(3, arr.Length);
        Assert.Equal("xx", arr[0].AsString());
        Assert.True(arr[1].IsNull);
        Assert.Equal("zz", arr[2].AsString());
    }

    [Fact]
    public void StringToArray_NullDelimiter_SplitsChars()
    {
        StringToArrayFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc"),
            DataValue.Null(DataKind.String)
        ]);
        DataValue[] arr = result.AsArray();
        Assert.Equal(3, arr.Length);
        Assert.Equal("a", arr[0].AsString());
        Assert.Equal("b", arr[1].AsString());
        Assert.Equal("c", arr[2].AsString());
    }

    // ───────────────── RegexpSplitToArrayFunction ─────────────────

    [Fact]
    public void RegexpSplitToArray_BasicSplit()
    {
        RegexpSplitToArrayFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello world"),
            DataValue.FromString("\\s+")
        ]);
        DataValue[] arr = result.AsArray();
        Assert.Equal(2, arr.Length);
        Assert.Equal("hello", arr[0].AsString());
        Assert.Equal("world", arr[1].AsString());
    }

    [Fact]
    public void RegexpSplitToArray_NullInput_ReturnsNull()
    {
        RegexpSplitToArrayFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("\\s+")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ToHexFunction ─────────────────

    [Fact]
    public void ToHex_ConvertsToHex()
    {
        ToHexFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(255)]);
        Assert.Equal("ff", result.AsString());
    }

    [Fact]
    public void ToHex_NullInput_ReturnsNull()
    {
        ToHexFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ToBinFunction ─────────────────

    [Fact]
    public void ToBin_ConvertsToBinary()
    {
        ToBinFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(10)]);
        Assert.Equal("1010", result.AsString());
    }

    [Fact]
    public void ToBin_NullInput_ReturnsNull()
    {
        ToBinFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ToOctFunction ─────────────────

    [Fact]
    public void ToOct_ConvertsToOctal()
    {
        ToOctFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(8)]);
        Assert.Equal("10", result.AsString());
    }

    [Fact]
    public void ToOct_NullInput_ReturnsNull()
    {
        ToOctFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── ToAsciiFunction ─────────────────

    [Fact]
    public void ToAscii_RemovesDiacritics()
    {
        ToAsciiFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("Karél")]);
        Assert.Equal("Karel", result.AsString());
    }

    [Fact]
    public void ToAscii_AsciiUnchanged()
    {
        ToAsciiFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void ToAscii_NullInput_ReturnsNull()
    {
        ToAsciiFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── UnistrFunction ─────────────────

    [Fact]
    public void Unistr_FourDigitEscape()
    {
        UnistrFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("d\\0061t\\0061")]);
        Assert.Equal("data", result.AsString());
    }

    [Fact]
    public void Unistr_UnicodeU_Escape()
    {
        UnistrFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("d\\u0061t\\u0061")]);
        Assert.Equal("data", result.AsString());
    }

    [Fact]
    public void Unistr_EscapedBackslash()
    {
        UnistrFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("a\\\\b")]);
        Assert.Equal("a\\b", result.AsString());
    }

    [Fact]
    public void Unistr_NullInput_ReturnsNull()
    {
        UnistrFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── CasefoldFunction ─────────────────

    [Fact]
    public void Casefold_FoldsToLower()
    {
        CasefoldFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("Hello WORLD")]);
        Assert.Equal("hello world", result.AsString());
    }

    [Fact]
    public void Casefold_NullInput_ReturnsNull()
    {
        CasefoldFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── QuoteIdentFunction ─────────────────

    [Fact]
    public void QuoteIdent_SimpleIdentifier_NoQuotes()
    {
        QuoteIdentFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("foo_bar")]);
        Assert.Equal("foo_bar", result.AsString());
    }

    [Fact]
    public void QuoteIdent_SpecialChars_AddQuotes()
    {
        QuoteIdentFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("Foo bar")]);
        Assert.Equal("\"Foo bar\"", result.AsString());
    }

    [Fact]
    public void QuoteIdent_EscapesDoubleQuotes()
    {
        QuoteIdentFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("has\"quote")]);
        Assert.Equal("\"has\"\"quote\"", result.AsString());
    }

    // ───────────────── QuoteLiteralFunction ─────────────────

    [Fact]
    public void QuoteLiteral_QuotesString()
    {
        QuoteLiteralFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("O'Reilly")]);
        Assert.Equal("'O''Reilly'", result.AsString());
    }

    [Fact]
    public void QuoteLiteral_NullInput_ReturnsNull()
    {
        QuoteLiteralFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── QuoteNullableFunction ─────────────────

    [Fact]
    public void QuoteNullable_NullInput_ReturnsNullString()
    {
        QuoteNullableFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.Equal("NULL", result.AsString());
    }

    [Fact]
    public void QuoteNullable_NonNull_QuotesLikeQuoteLiteral()
    {
        QuoteNullableFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal("'hello'", result.AsString());
    }

    // ───────────────── ParseIdentFunction ─────────────────

    [Fact]
    public void ParseIdent_QuotedIdentifier()
    {
        ParseIdentFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("\"SomeSchema\".sometable")
        ]);
        DataValue[] arr = result.AsArray();
        Assert.Equal(2, arr.Length);
        Assert.Equal("SomeSchema", arr[0].AsString());
        Assert.Equal("sometable", arr[1].AsString());
    }

    [Fact]
    public void ParseIdent_UnquotedFoldsToLower()
    {
        ParseIdentFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("MyTable")
        ]);
        DataValue[] arr = result.AsArray();
        Assert.Single(arr);
        Assert.Equal("mytable", arr[0].AsString());
    }

    [Fact]
    public void ParseIdent_NullInput_ReturnsNull()
    {
        ParseIdentFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── UnicodeNormalizeFunction ─────────────────

    [Fact]
    public void Normalize_DefaultNFC()
    {
        UnicodeNormalizeFunction function = new();
        // U+0061 (a) + U+0308 (combining diaeresis) → U+00E4 (ä) in NFC
        string input = "a\u0308";
        DataValue result = function.Execute([DataValue.FromString(input)]);
        Assert.Equal("\u00E4", result.AsString());
    }

    [Fact]
    public void Normalize_NFD()
    {
        UnicodeNormalizeFunction function = new();
        // ä (U+00E4) → a + combining diaeresis in NFD
        DataValue result = function.Execute([
            DataValue.FromString("\u00E4"),
            DataValue.FromString("NFD")
        ]);
        Assert.Equal("a\u0308", result.AsString());
    }

    [Fact]
    public void Normalize_NullInput_ReturnsNull()
    {
        UnicodeNormalizeFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Normalize_InvalidForm_Throws()
    {
        UnicodeNormalizeFunction function = new();
        Assert.Throws<InvalidOperationException>(() =>
            function.Execute([
                DataValue.FromString("hello"),
                DataValue.FromString("INVALID")
            ]));
    }
}
