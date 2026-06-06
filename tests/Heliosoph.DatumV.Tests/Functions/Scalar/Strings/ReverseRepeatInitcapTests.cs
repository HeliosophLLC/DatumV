using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="ReverseFunction"/>, <see cref="RepeatFunction"/>,
/// and <see cref="InitcapFunction"/>.
/// </summary>
public sealed class ReverseRepeatInitcapTests
{
    // ─── reverse ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello", "olleh")]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("ab", "ba")]
    public async Task Reverse_Ascii(string s, string expected)
    {
        ReverseFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Reverse_SurrogatePair_KeepsPairIntact()
    {
        // U+1F600 GRINNING FACE = D83D DE00; with a leading 'A' we expect the
        // emoji to come first in the reversed string with its surrogates in
        // the original (high, low) order.
        string input = "A" + char.ConvertFromUtf32(0x1F600);
        string expected = char.ConvertFromUtf32(0x1F600) + "A";

        ReverseFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(input) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Reverse_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<ReverseFunction>(registry.TryGetScalar("reverse"));
    }

    // ─── repeat ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ha", 3, "hahaha")]
    [InlineData("ab", 0, "")]
    [InlineData("ab", -1, "")]
    [InlineData("", 5, "")]
    [InlineData("x", 1, "x")]
    public async Task Repeat_Cases(string s, int count, string expected)
    {
        RepeatFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromInt32(count) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Repeat_ExceedingLimit_Throws()
    {
        RepeatFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(
                new[] { ValueRef.FromString("abcdefgh"), ValueRef.FromInt32(int.MaxValue) },
                default, default));
    }

    [Fact]
    public void Repeat_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RepeatFunction>(registry.TryGetScalar("repeat"));
    }

    // ─── initcap ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello world", "Hello World")]
    [InlineData("HELLO WORLD", "Hello World")]
    [InlineData("aBc dEf", "Abc Def")]
    [InlineData("hi-there foo_bar", "Hi-There Foo_Bar")]
    [InlineData("", "")]
    [InlineData("a", "A")]
    [InlineData("123abc 456def", "123Abc 456Def")]
    public async Task Initcap_Cases(string s, string expected)
    {
        InitcapFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Initcap_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<InitcapFunction>(registry.TryGetScalar("initcap"));
    }
}
