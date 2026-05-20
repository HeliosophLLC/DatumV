using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

/// <summary>
/// Tests for <see cref="UpperFunction"/> and <see cref="LowerFunction"/>.
/// </summary>
public sealed class UpperLowerFunctionTests
{
    // ─── upper ─────────────────────────────────────────────────────────────

    [Fact]
    public void Upper_Metadata_ExposesFields()
    {
        Assert.Equal("upper", UpperFunction.Name);
        Assert.Equal(FunctionCategory.String, UpperFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(UpperFunction.Description));
    }

    [Fact]
    public void Upper_Validate_AcceptsString()
    {
        Assert.Equal(DataKind.String, new UpperFunction().ValidateArguments([DataKind.String]));
    }

    [Theory]
    [InlineData("hello", "HELLO")]
    [InlineData("Hello, World!", "HELLO, WORLD!")]
    [InlineData("", "")]
    [InlineData("ALREADYUPPER", "ALREADYUPPER")]
    public async Task Upper_Execute_ConvertsToUpper(string input, string expected)
    {
        UpperFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = await function.ExecuteAsync(new[] { ValueRef.FromString(input) }, frame, default);
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Upper_Execute_NullInput_ReturnsNull()
    {
        UpperFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = await function.ExecuteAsync(new[] { ValueRef.Null(DataKind.String) }, frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void Upper_Validate_RejectsWrongArity()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new UpperFunction().ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(
            () => new UpperFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Upper_Validate_RejectsNonString()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new UpperFunction().ValidateArguments([DataKind.Int32]));
    }

    [Fact]
    public void Upper_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<UpperFunction>(registry.TryGetScalar("upper"));
    }

    // ─── lower ─────────────────────────────────────────────────────────────

    [Fact]
    public void Lower_Metadata_ExposesFields()
    {
        Assert.Equal("lower", LowerFunction.Name);
        Assert.Equal(FunctionCategory.String, LowerFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(LowerFunction.Description));
    }

    [Fact]
    public void Lower_Validate_AcceptsString()
    {
        Assert.Equal(DataKind.String, new LowerFunction().ValidateArguments([DataKind.String]));
    }

    [Theory]
    [InlineData("HELLO", "hello")]
    [InlineData("Hello, World!", "hello, world!")]
    [InlineData("", "")]
    [InlineData("alreadylower", "alreadylower")]
    public async Task Lower_Execute_ConvertsToLower(string input, string expected)
    {
        LowerFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = await function.ExecuteAsync(new[] { ValueRef.FromString(input) }, frame, default);
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Lower_Execute_NullInput_ReturnsNull()
    {
        LowerFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = await function.ExecuteAsync(new[] { ValueRef.Null(DataKind.String) }, frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void Lower_Validate_RejectsWrongArity()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new LowerFunction().ValidateArguments([]));
    }

    [Fact]
    public void Lower_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<LowerFunction>(registry.TryGetScalar("lower"));
    }
}
