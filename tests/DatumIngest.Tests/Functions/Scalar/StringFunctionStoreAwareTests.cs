using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Mirrors <see cref="StringFunctionTests"/> but runs every test through the
/// store-aware <c>Execute(args, IValueStore store)</c> overload with an
/// <see cref="Arena"/>-backed value store. This validates that the span-based
/// implementations produce identical results to the original string-based paths.
/// </summary>
public class StringFunctionStoreAwareTests : IDisposable
{
    private readonly Arena _arena = new();

    public void Dispose() => _arena.Dispose();

    private DataValue S(string value) => DataValue.FromString(value, _arena);
    private DataValue NullStr => DataValue.Null(DataKind.String);
    private DataValue F(float value) => DataValue.FromFloat32(value);

    private string Str(DataValue v) => v.AsString((IValueStore)_arena);

    // ───────────────── UpperFunction ─────────────────

    [Fact] public void Upper_Basic() => Assert.Equal("HELLO", Str(new UpperFunction().Execute([S("hello")], _arena)));
    [Fact] public void Upper_Null() => Assert.True(new UpperFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── LowerFunction ─────────────────

    [Fact] public void Lower_Basic() => Assert.Equal("hello", Str(new LowerFunction().Execute([S("HELLO")], _arena)));
    [Fact] public void Lower_Null() => Assert.True(new LowerFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── TrimFunction ─────────────────

    [Fact] public void Trim_Whitespace() => Assert.Equal("hello", Str(new TrimFunction().Execute([S("  hello  ")], _arena)));
    [Fact] public void Trim_CharSet() => Assert.Equal("Tom", Str(new TrimFunction().Execute([S("yxTomxx"), S("xyz")], _arena)));
    [Fact] public void Trim_Null() => Assert.True(new TrimFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── LtrimFunction ─────────────────

    [Fact] public void Ltrim_Basic() => Assert.Equal("hello  ", Str(new LtrimFunction().Execute([S("  hello  ")], _arena)));
    [Fact] public void Ltrim_CharSet() => Assert.Equal("test", Str(new LtrimFunction().Execute([S("zzzytest"), S("xyz")], _arena)));
    [Fact] public void Ltrim_Null() => Assert.True(new LtrimFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── RtrimFunction ─────────────────

    [Fact] public void Rtrim_Basic() => Assert.Equal("  hello", Str(new RtrimFunction().Execute([S("  hello  ")], _arena)));
    [Fact] public void Rtrim_CharSet() => Assert.Equal("test", Str(new RtrimFunction().Execute([S("testxxzx"), S("xyz")], _arena)));
    [Fact] public void Rtrim_Null() => Assert.True(new RtrimFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── ContainsFunction ─────────────────

    [Fact] public void Contains_Found() => Assert.True(new ContainsFunction().Execute([S("hello world"), S("world")], _arena).AsBoolean());
    [Fact] public void Contains_NotFound() => Assert.False(new ContainsFunction().Execute([S("hello"), S("xyz")], _arena).AsBoolean());
    [Fact] public void Contains_Null() => Assert.True(new ContainsFunction().Execute([NullStr, S("world")], _arena).IsNull);

    // ───────────────── StartsWithFunction ─────────────────

    [Fact] public void StartsWith_Match() => Assert.True(new StartsWithFunction().Execute([S("hello"), S("hel")], _arena).AsBoolean());
    [Fact] public void StartsWith_NoMatch() => Assert.False(new StartsWithFunction().Execute([S("hello"), S("xyz")], _arena).AsBoolean());
    [Fact] public void StartsWith_Null() => Assert.True(new StartsWithFunction().Execute([NullStr, S("hel")], _arena).IsNull);

    // ───────────────── EndsWithFunction ─────────────────

    [Fact] public void EndsWith_Match() => Assert.True(new EndsWithFunction().Execute([S("hello"), S("llo")], _arena).AsBoolean());
    [Fact] public void EndsWith_Null() => Assert.True(new EndsWithFunction().Execute([NullStr, S("llo")], _arena).IsNull);

    // ───────────────── PositionFunction ─────────────────

    [Fact] public void Position_Found() => Assert.Equal(3, new PositionFunction().Execute([S("hello"), S("ll")], _arena).AsInt32());
    [Fact] public void Position_NotFound() => Assert.Equal(0, new PositionFunction().Execute([S("hello"), S("xyz")], _arena).AsInt32());
    [Fact] public void Position_Null() => Assert.True(new PositionFunction().Execute([NullStr, S("ll")], _arena).IsNull);

    // ───────────────── ReplaceFunction ─────────────────

    [Fact] public void Replace_Basic() => Assert.Equal("xbx", Str(new ReplaceFunction().Execute([S("aabaa"), S("aa"), S("x")], _arena)));
    [Fact] public void Replace_Null() => Assert.True(new ReplaceFunction().Execute([NullStr, S("aa"), S("x")], _arena).IsNull);

    // ───────────────── ConcatFunction ─────────────────

    [Fact] public void Concat_Basic() => Assert.Equal("hello world", Str(new ConcatFunction().Execute([S("hello"), S(" "), S("world")], _arena)));
    [Fact] public void Concat_NullArg() => Assert.Equal("hi!", Str(new ConcatFunction().Execute([S("hi"), NullStr, S("!")], _arena)));

    // ───────────────── RepeatFunction ─────────────────

    [Fact] public void Repeat_Basic() => Assert.Equal("ababab", Str(new RepeatFunction().Execute([S("ab"), F(3)], _arena)));
    [Fact] public void Repeat_Null() => Assert.True(new RepeatFunction().Execute([NullStr, F(3)], _arena).IsNull);

    // ───────────────── ReverseFunction ─────────────────

    [Fact] public void Reverse_Basic() => Assert.Equal("olleh", Str(new ReverseFunction().Execute([S("hello")], _arena)));
    [Fact] public void Reverse_Null() => Assert.True(new ReverseFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── LeftFunction ─────────────────

    [Fact] public void Left_Basic() => Assert.Equal("hel", Str(new LeftFunction().Execute([S("hello"), F(3)], _arena)));
    [Fact] public void Left_Negative() => Assert.Equal("abc", Str(new LeftFunction().Execute([S("abcde"), F(-2)], _arena)));
    [Fact] public void Left_NegExceed() => Assert.Equal("", Str(new LeftFunction().Execute([S("ab"), F(-5)], _arena)));
    [Fact] public void Left_Null() => Assert.True(new LeftFunction().Execute([NullStr, F(3)], _arena).IsNull);

    // ───────────────── RightFunction ─────────────────

    [Fact] public void Right_Basic() => Assert.Equal("llo", Str(new RightFunction().Execute([S("hello"), F(3)], _arena)));
    [Fact] public void Right_Negative() => Assert.Equal("cde", Str(new RightFunction().Execute([S("abcde"), F(-2)], _arena)));
    [Fact] public void Right_NegExceed() => Assert.Equal("", Str(new RightFunction().Execute([S("ab"), F(-5)], _arena)));
    [Fact] public void Right_Null() => Assert.True(new RightFunction().Execute([NullStr, F(3)], _arena).IsNull);

    // ───────────────── LpadFunction ─────────────────

    [Fact] public void Lpad_Basic() => Assert.Equal("xxxhi", Str(new LpadFunction().Execute([S("hi"), F(5), S("x")], _arena)));
    [Fact] public void Lpad_DefaultFill() => Assert.Equal("   hi", Str(new LpadFunction().Execute([S("hi"), F(5)], _arena)));
    [Fact] public void Lpad_MultiFill() => Assert.Equal("xyxhi", Str(new LpadFunction().Execute([S("hi"), F(5), S("xy")], _arena)));
    [Fact] public void Lpad_Null() => Assert.True(new LpadFunction().Execute([NullStr, F(5), S("x")], _arena).IsNull);

    // ───────────────── RpadFunction ─────────────────

    [Fact] public void Rpad_Basic() => Assert.Equal("hixxx", Str(new RpadFunction().Execute([S("hi"), F(5), S("x")], _arena)));
    [Fact] public void Rpad_DefaultFill() => Assert.Equal("hi   ", Str(new RpadFunction().Execute([S("hi"), F(5)], _arena)));
    [Fact] public void Rpad_MultiFill() => Assert.Equal("hixyx", Str(new RpadFunction().Execute([S("hi"), F(5), S("xy")], _arena)));
    [Fact] public void Rpad_Null() => Assert.True(new RpadFunction().Execute([NullStr, F(5), S("x")], _arena).IsNull);

    // ───────────────── OverlayFunction ─────────────────

    [Fact] public void Overlay_Basic() => Assert.Equal("Thomas", Str(new OverlayFunction().Execute([S("Txxxxas"), S("hom"), F(2), F(4)], _arena)));
    [Fact] public void Overlay_DefaultCount() => Assert.Equal("abXYef", Str(new OverlayFunction().Execute([S("abcdef"), S("XY"), F(3)], _arena)));
    [Fact] public void Overlay_Null() => Assert.True(new OverlayFunction().Execute([NullStr, S("x"), F(1)], _arena).IsNull);

    // ───────────────── StrposFunction ─────────────────

    [Fact] public void Strpos_Found() => Assert.Equal(2, new StrposFunction().Execute([S("high"), S("ig")], _arena).AsInt32());
    [Fact] public void Strpos_NotFound() => Assert.Equal(0, new StrposFunction().Execute([S("hello"), S("xyz")], _arena).AsInt32());
    [Fact] public void Strpos_Null() => Assert.True(new StrposFunction().Execute([NullStr, S("x")], _arena).IsNull);

    // ───────────────── OctetLengthFunction ─────────────────

    [Fact] public void OctetLength_Ascii() => Assert.Equal(4, new OctetLengthFunction().Execute([S("jose")], _arena).AsInt32());
    [Fact] public void OctetLength_Utf8() => Assert.Equal(5, new OctetLengthFunction().Execute([S("josé")], _arena).AsInt32());
    [Fact] public void OctetLength_Null() => Assert.True(new OctetLengthFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── BitLengthFunction ─────────────────

    [Fact] public void BitLength_Ascii() => Assert.Equal(32, new BitLengthFunction().Execute([S("jose")], _arena).AsInt32());
    [Fact] public void BitLength_Utf8() => Assert.Equal(40, new BitLengthFunction().Execute([S("josé")], _arena).AsInt32());
    [Fact] public void BitLength_Null() => Assert.True(new BitLengthFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── SubstringFunction ─────────────────

    [Fact] public void Substring_Basic() => Assert.Equal("llo", Str(new SubstringFunction().Execute([S("hello"), F(3)], _arena)));
    [Fact] public void Substring_WithLength() => Assert.Equal("ll", Str(new SubstringFunction().Execute([S("hello"), F(3), F(2)], _arena)));
    [Fact] public void Substring_Null() => Assert.True(new SubstringFunction().Execute([NullStr, F(1)], _arena).IsNull);

    // ───────────────── ConcatWsFunction ─────────────────

    [Fact]
    public void ConcatWs_Basic()
    {
        ConcatWsFunction function = new();
        DataValue result = function.Execute([S(","), S("a"), S("b"), S("c")], _arena);
        Assert.Equal("a,b,c", Str(result));
    }

    [Fact]
    public void ConcatWs_NullSkipped()
    {
        ConcatWsFunction function = new();
        DataValue result = function.Execute([S(","), S("a"), NullStr, S("c")], _arena);
        Assert.Equal("a,c", Str(result));
    }

    // ───────────────── BtrimFunction ─────────────────

    [Fact] public void Btrim_Whitespace() => Assert.Equal("hello", Str(new BtrimFunction().Execute([S("  hello  ")], _arena)));
    [Fact] public void Btrim_CharSet() => Assert.Equal("Tom", Str(new BtrimFunction().Execute([S("yxTomxx"), S("xyz")], _arena)));
    [Fact] public void Btrim_Null() => Assert.True(new BtrimFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── MidFunction ─────────────────

    [Fact] public void Mid_Basic() => Assert.Equal("llo", Str(new MidFunction().Execute([S("hello"), F(3), F(3)], _arena)));
    [Fact] public void Mid_Null() => Assert.True(new MidFunction().Execute([NullStr, F(1), F(1)], _arena).IsNull);

    // ───────────────── AsciiFunction ─────────────────

    [Fact] public void Ascii_Basic() => Assert.Equal(104, new AsciiFunction().Execute([S("hello")], _arena).AsInt32());
    [Fact] public void Ascii_Null() => Assert.True(new AsciiFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── WordCountFunction ─────────────────

    [Fact] public void WordCount_Basic() => Assert.Equal(3, new WordCountFunction().Execute([S("hello brave world")], _arena).AsInt32());
    [Fact] public void WordCount_Null() => Assert.True(new WordCountFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── InitcapFunction ─────────────────

    [Fact] public void Initcap_Basic() => Assert.Equal("Hello World", Str(new InitcapFunction().Execute([S("hello world")], _arena)));
    [Fact] public void Initcap_Null() => Assert.True(new InitcapFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── CasefoldFunction ─────────────────

    [Fact] public void Casefold_Basic() => Assert.Equal("hello", Str(new CasefoldFunction().Execute([S("HELLO")], _arena)));
    [Fact] public void Casefold_Null() => Assert.True(new CasefoldFunction().Execute([NullStr], _arena).IsNull);

    // ───────────────── LenFunction ─────────────────

    [Fact] public void Len_String() => Assert.Equal(5, new LenFunction().Execute([S("hello")], _arena).AsInt32());
    [Fact] public void Len_Utf8() => Assert.Equal(4, new LenFunction().Execute([S("josé")], _arena).AsInt32());
    [Fact] public void Len_Null() => Assert.True(new LenFunction().Execute([DataValue.Null(DataKind.String)], _arena).IsNull);
}
