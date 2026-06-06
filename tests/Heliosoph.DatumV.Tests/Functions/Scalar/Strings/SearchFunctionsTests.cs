using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="PositionFunction"/> (incl. the <c>strpos</c> alias),
/// <see cref="StartsWithFunction"/>, <see cref="EndsWithFunction"/>,
/// <see cref="ContainsFunction"/>, <see cref="ReplaceFunction"/>, and
/// <see cref="SplitPartFunction"/>.
/// </summary>
public sealed class SearchFunctionsTests
{
    // ─── position / strpos ─────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", "world", 7)]
    [InlineData("hello", "hello", 1)]
    [InlineData("abc", "z", 0)]
    [InlineData("abcabc", "b", 2)]
    [InlineData("anything", "", 1)]
    public async Task Position_FindsSubstring(string s, string sub, int expected)
    {
        PositionFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(sub) }, default, default);
        Assert.Equal(expected, result.AsInt32());
    }

    [Fact]
    public async Task Position_NullArg_ReturnsNull()
    {
        PositionFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromString("x") }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Position_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<PositionFunction>(registry.TryGetScalar("position"));
    }

    [Fact]
    public void Strpos_RegisteredAsAlias()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<PositionFunction>(registry.TryGetScalar("strpos"));
    }

    // ─── starts_with ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", "hello", true)]
    [InlineData("hello world", "world", false)]
    [InlineData("", "", true)]
    [InlineData("hi", "hello", false)]
    public async Task StartsWith_Cases(string s, string prefix, bool expected)
    {
        StartsWithFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(prefix) }, default, default);
        Assert.Equal(expected, result.AsBoolean());
    }

    [Fact]
    public void StartsWith_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<StartsWithFunction>(registry.TryGetScalar("starts_with"));
    }

    // ─── ends_with ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", "world", true)]
    [InlineData("hello world", "hello", false)]
    [InlineData("", "", true)]
    [InlineData("hi", "hello", false)]
    public async Task EndsWith_Cases(string s, string suffix, bool expected)
    {
        EndsWithFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(suffix) }, default, default);
        Assert.Equal(expected, result.AsBoolean());
    }

    [Fact]
    public void EndsWith_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<EndsWithFunction>(registry.TryGetScalar("ends_with"));
    }

    // ─── contains ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", "lo wo", true)]
    [InlineData("hello world", "xyz", false)]
    [InlineData("abc", "", true)]
    public async Task Contains_Cases(string s, string sub, bool expected)
    {
        ContainsFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(sub) }, default, default);
        Assert.Equal(expected, result.AsBoolean());
    }

    [Fact]
    public void Contains_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<ContainsFunction>(registry.TryGetScalar("contains"));
    }

    // ─── replace ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", "world", "there", "hello there")]
    [InlineData("aaa", "a", "bb", "bbbbbb")]
    [InlineData("abc", "z", "Z", "abc")]
    [InlineData("hello", "", "X", "hello")]
    [InlineData("hello", "l", "", "heo")]
    public async Task Replace_Cases(string s, string from, string to, string expected)
    {
        ReplaceFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(from), ValueRef.FromString(to) },
            default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Replace_NullArg_ReturnsNull()
    {
        ReplaceFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("abc"), ValueRef.Null(DataKind.String), ValueRef.FromString("y") },
            default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Replace_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<ReplaceFunction>(registry.TryGetScalar("replace"));
    }

    // ─── split_part ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.b.c", ".", 1, "a")]
    [InlineData("a.b.c", ".", 2, "b")]
    [InlineData("a.b.c", ".", 3, "c")]
    [InlineData("a.b.c", ".", 4, "")]
    [InlineData("a.b.c", ".", -1, "c")]
    [InlineData("a.b.c", ".", -2, "b")]
    [InlineData("a.b.c", ".", -4, "")]
    [InlineData("a.b.c", ".", 0, "")]
    [InlineData("a~~b~~c", "~~", 2, "b")]
    [InlineData("nodelim", ".", 1, "nodelim")]
    [InlineData("nodelim", ".", 2, "")]
    public async Task SplitPart_Cases(string s, string delim, int n, string expected)
    {
        SplitPartFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(delim), ValueRef.FromInt32(n) },
            default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task SplitPart_EmptyDelimiter_OnlyN1Or_Minus1Yields_Value()
    {
        SplitPartFunction function = new();
        ValueRef pos = await function.ExecuteAsync(
            new[] { ValueRef.FromString("hello"), ValueRef.FromString(""), ValueRef.FromInt32(1) },
            default, default);
        Assert.Equal("hello", pos.AsString());

        ValueRef neg = await function.ExecuteAsync(
            new[] { ValueRef.FromString("hello"), ValueRef.FromString(""), ValueRef.FromInt32(-1) },
            default, default);
        Assert.Equal("hello", neg.AsString());

        ValueRef other = await function.ExecuteAsync(
            new[] { ValueRef.FromString("hello"), ValueRef.FromString(""), ValueRef.FromInt32(2) },
            default, default);
        Assert.Equal("", other.AsString());
    }

    [Fact]
    public void SplitPart_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<SplitPartFunction>(registry.TryGetScalar("split_part"));
    }
}
