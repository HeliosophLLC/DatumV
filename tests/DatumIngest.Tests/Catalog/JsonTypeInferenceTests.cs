using System.Text.Json;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="JsonTypeInference"/>.
/// </summary>
public class JsonTypeInferenceTests
{
    // ───────────────────── InferKind ─────────────────────

    [Fact]
    public void InferKind_Number_ReturnsScalar()
    {
        JsonElement element = JsonDocument.Parse("42").RootElement;
        Assert.Equal(DataKind.Int64, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_PlainString_ReturnsString()
    {
        JsonElement element = JsonDocument.Parse("\"hello\"").RootElement;
        Assert.Equal(DataKind.String, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_DateString_ReturnsDate()
    {
        JsonElement element = JsonDocument.Parse("\"2024-01-15\"").RootElement;
        Assert.Equal(DataKind.Date, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_DateTimeString_ReturnsDateTime()
    {
        JsonElement element = JsonDocument.Parse("\"2024-01-15T10:30:00Z\"").RootElement;
        Assert.Equal(DataKind.DateTime, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_DateTimeWithOffset_ReturnsDateTime()
    {
        JsonElement element = JsonDocument.Parse("\"2024-01-15T10:30:00+05:30\"").RootElement;
        Assert.Equal(DataKind.DateTime, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_Object_ReturnsJsonValue()
    {
        JsonElement element = JsonDocument.Parse("{\"key\": 1}").RootElement;
        Assert.Equal(DataKind.JsonValue, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_Array_ReturnsJsonValue()
    {
        JsonElement element = JsonDocument.Parse("[1, 2, 3]").RootElement;
        Assert.Equal(DataKind.JsonValue, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_Boolean_ReturnsScalar()
    {
        JsonElement element = JsonDocument.Parse("true").RootElement;
        Assert.Equal(DataKind.Boolean, JsonTypeInference.InferKind(element));
    }

    [Fact]
    public void InferKind_Null_ReturnsString()
    {
        JsonElement element = JsonDocument.Parse("null").RootElement;
        Assert.Equal(DataKind.String, JsonTypeInference.InferKind(element));
    }

    // ───────────────────── WidenKind ─────────────────────

    [Fact]
    public void WidenKind_SameKind_ReturnsSame()
    {
        Assert.Equal(DataKind.Int64, JsonTypeInference.WidenKind(DataKind.Int64, DataKind.Int64));
        Assert.Equal(DataKind.String, JsonTypeInference.WidenKind(DataKind.String, DataKind.String));
        Assert.Equal(DataKind.Date, JsonTypeInference.WidenKind(DataKind.Date, DataKind.Date));
        Assert.Equal(DataKind.DateTime, JsonTypeInference.WidenKind(DataKind.DateTime, DataKind.DateTime));
    }

    [Fact]
    public void WidenKind_DateAndDateTime_WidensToDateTime()
    {
        Assert.Equal(DataKind.DateTime, JsonTypeInference.WidenKind(DataKind.Date, DataKind.DateTime));
        Assert.Equal(DataKind.DateTime, JsonTypeInference.WidenKind(DataKind.DateTime, DataKind.Date));
    }

    [Fact]
    public void WidenKind_DifferentKinds_WidensToString()
    {
        Assert.Equal(DataKind.String, JsonTypeInference.WidenKind(DataKind.Int64, DataKind.String));
        Assert.Equal(DataKind.String, JsonTypeInference.WidenKind(DataKind.Date, DataKind.Int64));
        Assert.Equal(DataKind.String, JsonTypeInference.WidenKind(DataKind.DateTime, DataKind.String));
    }

    [Fact]
    public void WidenKind_JsonValue_DominatesOtherKinds()
    {
        Assert.Equal(DataKind.JsonValue, JsonTypeInference.WidenKind(DataKind.JsonValue, DataKind.String));
        Assert.Equal(DataKind.JsonValue, JsonTypeInference.WidenKind(DataKind.Int64, DataKind.JsonValue));
        Assert.Equal(DataKind.JsonValue, JsonTypeInference.WidenKind(DataKind.JsonValue, DataKind.Date));
    }

    // ───────────────────── ConvertElement ─────────────────────

    [Fact]
    public void ConvertElement_DateString_ProducesDateValue()
    {
        JsonElement element = JsonDocument.Parse("\"2024-06-15\"").RootElement;
        DataValue result = JsonTypeInference.ConvertElement(element, DataKind.Date);
        Assert.Equal(new DateOnly(2024, 6, 15), result.AsDate());
    }

    [Fact]
    public void ConvertElement_DateTimeString_ProducesDateTimeValue()
    {
        JsonElement element = JsonDocument.Parse("\"2024-06-15T10:30:00Z\"").RootElement;
        DataValue result = JsonTypeInference.ConvertElement(element, DataKind.DateTime);
        Assert.Equal(DataKind.DateTime, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void ConvertElement_InvalidDateString_ReturnsNull()
    {
        JsonElement element = JsonDocument.Parse("\"not a date\"").RootElement;
        DataValue result = JsonTypeInference.ConvertElement(element, DataKind.Date);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void ConvertElement_NullElement_ReturnsTypedNull()
    {
        JsonElement element = JsonDocument.Parse("null").RootElement;
        DataValue result = JsonTypeInference.ConvertElement(element, DataKind.Date);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }
}
