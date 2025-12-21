using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for <see cref="CastFunction"/>, <see cref="TryCastFunction"/>,
/// and <see cref="TypeofFunction"/>.
/// </summary>
public sealed class CastFunctionTests
{
    private static readonly EvaluationFrame Frame = default;

    // ─── cast ──────────────────────────────────────────────────────────────

    [Fact]
    public void Cast_Metadata()
    {
        Assert.Equal("cast", CastFunction.Name);
        Assert.Equal(FunctionCategory.Conversion, CastFunction.Category);
    }

    [Fact]
    public void Cast_Validate_AcceptsTypeLiteralTarget()
    {
        Assert.Equal(DataKind.String,
            new CastFunction().ValidateArguments([DataKind.Int32, DataKind.Type]));
    }

    [Fact]
    public void Cast_Validate_AcceptsStringTarget()
    {
        Assert.Equal(DataKind.String,
            new CastFunction().ValidateArguments([DataKind.Int32, DataKind.String]));
    }

    [Fact]
    public void Cast_Validate_RejectsWrongArity()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new CastFunction().ValidateArguments([DataKind.Int32]));
    }

    [Fact]
    public void Cast_Validate_RejectsBadTargetKind()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new CastFunction().ValidateArguments([DataKind.Int32, DataKind.Float32]));
    }

    [Fact]
    public async Task Cast_NullInput_ReturnsTypedNull()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Int32), ValueRef.FromType(DataKind.Float64) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
    }

    [Fact]
    public async Task Cast_SameKind_PassesThrough()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(42), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.Equal(42, result.AsInt32());
    }

    [Theory]
    [InlineData(DataKind.Int32, DataKind.Float64, 42, 42.0)]
    [InlineData(DataKind.Int32, DataKind.Int64, 42, 42L)]
    public async Task Cast_NumericToNumeric(DataKind sourceKind, DataKind targetKind, int sourceValue, object expected)
    {
        ValueRef source = sourceKind == DataKind.Int32 ? ValueRef.FromInt32(sourceValue) : ValueRef.FromInt64(sourceValue);
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { source, ValueRef.FromType(targetKind) },
            Frame, default);
        Assert.Equal(targetKind, result.Kind);
        if (targetKind == DataKind.Float64)
        {
            Assert.Equal((double)expected, result.AsFloat64());
        }
        else if (targetKind == DataKind.Int64)
        {
            Assert.Equal((long)expected, result.AsInt64());
        }
    }

    [Fact]
    public async Task Cast_NumericToString()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(42), ValueRef.FromType(DataKind.String) },
            Frame, default);
        Assert.Equal("42", result.AsString());
    }

    [Fact]
    public async Task Cast_StringToInt32()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("123"), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.Equal(123, result.AsInt32());
    }

    [Fact]
    public async Task Cast_StringToFloat64()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("3.14"), ValueRef.FromType(DataKind.Float64) },
            Frame, default);
        Assert.Equal(3.14, result.AsFloat64(), precision: 5);
    }

    [Fact]
    public async Task Cast_BooleanToInt32()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromBoolean(true), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public async Task Cast_StringToBoolean()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("true"), ValueRef.FromType(DataKind.Boolean) },
            Frame, default);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public async Task Cast_StringToDate()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("2026-04-29"), ValueRef.FromType(DataKind.Date) },
            Frame, default);
        Assert.Equal(new DateOnly(2026, 4, 29), result.AsDate());
    }

    [Fact]
    public async Task Cast_TargetAsString_AcceptsNameAndAlias()
    {
        ValueRef byName = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(1), ValueRef.FromString("Float64") },
            Frame, default);
        Assert.Equal(DataKind.Float64, byName.Kind);

        ValueRef byAlias = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(1), ValueRef.FromString("bool") },
            Frame, default);
        Assert.Equal(DataKind.Boolean, byAlias.Kind);
    }

    [Fact]
    public async Task Cast_UnsupportedPair_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { ValueRef.FromUuid(Guid.NewGuid()), ValueRef.FromType(DataKind.Int32) },
                Frame, default));
        Assert.Contains("does not support", ex.Message);
    }

    [Fact]
    public async Task Cast_ScalarToArrayAnnotation_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { ValueRef.FromString("1,2,3"), ValueRef.FromString("Array<Int32>") },
                Frame, default));
        // Error message points users at the right path — array construction
        // is a separate concern from type conversion.
        Assert.Contains("requires the source to already be Array", ex.Message);
        Assert.Contains("string_split", ex.Message);
    }

    [Fact]
    public async Task Cast_ArrayToScalarAnnotation_Throws()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2)]);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { arr, ValueRef.FromType(DataKind.Int32) },
                Frame, default));
        Assert.Contains("cannot convert Array<Int32>", ex.Message);
    }

    [Fact]
    public async Task Cast_ArrayToSameArrayAnnotation_PassesThrough()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.String,
            [ValueRef.FromString("a"), ValueRef.FromString("b")]);
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { arr, ValueRef.FromString("Array<String>") },
            Frame, default);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public async Task Cast_ArrayToDifferentArrayAnnotation_Throws()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2)]);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { arr, ValueRef.FromString("Array<Float64>") },
                Frame, default));
        Assert.Contains("requires the source to already be Array<Float64>", ex.Message);
    }

    [Fact]
    public async Task Cast_NullSourceToArrayAnnotation_ReturnsTypedNullArray()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromString("Array<String>") },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.String, result.Kind);
    }

    // ─── try_cast ──────────────────────────────────────────────────────────

    [Fact]
    public void TryCast_Metadata()
    {
        Assert.Equal("try_cast", TryCastFunction.Name);
    }

    [Fact]
    public async Task TryCast_NullInput_ReturnsTypedNull()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task TryCast_ValidConversion_Succeeds()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("42"), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.False(result.IsNull);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public async Task TryCast_BadParse_ReturnsNull()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("not-a-number"), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task TryCast_UnsupportedPair_ReturnsNull()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.FromUuid(Guid.NewGuid()), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    // ─── typeof ────────────────────────────────────────────────────────────

    [Fact]
    public void Typeof_Metadata()
    {
        Assert.Equal("typeof", TypeofFunction.Name);
    }

    [Theory]
    [InlineData(DataKind.Int32)]
    [InlineData(DataKind.String)]
    [InlineData(DataKind.Float64)]
    public async Task Typeof_ReturnsKind(DataKind expected)
    {
        ValueRef input = expected switch
        {
            DataKind.Int32 => ValueRef.FromInt32(0),
            DataKind.String => ValueRef.FromString("x"),
            DataKind.Float64 => ValueRef.FromFloat64(0),
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };
        ValueRef result = await new TypeofFunction().ExecuteAsync(new[] { input }, Frame, default);
        Assert.Equal(DataKind.Type, result.Kind);
        Assert.Equal(expected, result.AsType());
    }

    [Fact]
    public async Task Typeof_OnNull_ReturnsKind()
    {
        ValueRef result = await new TypeofFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Int32) },
            Frame, default);
        Assert.Equal(DataKind.Int32, result.AsType());
    }

    [Fact]
    public void Typeof_QueryUnitCost_IsZero()
    {
        Assert.Equal(0, new TypeofFunction().QueryUnitCost);
    }

    // ─── registry ──────────────────────────────────────────────────────────

    [Fact]
    public void RegistrySees_AllThreeFunctions()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<CastFunction>(registry.TryGetScalar("cast"));
        Assert.IsType<TryCastFunction>(registry.TryGetScalar("try_cast"));
        Assert.IsType<TypeofFunction>(registry.TryGetScalar("typeof"));
    }
}
