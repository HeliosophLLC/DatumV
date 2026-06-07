using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="NormalizeFunction"/>, <see cref="ToAsciiFunction"/>,
/// <see cref="UnistrFunction"/>, and <see cref="CasefoldFunction"/>.
/// </summary>
public sealed class UnicodeFunctionsTests
{
    // ─── normalize ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Normalize_DecomposedToNfc_Composes()
    {
        // "cafe" + COMBINING ACUTE ACCENT (U+0301) → 'café' (precomposed U+00E9)
        string decomposed = "café";
        NormalizeFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(decomposed), ValueRef.FromString("NFC") }, default, default);
        Assert.Equal("café", result.AsString());
        Assert.Equal(4, result.AsString().EnumerateRunes().Count());
    }

    [Fact]
    public async Task Normalize_DefaultsToNfc()
    {
        NormalizeFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("café") }, default, default);
        Assert.Equal("café", result.AsString());
    }

    [Fact]
    public async Task Normalize_NfkdDecomposesLigatures()
    {
        NormalizeFunction function = new();
        // U+FB01 (ﬁ ligature) → 'fi' under NFKD.
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("ofﬁce"), ValueRef.FromString("NFKD") }, default, default);
        Assert.Equal("office", result.AsString());
    }

    [Fact]
    public async Task Normalize_UnknownForm_Throws()
    {
        NormalizeFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(
                new[] { ValueRef.FromString("x"), ValueRef.FromString("NFG") }, default, default));
    }

    [Fact]
    public void Normalize_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<NormalizeFunction>(registry.TryGetScalar("normalize"));
    }

    // ─── to_ascii ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("café", "cafe")]
    [InlineData("naïve", "naive")]
    [InlineData("hello", "hello")]
    public async Task ToAscii_StripsDiacritics(string s, string expected)
    {
        ToAsciiFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void ToAscii_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<ToAsciiFunction>(registry.TryGetScalar("to_ascii"));
    }

    // ─── unistr ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"d\0061t\+000061", "data")]
    [InlineData(@"é", "é")]
    [InlineData(@"\U0001F600", "😀")]
    [InlineData(@"a\\b", @"a\b")]
    [InlineData("no escapes here", "no escapes here")]
    public async Task Unistr_Cases(string input, string expected)
    {
        UnistrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(input) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData(@"\xy")]      // 'x' not a valid hex digit and not a recognised escape
    [InlineData(@"\u00")]      // truncated
    [InlineData(@"\")]          // dangling backslash
    public async Task Unistr_Invalid_Throws(string input)
    {
        UnistrFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(new[] { ValueRef.FromString(input) }, default, default));
    }

    [Fact]
    public void Unistr_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<UnistrFunction>(registry.TryGetScalar("unistr"));
    }

    // ─── casefold ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Straße", "strasse")]
    [InlineData("Hello World", "hello world")]
    [InlineData("ﬁreﬂy", "firefly")]
    public async Task Casefold_Cases(string s, string expected)
    {
        CasefoldFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(s) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Casefold_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<CasefoldFunction>(registry.TryGetScalar("casefold"));
    }
}
