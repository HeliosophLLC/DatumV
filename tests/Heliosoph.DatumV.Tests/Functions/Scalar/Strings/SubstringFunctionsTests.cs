using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for the substring family: <see cref="SubstringFunction"/> (and the
/// <c>substr</c> alias), <see cref="MidFunction"/>, <see cref="LeftFunction"/>,
/// <see cref="RightFunction"/>, and <see cref="OverlayFunction"/>.
/// </summary>
public sealed class SubstringFunctionsTests
{
    // ─── substring / substr ────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", 7, "world")]
    [InlineData("hello world", 1, "hello world")]
    [InlineData("hello", 10, "")]
    [InlineData("hello", 0, "hello")]
    [InlineData("hello", -3, "hello")]
    public async Task Substring_TwoArg(string s, int start, string expected)
    {
        SubstringFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromInt32(start) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData("hello world", 1, 5, "hello")]
    [InlineData("hello world", 7, 5, "world")]
    [InlineData("hello", 2, 3, "ell")]
    [InlineData("hello", 0, 3, "he")]     // PG: start=0 means end = -1 + 3 = 2 inclusive → 'he'
    [InlineData("hello", -1, 3, "h")]     // PG: end = -2 + 3 = 1 inclusive → 'h'
    [InlineData("hello", 10, 5, "")]
    [InlineData("hello", 1, 0, "")]
    public async Task Substring_ThreeArg(string s, int start, int length, string expected)
    {
        SubstringFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromInt32(start), ValueRef.FromInt32(length) },
            default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Substring_NegativeLength_Throws()
    {
        SubstringFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(
                new[] { ValueRef.FromString("hi"), ValueRef.FromInt32(1), ValueRef.FromInt32(-1) },
                default, default));
    }

    [Fact]
    public void Substring_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<SubstringFunction>(registry.TryGetScalar("substring"));
    }

    [Fact]
    public void Substr_RegisteredAsAlias()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<SubstringFunction>(registry.TryGetScalar("substr"));
    }

    // ─── mid ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", 1, 5, "hello")]
    [InlineData("hello world", 7, 5, "world")]
    public async Task Mid_Cases(string s, int start, int length, string expected)
    {
        MidFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromInt32(start), ValueRef.FromInt32(length) },
            default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Mid_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<MidFunction>(registry.TryGetScalar("mid"));
    }

    // ─── left ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello", 3, "hel")]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 0, "")]
    [InlineData("hello", -2, "hel")]
    [InlineData("hello", -10, "")]
    public async Task Left_Cases(string s, int n, string expected)
    {
        LeftFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromInt32(n) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Left_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<LeftFunction>(registry.TryGetScalar("left"));
    }

    // ─── right ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello", 3, "llo")]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 0, "")]
    [InlineData("hello", -2, "llo")]
    [InlineData("hello", -10, "")]
    public async Task Right_Cases(string s, int n, string expected)
    {
        RightFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromInt32(n) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Right_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RightFunction>(registry.TryGetScalar("right"));
    }

    // ─── overlay ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Overlay_DocExample()
    {
        OverlayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("Txxxxas"),
                ValueRef.FromString("hom"),
                ValueRef.FromInt32(2),
                ValueRef.FromInt32(4),
            }, default, default);
        Assert.Equal("Thomas", result.AsString());
    }

    [Fact]
    public async Task Overlay_DefaultCount_UsesLengthOfNew()
    {
        OverlayFunction function = new();
        // overlay('abcdef','XYZ',2) → 'aXYZef'  (replaces 3 chars from pos 2)
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abcdef"),
                ValueRef.FromString("XYZ"),
                ValueRef.FromInt32(2),
            }, default, default);
        Assert.Equal("aXYZef", result.AsString());
    }

    [Fact]
    public async Task Overlay_StartBeyondEnd_Appends()
    {
        OverlayFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc"),
                ValueRef.FromString("XY"),
                ValueRef.FromInt32(10),
                ValueRef.FromInt32(0),
            }, default, default);
        Assert.Equal("abcXY", result.AsString());
    }

    [Fact]
    public async Task Overlay_NegativeCount_Throws()
    {
        OverlayFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(
                new[]
                {
                    ValueRef.FromString("abc"),
                    ValueRef.FromString("X"),
                    ValueRef.FromInt32(1),
                    ValueRef.FromInt32(-1),
                }, default, default));
    }

    [Fact]
    public void Overlay_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<OverlayFunction>(registry.TryGetScalar("overlay"));
    }
}
