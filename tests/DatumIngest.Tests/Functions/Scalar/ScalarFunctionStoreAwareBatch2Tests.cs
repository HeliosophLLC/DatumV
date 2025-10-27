using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Store-aware tests for batch 3 (regex/JSON/hash) and batch 4 (remaining scalar)
/// functions. All tests use an Arena-backed IValueStore to exercise the
/// <c>Execute(args, IValueStore store)</c> overloads.
/// </summary>
public class ScalarFunctionStoreAwareBatch2Tests : ServiceTestBase
{
    private readonly Arena _arena = new();

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }

    private DataValue S(string value) => DataValue.FromString(value, _arena);
    private DataValue J(string value) => DataValue.FromJsonValue(value, _arena);
    private DataValue NullStr => DataValue.Null(DataKind.String);
    private DataValue NullJson => DataValue.Null(DataKind.JsonValue);
    private DataValue F(float value) => DataValue.FromFloat32(value);

    private string Str(DataValue v) => v.AsString((IValueStore)_arena);

    // ───────────────── Regex functions ─────────────────

    [Fact] public void RegexpLike_Match() => Assert.True(new RegexpLikeFunction().Execute([S("abc123"), S("\\d+")], _arena).AsBoolean());
    [Fact] public void RegexpLike_NoMatch() => Assert.False(new RegexpLikeFunction().Execute([S("abc"), S("\\d+")], _arena).AsBoolean());
    [Fact] public void RegexpLike_Null() => Assert.True(new RegexpLikeFunction().Execute([NullStr, S("\\d+")], _arena).IsNull);

    [Fact] public void RegexpCount_Basic() => Assert.Equal(2, new RegexpCountFunction().Execute([S("ab12cd34"), S("\\d+")], _arena).AsInt32());
    [Fact] public void RegexpCount_Null() => Assert.True(new RegexpCountFunction().Execute([NullStr, S("\\d+")], _arena).IsNull);

    [Fact] public void RegexpExtract_Basic() => Assert.Equal("123", Str(new RegexpExtractFunction().Execute([S("abc123def"), S("\\d+")], _arena)));
    [Fact] public void RegexpExtract_NoMatch() => Assert.True(new RegexpExtractFunction().Execute([S("abc"), S("\\d+")], _arena).IsNull);
    [Fact] public void RegexpExtract_Null() => Assert.True(new RegexpExtractFunction().Execute([NullStr, S("\\d+")], _arena).IsNull);

    [Fact] public void RegexpReplace_Basic() => Assert.Equal("abc__def__", Str(new RegexpReplaceFunction().Execute([S("abc12def34"), S("\\d+"), S("__")], _arena)));
    [Fact] public void RegexpReplace_Null() => Assert.True(new RegexpReplaceFunction().Execute([NullStr, S("\\d+"), S("__")], _arena).IsNull);

    [Fact] public void RegexpSubstr_Basic() => Assert.Equal("123", Str(new RegexpSubstrFunction().Execute([S("abc123def"), S("\\d+")], _arena)));
    [Fact] public void RegexpSubstr_NoMatch() => Assert.True(new RegexpSubstrFunction().Execute([S("abc"), S("\\d+")], _arena).IsNull);

    [Fact] public void RegexpInstr_Found() => Assert.Equal(4, new RegexpInstrFunction().Execute([S("abc123"), S("\\d+")], _arena).AsInt32());
    [Fact] public void RegexpInstr_NotFound() => Assert.Equal(0, new RegexpInstrFunction().Execute([S("abc"), S("\\d+")], _arena).AsInt32());

    // ───────────────── JSON functions ─────────────────

    [Fact] public void JsonValue_String() => Assert.Equal("Alice", Str(new JsonValueFunction().Execute([J("{\"name\":\"Alice\"}"), S("name")], _arena)));
    [Fact] public void JsonValue_Number() => Assert.Equal(42.0, new JsonValueFunction().Execute([J("{\"x\":42}"), S("x")], _arena).AsFloat64());
    [Fact] public void JsonValue_NotFound() => Assert.True(new JsonValueFunction().Execute([J("{\"x\":1}"), S("y")], _arena).IsNull);
    [Fact] public void JsonValue_Null() => Assert.True(new JsonValueFunction().Execute([NullJson, S("x")], _arena).IsNull);

    [Fact] public void JsonExists_True() => Assert.True(new JsonExistsFunction().Execute([J("{\"x\":1}"), S("x")], _arena).AsBoolean());
    [Fact] public void JsonExists_False() => Assert.False(new JsonExistsFunction().Execute([J("{\"x\":1}"), S("y")], _arena).AsBoolean());

    [Fact] public void JsonArrayLength_Root() => Assert.Equal(3, new JsonArrayLengthFunction().Execute([J("[1,2,3]")], _arena).AsInt32());
    [Fact] public void JsonArrayLength_Nested() => Assert.Equal(2, new JsonArrayLengthFunction().Execute([J("{\"items\":[\"a\",\"b\"]}"), S("items")], _arena).AsInt32());

    // ───────────────── Hash functions ─────────────────

    [Fact]
    public void Md5Text_Basic()
    {
        string result = Str(new Md5TextFunction().Execute([S("hello")], _arena));
        Assert.Equal("5d41402abc4b2a76b9719d911017c592", result);
    }

    [Fact] public void Md5Text_Null() => Assert.True(new Md5TextFunction().Execute([NullStr], _arena).IsNull);

    [Fact]
    public void Sha256_Basic()
    {
        DataValue result = new Sha256Function().Execute([S("hello")], _arena);
        Assert.Equal(DataKind.UInt8Array, result.Kind);
        Assert.Equal(32, result.AsUInt8Array((IValueStore)_arena).Length);
    }

    [Fact] public void Sha256_Null() => Assert.True(new Sha256Function().Execute([NullStr], _arena).IsNull);

    [Fact]
    public void Crc32_Basic()
    {
        DataValue result = new Crc32Function().Execute([S("hello")], _arena);
        Assert.False(result.IsNull);
    }

    // ───────────────── Batch 4: remaining scalar ─────────────────

    // ── Concat / ConcatWs (already tested in batch 1, but verify store path) ──

    [Fact] public void Concat_Store() => Assert.Equal("ab", Str(new ConcatFunction().Execute([S("a"), S("b")], _arena)));
    [Fact] public void ConcatWs_Store() => Assert.Equal("a,b", Str(new ConcatWsFunction().Execute([S(","), S("a"), S("b")], _arena)));

    // ── Replace ──

    [Fact] public void Replace_Store() => Assert.Equal("xbx", Str(new ReplaceFunction().Execute([S("aabaa"), S("aa"), S("x")], _arena)));

    // ── Trim / Btrim ──

    [Fact] public void Trim_Store() => Assert.Equal("hello", Str(new TrimFunction().Execute([S("  hello  ")], _arena)));
    [Fact] public void Btrim_Store() => Assert.Equal("Tom", Str(new BtrimFunction().Execute([S("yxTomxx"), S("xyz")], _arena)));

    // ── Position / Strpos ──

    [Fact] public void Position_Store() => Assert.Equal(3, new PositionFunction().Execute([S("hello"), S("ll")], _arena).AsInt32());
    [Fact] public void Strpos_Store() => Assert.Equal(2, new StrposFunction().Execute([S("high"), S("ig")], _arena).AsInt32());

    // ── Lpad / Rpad ──

    [Fact] public void Lpad_Store() => Assert.Equal("xxhi", Str(new LpadFunction().Execute([S("hi"), F(4), S("x")], _arena)));
    [Fact] public void Rpad_Store() => Assert.Equal("hixx", Str(new RpadFunction().Execute([S("hi"), F(4), S("x")], _arena)));

    // ── OctetLength / BitLength ──

    [Fact] public void OctetLength_Store() => Assert.Equal(4, new OctetLengthFunction().Execute([S("test")], _arena).AsInt32());
    [Fact] public void BitLength_Store() => Assert.Equal(32, new BitLengthFunction().Execute([S("test")], _arena).AsInt32());
    [Fact] public void OctetLength_Utf8_Store() => Assert.Equal(5, new OctetLengthFunction().Execute([S("josé")], _arena).AsInt32());

    // ── Ascii ──

    [Fact] public void Ascii_Store() => Assert.Equal(104, new AsciiFunction().Execute([S("hello")], _arena).AsInt32());

    // ── Mid / Substring ──

    [Fact] public void Mid_Store() => Assert.Equal("llo", Str(new MidFunction().Execute([S("hello"), F(3), F(3)], _arena)));
    [Fact] public void Substring_Store() => Assert.Equal("llo", Str(new SubstringFunction().Execute([S("hello"), F(3)], _arena)));

    // ── Overlay ──

    [Fact] public void Overlay_Store() => Assert.Equal("Thomas", Str(new OverlayFunction().Execute([S("Txxxxas"), S("hom"), F(2), F(4)], _arena)));

    // ── WordCount ──

    [Fact] public void WordCount_Store() => Assert.Equal(3, new WordCountFunction().Execute([S("hello brave world")], _arena).AsInt32());

    // ── SplitPart ──

    [Fact] public void SplitPart_Store() => Assert.Equal("b", Str(new SplitPartFunction().Execute([S("a,b,c"), S(","), F(2)], _arena)));

    // ── StringToArray ──

    [Fact]
    public void StringToArray_Store()
    {
        DataValue result = new StringToArrayFunction().Execute([S("a,b,c"), S(",")], _arena);
        Assert.Equal(DataKind.Array, result.Kind);
    }

    // ── Translate ──

    [Fact] public void Translate_Store() => Assert.Equal("hollo", Str(new TranslateFunction().Execute([S("hello"), S("el"), S("ol")], _arena)));

    // ── IsDate / IsUuid ──

    [Fact] public void IsDate_Valid() => Assert.True(new IsDateFunction().Execute([S("2024-01-15")], _arena).AsBoolean());
    [Fact] public void IsDate_Invalid() => Assert.False(new IsDateFunction().Execute([S("not a date")], _arena).AsBoolean());
    [Fact] public void IsUuid_Valid() => Assert.True(new IsUuidFunction().Execute([S("12345678-1234-1234-1234-123456789abc")], _arena).AsBoolean());
    [Fact] public void IsUuid_Invalid() => Assert.False(new IsUuidFunction().Execute([S("nope")], _arena).AsBoolean());

    // ── GetFilename / GetFileExtension / GetPath ──

    [Fact] public void GetFilename_Store() => Assert.Equal("file.txt", Str(new GetFilenameFunction().Execute([S("/path/to/file.txt")], _arena)));
    [Fact] public void GetFileExtension_Store() => Assert.Equal(".txt", Str(new GetFileExtensionFunction().Execute([S("file.txt")], _arena)));
    [Fact] public void GetPath_Store() => Assert.Equal("/path/to", Str(new GetPathFunction().Execute([S("/path/to/file.txt")], _arena)));

    // ── HexDecode / Base64Decode ──

    [Fact]
    public void HexDecode_Store()
    {
        DataValue result = new HexDecodeFunction().Execute([S("48656C6C6F")], _arena);
        Assert.Equal("Hello", System.Text.Encoding.UTF8.GetString(result.AsUInt8Array((IValueStore)_arena)));
    }

    [Fact]
    public void Base64Decode_Store()
    {
        DataValue result = new Base64DecodeFunction().Execute([S("SGVsbG8=")], _arena);
        Assert.Equal("Hello", System.Text.Encoding.UTF8.GetString(result.AsUInt8Array((IValueStore)_arena)));
    }

    // ── QuoteIdent / QuoteLiteral / QuoteNullable ──

    [Fact] public void QuoteIdent_Simple() => Assert.Equal("hello", Str(new QuoteIdentFunction().Execute([S("hello")], _arena)));
    [Fact] public void QuoteIdent_NeedsQuoting() => Assert.Equal("\"Hello World\"", Str(new QuoteIdentFunction().Execute([S("Hello World")], _arena)));

    [Fact] public void QuoteLiteral_Store() => Assert.Equal("'hello'", Str(new QuoteLiteralFunction().Execute([S("hello")], _arena)));
    [Fact] public void QuoteLiteral_Escape() => Assert.Equal("'it''s'", Str(new QuoteLiteralFunction().Execute([S("it's")], _arena)));

    // ── Format ──

    [Fact]
    public void Format_Store()
    {
        FormatFunction function = new();
        DataValue result = function.Execute([S("value is %s"), F(3.14f)], _arena);
        Assert.Contains("3.14", Str(result));
    }

    // ── Cast ──

    [Fact]
    public void Cast_StringToInt()
    {
        CastFunction function = new();
        DataValue typeArg = DataValue.FromType(DataKind.Int64);
        DataValue result = function.Execute([S("42"), typeArg], _arena);
        Assert.Equal(42L, result.AsInt64());
    }

    [Fact]
    public void Cast_IntToString()
    {
        CastFunction function = new();
        DataValue typeArg = DataValue.FromType(DataKind.String);
        DataValue result = function.Execute([DataValue.FromInt64(42), typeArg], _arena);
        Assert.Equal("42", Str(result));
    }

    // ── UnicodeNormalize ──

    [Fact]
    public void UnicodeNormalize_Store()
    {
        UnicodeNormalizeFunction function = new();
        DataValue result = function.Execute([S("café"), S("NFC")], _arena);
        Assert.Equal("café", Str(result));
    }

    // ── Initcap / Casefold / Len ──

    [Fact] public void Initcap_Store() => Assert.Equal("Hello World", Str(new InitcapFunction().Execute([S("hello world")], _arena)));
    [Fact] public void Casefold_Store() => Assert.Equal("hello", Str(new CasefoldFunction().Execute([S("HELLO")], _arena)));
    [Fact] public void Len_Store() => Assert.Equal(5, new LenFunction().Execute([S("hello")], _arena).AsInt32());
    [Fact] public void Len_Utf8_Store() => Assert.Equal(4, new LenFunction().Execute([S("josé")], _arena).AsInt32());
}
