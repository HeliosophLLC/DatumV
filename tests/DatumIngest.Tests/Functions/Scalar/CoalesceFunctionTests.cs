using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

public sealed class CoalesceFunctionTests
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("coalesce", CoalesceFunction.Name);
        Assert.Equal(FunctionCategory.Utility, CoalesceFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(CoalesceFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsTwoSameKindArgs()
    {
        DataKind kind = new CoalesceFunction().ValidateArguments([DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, kind);
    }

    [Fact]
    public void Validate_AcceptsManySameKindArgs()
    {
        DataKind kind = new CoalesceFunction().ValidateArguments(
            [DataKind.Int32, DataKind.Int32, DataKind.Int32, DataKind.Int32]);
        Assert.Equal(DataKind.Int32, kind);
    }

    [Fact]
    public void Validate_RejectsLessThanTwoArgs()
    {
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => new CoalesceFunction().ValidateArguments([DataKind.String]));
        Assert.Contains("coalesce", ex.Message);
    }

    [Fact]
    public void Validate_RejectsMixedFamilies()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new CoalesceFunction().ValidateArguments([DataKind.String, DataKind.Int32]));
    }

    [Fact]
    public void Validate_AcceptsMixedNumericKinds_PromotesToWidest()
    {
        DataKind kind = new CoalesceFunction().ValidateArguments(
            [DataKind.Float64, DataKind.Int16]);
        Assert.Equal(DataKind.Float64, kind);
    }

    [Fact]
    public void Validate_MixedIntegerKinds_PromotesToInt32OrWider()
    {
        DataKind kind = new CoalesceFunction().ValidateArguments(
            [DataKind.Int16, DataKind.Int8]);
        Assert.Equal(DataKind.Int32, kind);
    }

    [Fact]
    public void Execute_FirstArgNonNull_ReturnsFirstArg()
    {
        ValueRef result = Invoke(ValueRef.FromString("a"), ValueRef.FromString("b"));
        Assert.False(result.IsNull);
        Assert.Equal("a", result.AsString());
    }

    [Fact]
    public void Execute_FirstNull_ReturnsSecond()
    {
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("b"));
        Assert.False(result.IsNull);
        Assert.Equal("b", result.AsString());
    }

    [Fact]
    public void Execute_LeadingNulls_ReturnsFirstNonNull()
    {
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.String),
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("third"),
            ValueRef.FromString("fourth"));
        Assert.False(result.IsNull);
        Assert.Equal("third", result.AsString());
    }

    [Fact]
    public void Execute_AllNulls_ReturnsTypedNull()
    {
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.Int32),
            ValueRef.Null(DataKind.Int32));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public void Execute_MixedNumeric_FirstNonNullCoercedToWidestKind()
    {
        // coalesce(NULL_Float64, 12::Int16) — the literal-narrowing case.
        // Result must come out as Float64 to match the planner-resolved kind.
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.Float64),
            ValueRef.FromInline(DataValue.FromInt16(12)));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(12.0, result.AsFloat64());
    }

    [Fact]
    public void Execute_MixedNumeric_AllNullsReturnsTypedPromotedNull()
    {
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.Float64),
            ValueRef.Null(DataKind.Int16));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
    }

    [Fact]
    public void Execute_PreservesKindOfReturnedValue()
    {
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.Int32),
            ValueRef.FromInt32(42));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public void RegisteredInRegistry_ResolvesByName()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("coalesce");
        Assert.NotNull(function);
        Assert.IsType<CoalesceFunction>(function);
    }

    [Fact]
    public void RegisteredInRegistry_DescriptorIsAvailable()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        FunctionDescriptor? descriptor = registry.TryGetScalarDescriptor("coalesce");
        Assert.NotNull(descriptor);
        Assert.Equal("coalesce", descriptor.PrimaryName);
        Assert.Equal(FunctionCategory.Utility, descriptor.Category);
        Assert.Equal(2, descriptor.Signatures.Count);
    }

    private static ValueRef Invoke(params ValueRef[] arguments)
    {
        CoalesceFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
