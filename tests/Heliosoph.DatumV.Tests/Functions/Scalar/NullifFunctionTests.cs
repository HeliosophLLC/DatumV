using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

public sealed class NullifFunctionTests
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("nullif", NullifFunction.Name);
        Assert.Equal(FunctionCategory.Utility, NullifFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(NullifFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsTwoSameKindArgs()
    {
        DataKind kind = new NullifFunction().ValidateArguments([DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.String, kind);
    }

    [Fact]
    public void Validate_AcceptsMixedNumeric_PromotesToWidest()
    {
        DataKind kind = new NullifFunction().ValidateArguments(
            [DataKind.Float64, DataKind.Int16]);
        Assert.Equal(DataKind.Float64, kind);
    }

    [Fact]
    public void Validate_RejectsArgCountOtherThanTwo()
    {
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => new NullifFunction().ValidateArguments([DataKind.Int32]));
        Assert.Contains("nullif", ex.Message);

        Assert.Throws<FunctionArgumentException>(
            () => new NullifFunction().ValidateArguments([DataKind.Int32, DataKind.Int32, DataKind.Int32]));
    }

    [Fact]
    public void Validate_RejectsMixedFamilies()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new NullifFunction().ValidateArguments([DataKind.String, DataKind.Int32]));
    }

    [Fact]
    public void Execute_EqualValues_ReturnsTypedNull()
    {
        ValueRef result = Invoke(ValueRef.FromInt32(7), ValueRef.FromInt32(7));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public void Execute_UnequalValues_ReturnsFirst()
    {
        ValueRef result = Invoke(ValueRef.FromString("a"), ValueRef.FromString("b"));
        Assert.False(result.IsNull);
        Assert.Equal("a", result.AsString());
    }

    [Fact]
    public void Execute_FirstNull_ReturnsTypedNull()
    {
        ValueRef result = Invoke(
            ValueRef.Null(DataKind.Int32),
            ValueRef.FromInt32(5));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public void Execute_SecondNull_ReturnsFirst()
    {
        ValueRef result = Invoke(
            ValueRef.FromInt32(5),
            ValueRef.Null(DataKind.Int32));
        Assert.False(result.IsNull);
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void Execute_MixedNumeric_EqualAfterPromotion_ReturnsNullAtWidestKind()
    {
        // nullif(12.0::Float64, 12::Int16) — both promote to Float64.
        ValueRef result = Invoke(
            ValueRef.FromFloat64(12.0),
            ValueRef.FromInline(DataValue.FromInt16(12)));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
    }

    [Fact]
    public void Execute_MixedNumeric_Unequal_ReturnsFirstCoerced()
    {
        ValueRef result = Invoke(
            ValueRef.FromInline(DataValue.FromInt16(7)),
            ValueRef.FromFloat64(0.0));
        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(7.0, result.AsFloat64());
    }

    [Fact]
    public void RegisteredInRegistry_ResolvesByName()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("nullif");
        Assert.NotNull(function);
        Assert.IsType<NullifFunction>(function);
    }

    private static ValueRef Invoke(params ValueRef[] arguments)
    {
        NullifFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
