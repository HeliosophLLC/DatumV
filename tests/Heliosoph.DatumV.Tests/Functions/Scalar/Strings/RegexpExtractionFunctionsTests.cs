using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for the regex-extraction family: <see cref="RegexpMatchFunction"/>,
/// <see cref="RegexpCountFunction"/>, and <see cref="RegexpSubstrFunction"/>.
/// </summary>
public sealed class RegexpExtractionFunctionsTests
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

    // ─── regexp_match ──────────────────────────────────────────────────────

    [Fact]
    public async Task RegexpMatch_WithGroups_ReturnsCaptures()
    {
        RegexpMatchFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("2024-03-26"),
                ValueRef.FromString(@"(\d{4})-(\d{2})-(\d{2})"),
            }, default, default);
        Assert.Equal(new[] { "2024", "03", "26" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task RegexpMatch_NoGroups_ReturnsWholeMatch()
    {
        RegexpMatchFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.Equal(new[] { "123" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task RegexpMatch_NoMatch_ReturnsNullArray()
    {
        RegexpMatchFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("hello"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task RegexpMatch_CaseInsensitiveFlag()
    {
        RegexpMatchFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("Hello World"),
                ValueRef.FromString("(world)"),
                ValueRef.FromString("i"),
            }, default, default);
        Assert.Equal(new[] { "World" }, ArrayToStringList(result));
    }

    [Fact]
    public void RegexpMatch_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpMatchFunction>(registry.TryGetScalar("regexp_match"));
    }

    // ─── regexp_count ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("abc123def456", @"\d+", 2)]
    [InlineData("aaaa", "a", 4)]
    [InlineData("hello", @"\d", 0)]
    public async Task RegexpCount_BasicCases(string s, string pattern, int expected)
    {
        RegexpCountFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(pattern) }, default, default);
        Assert.Equal(expected, result.AsInt32());
    }

    [Fact]
    public async Task RegexpCount_WithStart_SkipsEarlierMatches()
    {
        RegexpCountFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def456"),
                ValueRef.FromString(@"\d+"),
                ValueRef.FromInt32(7),
            }, default, default);
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public async Task RegexpCount_CaseInsensitiveFlag()
    {
        RegexpCountFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("Hello hello HELLO"),
                ValueRef.FromString("hello"),
                ValueRef.FromInt32(1),
                ValueRef.FromString("i"),
            }, default, default);
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void RegexpCount_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpCountFunction>(registry.TryGetScalar("regexp_count"));
    }

    // ─── regexp_substr ─────────────────────────────────────────────────────

    [Fact]
    public async Task RegexpSubstr_DefaultsToFirstMatch()
    {
        RegexpSubstrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def456"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.Equal("123", result.AsString());
    }

    [Fact]
    public async Task RegexpSubstr_StartAndN_PicksNthFromStart()
    {
        RegexpSubstrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def456"),
                ValueRef.FromString(@"\d+"),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(2),
            }, default, default);
        Assert.Equal("456", result.AsString());
    }

    [Fact]
    public async Task RegexpSubstr_NoMatch_ReturnsNull()
    {
        RegexpSubstrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("hello"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task RegexpSubstr_SubexprSelectsCaptureGroup()
    {
        RegexpSubstrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("ABCDEF"),
                ValueRef.FromString("c(.)(..)"),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(1),
                ValueRef.FromString("i"),
                ValueRef.FromInt32(2),
            }, default, default);
        Assert.Equal("EF", result.AsString());
    }

    [Fact]
    public void RegexpSubstr_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpSubstrFunction>(registry.TryGetScalar("regexp_substr"));
    }
}
