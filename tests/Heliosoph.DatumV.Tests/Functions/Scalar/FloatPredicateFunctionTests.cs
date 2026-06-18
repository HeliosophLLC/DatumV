using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Math;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

/// <summary>
/// Covers the floating-point predicate trio (<c>is_nan</c>, <c>is_finite</c>,
/// <c>is_infinite</c>) and their PostgreSQL-conformant aliases (<c>isnan</c>,
/// <c>isfinite</c>).
/// </summary>
public sealed class FloatPredicateFunctionTests
{
    [Fact]
    public void Metadata_NamesAndCategories()
    {
        Assert.Equal("is_nan", IsNanFunction.Name);
        Assert.Equal("is_finite", IsFiniteFunction.Name);
        Assert.Equal("is_infinite", IsInfiniteFunction.Name);
        Assert.Equal(FunctionCategory.Utility, IsNanFunction.Category);
        Assert.Equal(FunctionCategory.Utility, IsFiniteFunction.Category);
        Assert.Equal(FunctionCategory.Utility, IsInfiniteFunction.Category);
    }

    [Fact]
    public void IsNan_Validate_RejectsNonFloat()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new IsNanFunction().ValidateArguments([DataKind.Int32]));
    }

    [Fact]
    public void IsNan_Float32Nan_ReturnsTrue()
    {
        Assert.True(InvokeIsNan(ValueRef.FromFloat32(float.NaN)).AsBoolean());
    }

    [Fact]
    public void IsNan_Float64Finite_ReturnsFalse()
    {
        Assert.False(InvokeIsNan(ValueRef.FromFloat64(1.5)).AsBoolean());
    }

    [Fact]
    public void IsNan_Float64Infinite_ReturnsFalse()
    {
        Assert.False(InvokeIsNan(ValueRef.FromFloat64(double.PositiveInfinity)).AsBoolean());
    }

    [Fact]
    public void IsNan_Null_ReturnsNullBoolean()
    {
        ValueRef result = InvokeIsNan(ValueRef.Null(DataKind.Float64));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Boolean, result.Kind);
    }

    [Fact]
    public void IsFinite_Finite_ReturnsTrue()
    {
        Assert.True(InvokeIsFinite(ValueRef.FromFloat64(0.0)).AsBoolean());
        Assert.True(InvokeIsFinite(ValueRef.FromFloat64(-1e10)).AsBoolean());
    }

    [Fact]
    public void IsFinite_Nan_ReturnsFalse()
    {
        Assert.False(InvokeIsFinite(ValueRef.FromFloat64(double.NaN)).AsBoolean());
    }

    [Fact]
    public void IsFinite_Infinity_ReturnsFalse()
    {
        Assert.False(InvokeIsFinite(ValueRef.FromFloat64(double.PositiveInfinity)).AsBoolean());
        Assert.False(InvokeIsFinite(ValueRef.FromFloat64(double.NegativeInfinity)).AsBoolean());
    }

    [Fact]
    public void IsFinite_Null_ReturnsNullBoolean()
    {
        ValueRef result = InvokeIsFinite(ValueRef.Null(DataKind.Float32));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Boolean, result.Kind);
    }

    [Fact]
    public void IsInfinite_PositiveInfinity_ReturnsTrue()
    {
        Assert.True(InvokeIsInfinite(ValueRef.FromFloat64(double.PositiveInfinity)).AsBoolean());
    }

    [Fact]
    public void IsInfinite_NegativeInfinity_ReturnsTrue()
    {
        Assert.True(InvokeIsInfinite(ValueRef.FromFloat64(double.NegativeInfinity)).AsBoolean());
    }

    [Fact]
    public void IsInfinite_Finite_ReturnsFalse()
    {
        Assert.False(InvokeIsInfinite(ValueRef.FromFloat64(42.0)).AsBoolean());
    }

    [Fact]
    public void IsInfinite_Nan_ReturnsFalse()
    {
        // NaN is neither finite nor infinite — both predicates must return false.
        Assert.False(InvokeIsInfinite(ValueRef.FromFloat64(double.NaN)).AsBoolean());
    }

    [Fact]
    public void IsInfinite_Null_ReturnsNullBoolean()
    {
        ValueRef result = InvokeIsInfinite(ValueRef.Null(DataKind.Float64));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Boolean, result.Kind);
    }

    [Fact]
    public void Registry_ResolvesByName_AndPgAliases()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<IsNanFunction>(registry.TryGetScalar("is_nan"));
        Assert.IsType<IsFiniteFunction>(registry.TryGetScalar("is_finite"));
        Assert.IsType<IsInfiniteFunction>(registry.TryGetScalar("is_infinite"));
        Assert.IsType<IsNanFunction>(registry.TryGetScalar("isnan"));
        Assert.IsType<IsFiniteFunction>(registry.TryGetScalar("isfinite"));
    }

    private static ValueRef InvokeIsNan(ValueRef arg) => Invoke(new IsNanFunction(), arg);
    private static ValueRef InvokeIsFinite(ValueRef arg) => Invoke(new IsFiniteFunction(), arg);
    private static ValueRef InvokeIsInfinite(ValueRef arg) => Invoke(new IsInfiniteFunction(), arg);

    private static ValueRef Invoke(IScalarFunction function, ValueRef arg)
    {
        EvaluationFrame frame = default;
        return function.ExecuteAsync(new[] { arg }, frame, default).GetAwaiter().GetResult();
    }
}
