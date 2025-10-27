using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

public class JsonFunctionTests : ServiceTestBase
{
    // ───────────────── JsonValueFunction ─────────────────

    [Fact]
    public void JsonValue_ExtractsStringValue()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"name\": \"Alice\"}"),
            DataValue.FromString("name")
        ]);
        Assert.Equal("Alice", result.AsString());
    }

    [Fact]
    public void JsonValue_ExtractsNumberValue()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"age\": 30}"),
            DataValue.FromString("age")
        ]);
        Assert.Equal(30.0, result.AsFloat64());
    }

    [Fact]
    public void JsonValue_ExtractsBoolTrue()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"active\": true}"),
            DataValue.FromString("active")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void JsonValue_ExtractsBoolFalse()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"active\": false}"),
            DataValue.FromString("active")
        ]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void JsonValue_NestedPath()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"address\": {\"city\": \"NYC\"}}"),
            DataValue.FromString("address.city")
        ]);
        Assert.Equal("NYC", result.AsString());
    }

    [Fact]
    public void JsonValue_ArrayIndexPath()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"items\": [10, 20, 30]}"),
            DataValue.FromString("items.1")
        ]);
        Assert.Equal(20.0, result.AsFloat64());
    }

    [Fact]
    public void JsonValue_MissingPath_ReturnsNull()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"name\": \"Alice\"}"),
            DataValue.FromString("missing")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JsonValue_ObjectAtPath_ReturnsNull()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"obj\": {\"key\": 1}}"),
            DataValue.FromString("obj")
        ]);
        // json_value returns null for non-scalar values
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JsonValue_NullInput_ReturnsNull()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.JsonValue),
            DataValue.FromString("key")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JsonValue_InvalidJson_ReturnsNull()
    {
        JsonValueFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("not json"),
            DataValue.FromString("key")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JsonValue_Validate_WrongArgCount_Throws()
    {
        JsonValueFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.JsonValue]));
    }

    [Fact]
    public void JsonValue_Validate_WrongFirstArgKind_Throws()
    {
        JsonValueFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Float32, DataKind.String]));
    }

    // ───────────────── JsonQueryFunction ─────────────────

    [Fact]
    public void JsonQuery_ExtractsObject()
    {
        JsonQueryFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"obj\": {\"key\": 1}}"),
            DataValue.FromString("obj")
        ]);
        Assert.Equal(DataKind.JsonValue, result.Kind);
        Assert.Equal("{\"key\": 1}", result.AsJsonValue());
    }

    [Fact]
    public void JsonQuery_ExtractsNumericArray_AsVector()
    {
        JsonQueryFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"nums\": [1.5, 2.5, 3.5]}"),
            DataValue.FromString("nums")
        ]);
        Assert.Equal(DataKind.Vector, result.Kind);
        float[] vector = result.AsVector();
        Assert.Equal(3, vector.Length);
        Assert.Equal(1.5f, vector[0], 0.001f);
        Assert.Equal(2.5f, vector[1], 0.001f);
        Assert.Equal(3.5f, vector[2], 0.001f);
    }

    [Fact]
    public void JsonQuery_MixedArray_ReturnsJsonValue()
    {
        JsonQueryFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"arr\": [1, \"two\", 3]}"),
            DataValue.FromString("arr")
        ]);
        Assert.Equal(DataKind.JsonValue, result.Kind);
    }

    [Fact]
    public void JsonQuery_MissingPath_ReturnsNull()
    {
        JsonQueryFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"a\": 1}"),
            DataValue.FromString("missing")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JsonQuery_NullInput_ReturnsNull()
    {
        JsonQueryFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.JsonValue),
            DataValue.FromString("key")
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── JsonExistsFunction ─────────────────

    [Fact]
    public void JsonExists_PathExists_ReturnsTrue()
    {
        JsonExistsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"name\": \"Alice\"}"),
            DataValue.FromString("name")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void JsonExists_PathMissing_ReturnsFalse()
    {
        JsonExistsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"name\": \"Alice\"}"),
            DataValue.FromString("missing")
        ]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void JsonExists_NestedPath()
    {
        JsonExistsFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"a\": {\"b\": {\"c\": 1}}}"),
            DataValue.FromString("a.b.c")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void JsonExists_NullInput_ReturnsFalse()
    {
        JsonExistsFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.JsonValue),
            DataValue.FromString("key")
        ]);
        Assert.False(result.AsBoolean());
    }

    // ───────────────── JsonArrayLengthFunction ─────────────────

    [Fact]
    public void JsonArrayLength_RootArray()
    {
        JsonArrayLengthFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("[1, 2, 3, 4, 5]")
        ]);
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void JsonArrayLength_NestedArray()
    {
        JsonArrayLengthFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"items\": [\"a\", \"b\"]}"),
            DataValue.FromString("items")
        ]);
        Assert.Equal(2, result.AsInt32());
    }

    [Fact]
    public void JsonArrayLength_NotAnArray_ReturnsNull()
    {
        JsonArrayLengthFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"key\": 1}")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JsonArrayLength_EmptyArray_ReturnsZero()
    {
        JsonArrayLengthFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("[]")
        ]);
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void JsonArrayLength_NullInput_ReturnsNull()
    {
        JsonArrayLengthFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.JsonValue)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JsonArrayLength_MissingPath_ReturnsNull()
    {
        JsonArrayLengthFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromJsonValue("{\"a\": 1}"),
            DataValue.FromString("missing")
        ]);
        Assert.True(result.IsNull);
    }
}
