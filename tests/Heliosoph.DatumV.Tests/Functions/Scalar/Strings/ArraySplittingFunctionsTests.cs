using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="StringToArrayFunction"/> and
/// <see cref="RegexpSplitToArrayFunction"/>.
/// </summary>
public sealed class ArraySplittingFunctionsTests
{
    private static List<string?> ArrayToStringList(ValueRef arrayRef)
    {
        Assert.True(arrayRef.IsArray);
        ReadOnlySpan<ValueRef> elements = arrayRef.GetArrayElements();
        List<string?> result = new(elements.Length);
        for (int i = 0; i < elements.Length; i++)
        {
            result.Add(elements[i].IsNull ? null : elements[i].AsString());
        }
        return result;
    }

    // ─── string_to_array ───────────────────────────────────────────────────

    [Fact]
    public async Task StringToArray_SimpleDelimiter()
    {
        StringToArrayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("a,b,c"), ValueRef.FromString(",") }, default, default);
        Assert.Equal(new[] { "a", "b", "c" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task StringToArray_NullStringMapsToNull()
    {
        StringToArrayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("xx~~yy~~zz"),
                ValueRef.FromString("~~"),
                ValueRef.FromString("yy"),
            }, default, default);
        Assert.Equal(new string?[] { "xx", null, "zz" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task StringToArray_NullDelimiterSplitsIntoCodePoints()
    {
        StringToArrayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("abc"), ValueRef.Null(DataKind.String) }, default, default);
        Assert.Equal(new[] { "a", "b", "c" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task StringToArray_EmptyDelimiter_ReturnsWholeStringAsSingleElement()
    {
        StringToArrayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("abc"), ValueRef.FromString("") }, default, default);
        Assert.Equal(new[] { "abc" }, ArrayToStringList(result));
    }

    [Fact]
    public void StringToArray_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<StringToArrayFunction>(registry.TryGetScalar("string_to_array"));
    }

    // ─── regexp_split_to_array ─────────────────────────────────────────────

    [Fact]
    public async Task RegexpSplitToArray_Whitespace()
    {
        RegexpSplitToArrayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("hello   world"), ValueRef.FromString(@"\s+") }, default, default);
        Assert.Equal(new[] { "hello", "world" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task RegexpSplitToArray_CaseInsensitive()
    {
        RegexpSplitToArrayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("aXbxc"),
                ValueRef.FromString("x"),
                ValueRef.FromString("i"),
            }, default, default);
        Assert.Equal(new[] { "a", "b", "c" }, ArrayToStringList(result));
    }

    [Fact]
    public void RegexpSplitToArray_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpSplitToArrayFunction>(registry.TryGetScalar("regexp_split_to_array"));
    }
}
