using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="AsciiFunction"/>, <see cref="ChrFunction"/>, and
/// <see cref="TranslateFunction"/>.
/// </summary>
public sealed class CharCodeAndTranslateTests
{
    // ─── ascii ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A", 65)]
    [InlineData("abc", 97)]
    [InlineData("", 0)]
    public async Task Ascii_Cases(string s, int expected)
    {
        AsciiFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s) }, default, default);
        Assert.Equal(expected, result.AsInt32());
    }

    [Fact]
    public async Task Ascii_EmojiReturnsFullCodePoint()
    {
        AsciiFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("😀hi") }, default, default);
        Assert.Equal(0x1F600, result.AsInt32());
    }

    [Fact]
    public void Ascii_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<AsciiFunction>(registry.TryGetScalar("ascii"));
    }

    // ─── chr ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(65, "A")]
    [InlineData(97, "a")]
    [InlineData(0x1F600, "😀")]
    public async Task Chr_Cases(int code, string expected)
    {
        ChrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromInt32(code) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0xD800)]
    [InlineData(0x110000)]
    public async Task Chr_InvalidCodePoint_Throws(int code)
    {
        ChrFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(new[] { ValueRef.FromInt32(code) }, default, default));
    }

    [Fact]
    public void Chr_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<ChrFunction>(registry.TryGetScalar("chr"));
    }

    // ─── translate ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12345", "143", "ax", "a2x5")]
    [InlineData("abcabc", "abc", "ABC", "ABCABC")]
    [InlineData("abcdef", "ace", "", "bdf")]            // deletes a, c, e
    [InlineData("hello", "lo", "1", "he11")]            // 'o' deleted (no counterpart)
    [InlineData("anything", "", "ignored", "anything")] // empty from = noop
    public async Task Translate_Cases(string s, string from, string to, string expected)
    {
        TranslateFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s), ValueRef.FromString(from), ValueRef.FromString(to) },
            default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Translate_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<TranslateFunction>(registry.TryGetScalar("translate"));
    }
}
