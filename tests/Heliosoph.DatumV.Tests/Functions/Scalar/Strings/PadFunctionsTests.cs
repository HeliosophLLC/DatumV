using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="LpadFunction"/> and <see cref="RpadFunction"/> —
/// PG-compliant character-padding semantics including truncation when value
/// already exceeds the target length.
/// </summary>
public sealed class PadFunctionsTests
{
    // ─── lpad ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hi", 5, "   hi")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello", 3, "hel")]
    public async Task Lpad_DefaultFill_PadsWithSpaces(string value, int length, string expected)
    {
        LpadFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(value), ValueRef.FromInt32(length) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData("hi", 5, "xy", "xyxhi")]
    [InlineData("hi", 6, "xy", "xyxyhi")]
    [InlineData("hello", 8, "*", "***hello")]
    [InlineData("hello", 3, "x", "hel")]
    public async Task Lpad_CustomFill(string value, int length, string fill, string expected)
    {
        LpadFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(value), ValueRef.FromInt32(length), ValueRef.FromString(fill) },
            default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Lpad_EmptyFill_ReturnsValueUnchanged()
    {
        LpadFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("hi"), ValueRef.FromInt32(10), ValueRef.FromString("") },
            default, default);
        Assert.Equal("hi", result.AsString());
    }

    [Fact]
    public async Task Lpad_NullArg_ReturnsNull()
    {
        LpadFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromInt32(5) }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Lpad_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<LpadFunction>(registry.TryGetScalar("lpad"));
    }

    // ─── rpad ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hi", 5, "hi   ")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello", 3, "hel")]
    public async Task Rpad_DefaultFill_PadsWithSpaces(string value, int length, string expected)
    {
        RpadFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(value), ValueRef.FromInt32(length) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData("hi", 5, "xy", "hixyx")]
    [InlineData("hi", 6, "xy", "hixyxy")]
    [InlineData("hello", 8, "*", "hello***")]
    [InlineData("hello", 3, "x", "hel")]
    public async Task Rpad_CustomFill(string value, int length, string fill, string expected)
    {
        RpadFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(value), ValueRef.FromInt32(length), ValueRef.FromString(fill) },
            default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Rpad_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RpadFunction>(registry.TryGetScalar("rpad"));
    }
}
