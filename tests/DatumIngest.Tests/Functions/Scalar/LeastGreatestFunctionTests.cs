using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Math;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

public sealed class LeastGreatestFunctionTests
{
    [Fact]
    public void Least_Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("least", LeastFunction.Name);
        Assert.Equal(FunctionCategory.Numeric, LeastFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(LeastFunction.Description));
    }

    [Fact]
    public void Greatest_Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("greatest", GreatestFunction.Name);
        Assert.Equal(FunctionCategory.Numeric, GreatestFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(GreatestFunction.Description));
    }

    [Fact]
    public void Validate_Least_RejectsLessThanTwoArgs()
    {
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => new LeastFunction().ValidateArguments([DataKind.Int32]));
        Assert.Contains("least", ex.Message);
    }

    [Fact]
    public void Validate_Greatest_RejectsLessThanTwoArgs()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new GreatestFunction().ValidateArguments([DataKind.Int32]));
    }

    [Fact]
    public void Validate_AcceptsMixedNumeric_PromotesToWidest()
    {
        DataKind least = new LeastFunction().ValidateArguments(
            [DataKind.Float64, DataKind.Int16]);
        DataKind greatest = new GreatestFunction().ValidateArguments(
            [DataKind.Int16, DataKind.Float64]);
        Assert.Equal(DataKind.Float64, least);
        Assert.Equal(DataKind.Float64, greatest);
    }

    [Fact]
    public void Validate_AcceptsSameKindString()
    {
        DataKind kind = new LeastFunction().ValidateArguments(
            [DataKind.String, DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, kind);
    }

    [Fact]
    public void Validate_RejectsMixedFamilies()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new LeastFunction().ValidateArguments([DataKind.String, DataKind.Int32]));
    }

    [Fact]
    public void Validate_RejectsNonComparableKinds()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new LeastFunction().ValidateArguments([DataKind.Image, DataKind.Image]));
    }

    [Fact]
    public void Least_ReturnsSmallestSameKindInteger()
    {
        ValueRef result = InvokeLeast(
            ValueRef.FromInt32(3),
            ValueRef.FromInt32(1),
            ValueRef.FromInt32(7));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public void Greatest_ReturnsLargestSameKindInteger()
    {
        ValueRef result = InvokeGreatest(
            ValueRef.FromInt32(3),
            ValueRef.FromInt32(1),
            ValueRef.FromInt32(7));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(7, result.AsInt32());
    }

    [Fact]
    public void Least_SkipsNullArguments()
    {
        ValueRef result = InvokeLeast(
            ValueRef.Null(DataKind.Int32),
            ValueRef.FromInt32(5),
            ValueRef.Null(DataKind.Int32),
            ValueRef.FromInt32(2));
        Assert.False(result.IsNull);
        Assert.Equal(2, result.AsInt32());
    }

    [Fact]
    public void Greatest_SkipsNullArguments()
    {
        ValueRef result = InvokeGreatest(
            ValueRef.Null(DataKind.Int32),
            ValueRef.FromInt32(5),
            ValueRef.FromInt32(8),
            ValueRef.Null(DataKind.Int32));
        Assert.False(result.IsNull);
        Assert.Equal(8, result.AsInt32());
    }

    [Fact]
    public void Least_AllNullsReturnsTypedNull()
    {
        ValueRef result = InvokeLeast(
            ValueRef.Null(DataKind.Int32),
            ValueRef.Null(DataKind.Int32));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public void Greatest_AllNullsReturnsTypedNull()
    {
        ValueRef result = InvokeGreatest(
            ValueRef.Null(DataKind.Float64),
            ValueRef.Null(DataKind.Float64));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
    }

    [Fact]
    public void Least_MixedNumeric_PromotesResultToWidestKind()
    {
        // least(2::Int16, 1.5::Float64) → 1.5 (Float64).
        ValueRef result = InvokeLeast(
            ValueRef.FromInline(DataValue.FromInt16(2)),
            ValueRef.FromFloat64(1.5));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(1.5, result.AsFloat64());
    }

    [Fact]
    public void Greatest_MixedNumeric_PromotesResultToWidestKind()
    {
        // greatest(2::Int16, 1.5::Float64) → 2 (Float64).
        ValueRef result = InvokeGreatest(
            ValueRef.FromInline(DataValue.FromInt16(2)),
            ValueRef.FromFloat64(1.5));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(2.0, result.AsFloat64());
    }

    [Fact]
    public void Least_String_LexicographicOrder()
    {
        ValueRef result = InvokeLeast(
            ValueRef.FromString("banana"),
            ValueRef.FromString("apple"),
            ValueRef.FromString("cherry"));
        Assert.False(result.IsNull);
        Assert.Equal("apple", result.AsString());
    }

    [Fact]
    public void Greatest_String_LexicographicOrder()
    {
        ValueRef result = InvokeGreatest(
            ValueRef.FromString("banana"),
            ValueRef.FromString("apple"),
            ValueRef.FromString("cherry"));
        Assert.False(result.IsNull);
        Assert.Equal("cherry", result.AsString());
    }

    [Fact]
    public void Least_Date_ChronologicalOrder()
    {
        ValueRef earlier = ValueRef.FromInline(DataValue.FromDate(new DateOnly(2024, 1, 1)));
        ValueRef later = ValueRef.FromInline(DataValue.FromDate(new DateOnly(2026, 12, 31)));
        ValueRef result = InvokeLeast(later, earlier);
        Assert.Equal(new DateOnly(2024, 1, 1), result.AsDate());
    }

    [Fact]
    public void Greatest_Decimal_SameKindOrdering()
    {
        ValueRef result = InvokeGreatest(
            ValueRef.FromInline(DataValue.FromDecimal(1.25m)),
            ValueRef.FromInline(DataValue.FromDecimal(9.99m)),
            ValueRef.FromInline(DataValue.FromDecimal(3.5m)));
        Assert.Equal(9.99m, result.AsDecimal());
    }

    [Fact]
    public void Least_RegisteredInRegistry_ResolvesByName()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("least");
        Assert.NotNull(function);
        Assert.IsType<LeastFunction>(function);
    }

    [Fact]
    public void Greatest_RegisteredInRegistry_ResolvesByName()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("greatest");
        Assert.NotNull(function);
        Assert.IsType<GreatestFunction>(function);
    }

    private static ValueRef InvokeLeast(params ValueRef[] arguments)
    {
        LeastFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }

    private static ValueRef InvokeGreatest(params ValueRef[] arguments)
    {
        GreatestFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
