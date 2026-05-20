using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

/// <summary>
/// Tests for <see cref="ConcatStrictFunction"/> — the null-propagating
/// concat variant that backs the SQL-92 <c>||</c> operator. Mirrors
/// <c>ConcatFunctionTests</c> but flips the null-handling assertions:
/// any null operand yields a null result.
/// </summary>
public sealed class ConcatStrictFunctionTests
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("concat_strict", ConcatStrictFunction.Name);
        Assert.Equal(FunctionCategory.String, ConcatStrictFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(ConcatStrictFunction.Description));
    }

    [Fact]
    public void Execute_TwoStrings_ReturnsConcatenation()
    {
        ValueRef result = Invoke(ValueRef.FromString("hello "), ValueRef.FromString("world"));
        Assert.False(result.IsNull);
        Assert.Equal("hello world", result.AsString());
    }

    [Fact]
    public void Execute_AnyNullArgument_PropagatesNull()
    {
        ValueRef result = Invoke(
            ValueRef.FromString("a"),
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("b"));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void Execute_LeadingNull_PropagatesNull()
    {
        // Mirrors the most common chain pattern: 'prefix: ' || maybe_null
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("b"));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Execute_AllNonNull_ConcatsAll()
    {
        ValueRef result = Invoke(
            ValueRef.FromString("a"),
            ValueRef.FromString("b"),
            ValueRef.FromString("c"));
        Assert.Equal("abc", result.AsString());
    }

    [Fact]
    public void RegisteredInRegistry_ResolvesByName()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("concat_strict");
        Assert.NotNull(function);
        Assert.IsType<ConcatStrictFunction>(function);
    }

    private static ValueRef Invoke(params ValueRef[] arguments)
    {
        ConcatStrictFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
