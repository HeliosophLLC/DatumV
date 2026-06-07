using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="RegexpExtractFunction"/> and
/// <see cref="RegexpInstrFunction"/>.
/// </summary>
public sealed class RegexExtractInstrFunctionsTests
{
    // ─── regexp_extract ────────────────────────────────────────────────────

    [Fact]
    public async Task RegexpExtract_FirstMatch_NoGroup_ReturnsWholeMatch()
    {
        RegexpExtractFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.Equal("123", result.AsString());
    }

    [Fact]
    public async Task RegexpExtract_WithGroup_ReturnsCapture()
    {
        RegexpExtractFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123"),
                ValueRef.FromString(@"([a-z]+)(\d+)"),
                ValueRef.FromInt32(2),
            }, default, default);
        Assert.Equal("123", result.AsString());
    }

    [Fact]
    public async Task RegexpExtract_NoMatch_ReturnsNull()
    {
        RegexpExtractFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("hello"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RegexpExtract_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpExtractFunction>(registry.TryGetScalar("regexp_extract"));
    }

    // ─── regexp_instr ──────────────────────────────────────────────────────

    [Fact]
    public async Task RegexpInstr_FirstMatch_DefaultStart_OneBased()
    {
        RegexpInstrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.Equal(4, result.AsInt32());
    }

    [Fact]
    public async Task RegexpInstr_DocExample_CaseInsensitive()
    {
        // regexp_instr('ABCDEF', 'c(.)(..)', 1, 1, 0, 'i') → 3
        RegexpInstrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("ABCDEF"),
                ValueRef.FromString("c(.)(..)"),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(0),
                ValueRef.FromString("i"),
            }, default, default);
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public async Task RegexpInstr_NoMatch_ReturnsZero()
    {
        RegexpInstrFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("hello"),
                ValueRef.FromString(@"\d+"),
            }, default, default);
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public async Task RegexpInstr_EndOption_ReturnsPositionAfterMatch()
    {
        RegexpInstrFunction function = new();
        // 'abc123def': '123' occupies positions 4-6, end+1 = 7.
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def"),
                ValueRef.FromString(@"\d+"),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(1),
            }, default, default);
        Assert.Equal(7, result.AsInt32());
    }

    [Fact]
    public async Task RegexpInstr_NthMatch()
    {
        RegexpInstrFunction function = new();
        // 'abc123def456' — first \d+ at 4, second at 10.
        ValueRef result = await function.ExecuteAsync(
            new[]
            {
                ValueRef.FromString("abc123def456"),
                ValueRef.FromString(@"\d+"),
                ValueRef.FromInt32(1),
                ValueRef.FromInt32(2),
            }, default, default);
        Assert.Equal(10, result.AsInt32());
    }

    [Fact]
    public void RegexpInstr_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpInstrFunction>(registry.TryGetScalar("regexp_instr"));
    }
}
