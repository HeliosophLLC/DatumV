using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public class TypeCoercionTests : ServiceTestBase
{
    [Fact]
    public void UInt8WidensToInt16()
    {
        DataValue uint8 = DataValue.FromUInt8(200);
        DataValue int16 = TypeCoercion.Widen(uint8, DataKind.Int16);

        Assert.Equal(DataKind.Int16, int16.Kind);
        Assert.Equal((short)200, int16.AsInt16());
    }

    // ScalarWidensToVectorOfLengthOne and the Float64 → Vector CanWiden assertion
    // were retired alongside DataKind.Vector. The widening chain still terminates
    // numeric coercions at Float64; restoring "scalar → 1-element typed array"
    // requires a (DataKind, IsArray) target descriptor in TypeCoercion's API.

    [Fact]
    public void CanWiden_ReturnsTrueForValidWidening()
    {
        Assert.True(TypeCoercion.CanWiden(DataKind.UInt8, DataKind.Int16));
        Assert.True(TypeCoercion.CanWiden(DataKind.UInt8, DataKind.Float64));
        Assert.True(TypeCoercion.CanWiden(DataKind.Float32, DataKind.Float64));
    }

    [Fact]
    public void CanWiden_ReturnsFalseForInvalidWidening()
    {
        Assert.False(TypeCoercion.CanWiden(DataKind.String, DataKind.Float32));
        Assert.False(TypeCoercion.CanWiden(DataKind.Float32, DataKind.UInt8));
        Assert.False(TypeCoercion.CanWiden(DataKind.Image, DataKind.Float32));
    }

    [Fact]
    public void CanWiden_SameKindReturnsTrue()
    {
        Assert.True(TypeCoercion.CanWiden(DataKind.Float32, DataKind.Float32));
        Assert.True(TypeCoercion.CanWiden(DataKind.String, DataKind.String));
    }

    [Fact]
    public void Widen_ThrowsOnInvalidWidening()
    {
        DataValue str = DataValue.FromString("hello");

        Assert.Throws<InvalidOperationException>(() => TypeCoercion.Widen(str, DataKind.Float32));
    }

    [Fact]
    public void Widen_NullRemainsNull()
    {
        DataValue nullValue = DataValue.Null(DataKind.UInt8);
        DataValue result = TypeCoercion.Widen(nullValue, DataKind.Float32);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void FindCommonKind_ReturnsWiderType()
    {
        Assert.Equal(DataKind.Int16, TypeCoercion.FindCommonKind(DataKind.UInt8, DataKind.Int8));
        Assert.Equal(DataKind.Float64, TypeCoercion.FindCommonKind(DataKind.UInt8, DataKind.Float32));
        Assert.Equal(DataKind.Float64, TypeCoercion.FindCommonKind(DataKind.Float32, DataKind.UInt8));
        Assert.Equal(DataKind.Float64, TypeCoercion.FindCommonKind(DataKind.Int32, DataKind.Float32));
        Assert.Equal(DataKind.Int32, TypeCoercion.FindCommonKind(DataKind.Int32, DataKind.UInt16));
        Assert.Equal(DataKind.Int64, TypeCoercion.FindCommonKind(DataKind.Int64, DataKind.UInt32));
    }

    [Fact]
    public void FindCommonKind_ReturnsNullForIncompatibleTypes()
    {
        DataKind? result = TypeCoercion.FindCommonKind(DataKind.String, DataKind.Float32);

        Assert.Null(result);
    }

    [Fact]
    public void FindCommonKind_SameTypeReturnsSame()
    {
        Assert.Equal(DataKind.String, TypeCoercion.FindCommonKind(DataKind.String, DataKind.String));
    }

    // ─────────────── FindCommonShape ───────────────

    [Fact]
    public void FindCommonShape_ScalarScalar_WidensKindLikeFindCommonKind()
    {
        var result = TypeCoercion.FindCommonShape(
            DataKind.Int32, isArrayA: false, isMultiDimA: false,
            DataKind.Int64, isArrayB: false, isMultiDimB: false);

        Assert.NotNull(result);
        Assert.Equal(DataKind.Int64, result.Value.Kind);
        Assert.False(result.Value.IsArray);
        Assert.False(result.Value.IsMultiDim);
    }

    [Fact]
    public void FindCommonShape_ArrayArray_SameElement_Unifies()
    {
        var result = TypeCoercion.FindCommonShape(
            DataKind.Float32, isArrayA: true, isMultiDimA: false,
            DataKind.Float32, isArrayB: true, isMultiDimB: false);

        Assert.NotNull(result);
        Assert.Equal(DataKind.Float32, result.Value.Kind);
        Assert.True(result.Value.IsArray);
    }

    [Fact]
    public void FindCommonShape_ArrayArray_WideningElementKind_Unifies()
    {
        // Int32[] + Int64[] → Int64[] — same widening rules as scalars,
        // applied to the per-element kind.
        var result = TypeCoercion.FindCommonShape(
            DataKind.Int32, isArrayA: true, isMultiDimA: false,
            DataKind.Int64, isArrayB: true, isMultiDimB: false);

        Assert.NotNull(result);
        Assert.Equal(DataKind.Int64, result.Value.Kind);
        Assert.True(result.Value.IsArray);
    }

    [Fact]
    public void FindCommonShape_ArrayPlusScalar_Rejected()
    {
        var result = TypeCoercion.FindCommonShape(
            DataKind.Float32, isArrayA: true, isMultiDimA: false,
            DataKind.Float32, isArrayB: false, isMultiDimB: false);

        Assert.Null(result);
    }

    [Fact]
    public void FindCommonShape_MultiDimMismatch_Rejected()
    {
        var result = TypeCoercion.FindCommonShape(
            DataKind.Float32, isArrayA: true, isMultiDimA: true,
            DataKind.Float32, isArrayB: true, isMultiDimB: false);

        Assert.Null(result);
    }

    [Fact]
    public void FindCommonShape_IncompatibleElementKinds_Rejected()
    {
        var result = TypeCoercion.FindCommonShape(
            DataKind.String, isArrayA: false, isMultiDimA: false,
            DataKind.Int32, isArrayB: false, isMultiDimB: false);

        Assert.Null(result);
    }

    // ─────────────── CoerceValue ───────────────

    [Fact]
    public void CoerceValue_SameKind_ReturnsSameValue()
    {
        DataValue value = DataValue.FromFloat32(42f);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Float32);
        Assert.Equal(42f, result.AsFloat32());
    }

    [Fact]
    public void CoerceValue_Null_ReturnsTypedNull()
    {
        DataValue value = DataValue.Null(DataKind.String);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Float32);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void CoerceValue_WideningChain_BooleanToFloat64()
    {
        DataValue value = DataValue.FromBoolean(true);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Float64);
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(1.0, result.AsFloat64());
    }

    [Fact]
    public void CoerceValue_StringToScalar_ParsesNumber()
    {
        DataValue value = DataValue.FromString("3.14");
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Float32);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal(3.14f, result.AsFloat32(), 0.001f);
    }

    [Fact]
    public void CoerceValue_StringToScalar_UnparseableReturnsNull()
    {
        DataValue value = DataValue.FromString("abc");
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Float32);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void CoerceValue_StringToBoolean_ParsesTrue()
    {
        DataValue value = DataValue.FromString("true");
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Boolean);
        Assert.Equal(DataKind.Boolean, result.Kind);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CoerceValue_StringToBoolean_ParsesOne()
    {
        DataValue value = DataValue.FromString("1");
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Boolean);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CoerceValue_StringToBoolean_InvalidReturnsNull()
    {
        DataValue value = DataValue.FromString("maybe");
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Boolean);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void CoerceValue_IncompatibleKinds_ReturnsNull()
    {
        DataValue value = DataValue.FromFloat32(42f);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Date);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    // ─────────────── CanCoerceStringTo ───────────────

    [Theory]
    [InlineData(DataKind.Float32, true)]
    [InlineData(DataKind.Boolean, true)]
    [InlineData(DataKind.Date, true)]
    [InlineData(DataKind.Image, false)]
    public void CanCoerceStringTo_ReturnsExpected(DataKind kind, bool expected)
    {
        Assert.Equal(expected, TypeCoercion.CanCoerceStringTo(kind));
    }
}
