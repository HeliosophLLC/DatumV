using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

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
    public void Upper_Execute_ConvertsToUpper(string input, string expected)
    {
        UpperFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = function.Execute([ValueRef.FromString(input)], in frame);
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Upper_Execute_NullInput_ReturnsNull()
    {
        UpperFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = function.Execute([ValueRef.Null(DataKind.String)], in frame);
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
    public void Lower_Execute_ConvertsToLower(string input, string expected)
    {
        LowerFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = function.Execute([ValueRef.FromString(input)], in frame);
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Lower_Execute_NullInput_ReturnsNull()
    {
        LowerFunction function = new();
        EvaluationFrame frame = default;
        ValueRef result = function.Execute([ValueRef.Null(DataKind.String)], in frame);
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
