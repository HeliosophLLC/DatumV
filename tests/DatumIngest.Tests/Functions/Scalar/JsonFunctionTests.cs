using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Functions.Scalar.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// End-to-end smoke tests for the json_parse → json_value / json_query →
/// json_to_text flow. Exercises the codec, the path walker, and the round-trip
/// to verify the engine handles the parse-once-read-many pattern that the
/// LLM-output use case depends on.
/// </summary>
public sealed class JsonFunctionTests
{
    private static readonly EvaluationFrame Frame = default;

    // ─── json_parse ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleObject_ProducesJsonKind()
    {
        ValueRef result = new JsonParseFunction().Execute(
            [ValueRef.FromString("""{"a":1,"b":"x"}""")], in Frame);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Json, result.Kind);
    }

    [Fact]
    public void Parse_NullInput_ReturnsTypedNull()
    {
        ValueRef result = new JsonParseFunction().Execute(
            [ValueRef.Null(DataKind.String)], in Frame);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Json, result.Kind);
    }

    // ─── json_value (typed scalar extraction) ──────────────────────────────

    [Fact]
    public void Value_String_ReturnsString()
    {
        ValueRef doc = Parse("""{"name":"Alice"}""");
        ValueRef result = ExecuteValue(doc, "$.name");

        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal("Alice", result.AsString());
    }

    [Fact]
    public void Value_Integer_ReturnsInt64()
    {
        ValueRef doc = Parse("""{"age":30}""");
        ValueRef result = ExecuteValue(doc, "$.age");

        Assert.Equal(DataKind.Int64, result.Kind);
        Assert.Equal(30L, result.AsInt64());
    }

    [Fact]
    public void Value_Float_ReturnsFloat64()
    {
        ValueRef doc = Parse("""{"score":0.97}""");
        ValueRef result = ExecuteValue(doc, "$.score");

        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(0.97, result.AsFloat64(), precision: 10);
    }

    [Fact]
    public void Value_Boolean_ReturnsBoolean()
    {
        ValueRef doc = Parse("""{"active":true}""");
        ValueRef result = ExecuteValue(doc, "$.active");

        Assert.Equal(DataKind.Boolean, result.Kind);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Value_MissingKey_ReturnsNull()
    {
        ValueRef doc = Parse("""{"a":1}""");
        ValueRef result = ExecuteValue(doc, "$.missing");

        Assert.True(result.IsNull);
    }

    [Fact]
    public void Value_NestedPath_DescendsCorrectly()
    {
        ValueRef doc = Parse("""{"user":{"profile":{"city":"NYC"}}}""");
        ValueRef result = ExecuteValue(doc, "$.user.profile.city");

        Assert.Equal("NYC", result.AsString());
    }

    [Fact]
    public void Value_ArrayIndex_ReturnsElement()
    {
        ValueRef doc = Parse("""{"tags":["admin","vip","staff"]}""");
        ValueRef result = ExecuteValue(doc, "$.tags[1]");

        Assert.Equal("vip", result.AsString());
    }

    [Fact]
    public void Value_OutOfRangeIndex_ReturnsNull()
    {
        ValueRef doc = Parse("""{"tags":["a"]}""");
        ValueRef result = ExecuteValue(doc, "$.tags[5]");

        Assert.True(result.IsNull);
    }

    [Fact]
    public void Value_PathPointsToObject_ReturnsNull()
    {
        // $.user resolves to an object; json_value returns NULL for non-scalars.
        ValueRef doc = Parse("""{"user":{"name":"Bob"}}""");
        ValueRef result = ExecuteValue(doc, "$.user");

        Assert.True(result.IsNull);
    }

    // ─── json_query (subdocument extraction) ───────────────────────────────

    [Fact]
    public void Query_Subobject_ReturnsJson()
    {
        ValueRef doc = Parse("""{"user":{"name":"Bob","age":40}}""");
        ValueRef result = ExecuteQuery(doc, "$.user");

        Assert.Equal(DataKind.Json, result.Kind);
        Assert.False(result.IsNull);

        // Round-trip the subdocument back to text so we can verify the slice
        // boundary was correct (not over-or-under-shooting into siblings).
        string text = ToText(result);
        Assert.Equal("""{"age":40,"name":"Bob"}""", text);
    }

    [Fact]
    public void Query_Array_ReturnsJson()
    {
        ValueRef doc = Parse("""{"tags":["a","b","c"]}""");
        ValueRef result = ExecuteQuery(doc, "$.tags");

        Assert.Equal(DataKind.Json, result.Kind);
        Assert.Equal("""["a","b","c"]""", ToText(result));
    }

    [Fact]
    public void Query_PathPointsToScalar_ReturnsNull()
    {
        ValueRef doc = Parse("""{"name":"Bob"}""");
        ValueRef result = ExecuteQuery(doc, "$.name");

        Assert.True(result.IsNull);
    }

    [Fact]
    public void Query_ChainedThroughValue_ResolvesNestedScalar()
    {
        // The parse-once, query-then-value pattern: extract a subdocument
        // with json_query, then read a scalar from it with json_value. The
        // intermediate Json value is a slice view over the source CBOR; no
        // extra allocation in the chain.
        ValueRef doc = Parse("""{"user":{"addr":{"city":"NYC","zip":"10001"}}}""");
        ValueRef sub = ExecuteQuery(doc, "$.user.addr");
        ValueRef city = ExecuteValue(sub, "$.city");

        Assert.Equal("NYC", city.AsString());
    }

    // ─── json_to_text (canonical round-trip) ───────────────────────────────

    [Fact]
    public void ToText_SortsObjectKeys()
    {
        // Canonical CBOR sorts map keys (length-first then lexicographic byte
        // order). Re-emit reflects the canonical form, not the source order.
        ValueRef doc = Parse("""{"b":2,"a":1}""");

        Assert.Equal("""{"a":1,"b":2}""", ToText(doc));
    }

    [Fact]
    public void ToText_NumberPolicy_PrefersInt()
    {
        // Conservative number policy: 1.0 parses as int (TryGetInt64 succeeds
        // on integer-valued JSON numbers); the canonical form drops the .0.
        ValueRef doc = Parse("""{"x":1.0}""");

        Assert.Equal("""{"x":1}""", ToText(doc));
    }

    [Fact]
    public void ToText_PreservesArrayOrder()
    {
        // Arrays are ordered; canonical mode does NOT re-sort them.
        ValueRef doc = Parse("""[3,1,2]""");

        Assert.Equal("[3,1,2]", ToText(doc));
    }

    // ─── Cast wiring ───────────────────────────────────────────────────────

    [Fact]
    public void Cast_StringToJson_Parses()
    {
        ValueRef typeArg = ValueRef.FromType(DataKind.Json);
        ValueRef result = new CastFunction().Execute(
            [ValueRef.FromString("""{"k":42}"""), typeArg], in Frame);

        Assert.Equal(DataKind.Json, result.Kind);
        Assert.Equal(42L, ExecuteValue(result, "$.k").AsInt64());
    }

    [Fact]
    public void TryCast_StringToJson_InvalidJson_ReturnsNull()
    {
        ValueRef typeArg = ValueRef.FromType(DataKind.Json);
        ValueRef result = new TryCastFunction().Execute(
            [ValueRef.FromString("not valid json"), typeArg], in Frame);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void Cast_JsonToString_ReturnsCanonicalText()
    {
        ValueRef doc = Parse("""{"b":2,"a":1}""");
        ValueRef typeArg = ValueRef.FromType(DataKind.String);
        ValueRef result = new CastFunction().Execute([doc, typeArg], in Frame);

        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal("""{"a":1,"b":2}""", result.AsString());
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static ValueRef Parse(string jsonText) =>
        new JsonParseFunction().Execute([ValueRef.FromString(jsonText)], in Frame);

    private static ValueRef ExecuteValue(ValueRef doc, string path) =>
        new JsonValueFunction().Execute([doc, ValueRef.FromString(path)], in Frame);

    private static ValueRef ExecuteQuery(ValueRef doc, string path) =>
        new JsonQueryFunction().Execute([doc, ValueRef.FromString(path)], in Frame);

    private static string ToText(ValueRef doc) =>
        new JsonToTextFunction().Execute([doc], in Frame).AsString();
}
