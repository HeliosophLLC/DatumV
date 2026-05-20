using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

/// <summary>
/// Tests for <see cref="ConcatFunction"/> — the pilot implementation that
/// proves the ValueRef + static-abstract-metadata interface end-to-end.
/// </summary>
public sealed class ConcatFunctionTests
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("concat", ConcatFunction.Name);
        Assert.Equal(FunctionCategory.String, ConcatFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ConcatFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsTwoStrings()
    {
        DataKind kind = new ConcatFunction().ValidateArguments([DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, kind);
    }

    [Fact]
    public void Validate_AcceptsManyStrings()
    {
        DataKind kind = new ConcatFunction().ValidateArguments(
            [DataKind.String, DataKind.String, DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, kind);
    }

    [Fact]
    public void Validate_RejectsLessThanTwoArgs()
    {
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => new ConcatFunction().ValidateArguments([DataKind.String]));
        Assert.Contains("concat", ex.Message);
    }

    [Fact]
    public void Validate_RejectsNonStringArg()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new ConcatFunction().ValidateArguments([DataKind.String, DataKind.Int32]));
    }

    [Fact]
    public void Execute_TwoShortStrings_ReturnsConcatenation()
    {
        ValueRef result = Invoke(ValueRef.FromString("hello "), ValueRef.FromString("world"));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal("hello world", result.AsString());
    }

    [Fact]
    public void Execute_VariadicThreeStrings_ReturnsConcatenation()
    {
        ValueRef result = Invoke(
            ValueRef.FromString("a"),
            ValueRef.FromString("b"),
            ValueRef.FromString("c"));
        Assert.Equal("abc", result.AsString());
    }

    [Fact]
    public void Execute_SkipsNullArguments()
    {
        ValueRef result = Invoke(
            ValueRef.FromString("a"),
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("b"));
        Assert.False(result.IsNull);
        Assert.Equal("ab", result.AsString());
    }

    [Fact]
    public void Execute_AllNulls_ReturnsEmptyString()
    {
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.String),
            ValueRef.Null(DataKind.String));
        Assert.False(result.IsNull);
        Assert.Equal(string.Empty, result.AsString());
    }

    [Fact]
    public void Execute_LongStrings_ResultExceedsInlineCapacity()
    {
        string a = new('a', 64);
        string b = new('b', 64);
        ValueRef result = Invoke(ValueRef.FromString(a), ValueRef.FromString(b));
        Assert.Equal(a + b, result.AsString());
    }

    [Fact]
    public void RegisteredInRegistry_ResolvesByName()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("concat");
        Assert.NotNull(function);
        Assert.IsType<ConcatFunction>(function);
    }

    [Fact]
    public void RegisteredInRegistry_DescriptorIsAvailable()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        FunctionDescriptor? descriptor = registry.TryGetScalarDescriptor("concat");
        Assert.NotNull(descriptor);
        Assert.Equal("concat", descriptor.PrimaryName);
        Assert.Equal(FunctionCategory.String, descriptor.Category);
        Assert.Single(descriptor.Signatures);
    }

    private static ValueRef Invoke(params ValueRef[] arguments)
    {
        ConcatFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
