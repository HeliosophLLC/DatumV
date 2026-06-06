using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for <see cref="ToHexFunction"/>, <see cref="ToBinFunction"/>, and
/// <see cref="ToOctFunction"/>.
/// </summary>
public sealed class BaseConversionFunctionsTests
{
    [Theory]
    [InlineData(255, "ff")]
    [InlineData(0, "0")]
    [InlineData(16, "10")]
    [InlineData(int.MaxValue, "7fffffff")]
    [InlineData(-1, "ffffffff")]
    public async Task ToHex_Int32(int value, string expected)
    {
        ToHexFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromInt32(value) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task ToHex_Int64_NegativeUsesTwosComplement()
    {
        ToHexFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromInt64(-1L) }, default, default);
        Assert.Equal("ffffffffffffffff", result.AsString());
    }

    [Theory]
    [InlineData(10, "1010")]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(255, "11111111")]
    public async Task ToBin_Int32(int value, string expected)
    {
        ToBinFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromInt32(value) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData(8, "10")]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    [InlineData(64, "100")]
    public async Task ToOct_Int32(int value, string expected)
    {
        ToOctFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromInt32(value) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void All_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<ToHexFunction>(registry.TryGetScalar("to_hex"));
        Assert.IsType<ToBinFunction>(registry.TryGetScalar("to_bin"));
        Assert.IsType<ToOctFunction>(registry.TryGetScalar("to_oct"));
    }
}
