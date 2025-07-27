using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public class TypeCoercionTests
{
    [Fact]
    public void UInt8WidensToScalar()
    {
        DataValue uint8 = DataValue.FromUInt8(200);
        DataValue scalar = TypeCoercion.Widen(uint8, DataKind.Scalar);

        Assert.Equal(DataKind.Scalar, scalar.Kind);
        Assert.Equal(200.0f, scalar.AsScalar());
    }

    [Fact]
    public void ScalarWidensToVectorOfLengthOne()
    {
        DataValue scalar = DataValue.FromScalar(5.0f);
        DataValue vector = TypeCoercion.Widen(scalar, DataKind.Vector);

        Assert.Equal(DataKind.Vector, vector.Kind);
        Assert.Equal([5.0f], vector.AsVector());
    }

    [Fact]
    public void VectorWidensToTensorRankOne()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        DataValue vector = DataValue.FromVector(data);
        DataValue tensor = TypeCoercion.Widen(vector, DataKind.Tensor);

        Assert.Equal(DataKind.Tensor, tensor.Kind);
        float[] tensorData = tensor.AsTensor(out int[] shape);
        Assert.Same(data, tensorData);
        Assert.Equal([3], shape);
    }

    [Fact]
    public void MatrixWidensToTensorRankTwo()
    {
        float[] data = [1.0f, 2.0f, 3.0f, 4.0f];
        DataValue matrix = DataValue.FromMatrix(data, 2, 2);
        DataValue tensor = TypeCoercion.Widen(matrix, DataKind.Tensor);

        Assert.Equal(DataKind.Tensor, tensor.Kind);
        float[] tensorData = tensor.AsTensor(out int[] shape);
        Assert.Same(data, tensorData);
        Assert.Equal([2, 2], shape);
    }

    [Fact]
    public void CanWiden_ReturnsTrueForValidWidening()
    {
        Assert.True(TypeCoercion.CanWiden(DataKind.UInt8, DataKind.Scalar));
        Assert.True(TypeCoercion.CanWiden(DataKind.Scalar, DataKind.Vector));
        Assert.True(TypeCoercion.CanWiden(DataKind.Vector, DataKind.Tensor));
        Assert.True(TypeCoercion.CanWiden(DataKind.Matrix, DataKind.Tensor));
    }

    [Fact]
    public void CanWiden_ReturnsFalseForInvalidWidening()
    {
        Assert.False(TypeCoercion.CanWiden(DataKind.String, DataKind.Scalar));
        Assert.False(TypeCoercion.CanWiden(DataKind.Scalar, DataKind.UInt8));
        Assert.False(TypeCoercion.CanWiden(DataKind.Tensor, DataKind.Vector));
        Assert.False(TypeCoercion.CanWiden(DataKind.Image, DataKind.Scalar));
    }

    [Fact]
    public void CanWiden_SameKindReturnsTrue()
    {
        Assert.True(TypeCoercion.CanWiden(DataKind.Scalar, DataKind.Scalar));
        Assert.True(TypeCoercion.CanWiden(DataKind.String, DataKind.String));
    }

    [Fact]
    public void Widen_ThrowsOnInvalidWidening()
    {
        DataValue str = DataValue.FromString("hello");

        Assert.Throws<InvalidOperationException>(() => TypeCoercion.Widen(str, DataKind.Scalar));
    }

    [Fact]
    public void Widen_NullRemainsNull()
    {
        DataValue nullValue = DataValue.Null(DataKind.UInt8);
        DataValue result = TypeCoercion.Widen(nullValue, DataKind.Scalar);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }

    [Fact]
    public void FindCommonKind_ReturnsWiderType()
    {
        Assert.Equal(DataKind.Scalar, TypeCoercion.FindCommonKind(DataKind.UInt8, DataKind.Scalar));
        Assert.Equal(DataKind.Scalar, TypeCoercion.FindCommonKind(DataKind.Scalar, DataKind.UInt8));
        Assert.Equal(DataKind.Tensor, TypeCoercion.FindCommonKind(DataKind.Vector, DataKind.Matrix));
    }

    [Fact]
    public void FindCommonKind_ReturnsNullForIncompatibleTypes()
    {
        DataKind? result = TypeCoercion.FindCommonKind(DataKind.String, DataKind.Scalar);

        Assert.Null(result);
    }

    [Fact]
    public void FindCommonKind_SameTypeReturnsSame()
    {
        Assert.Equal(DataKind.String, TypeCoercion.FindCommonKind(DataKind.String, DataKind.String));
    }

    [Fact]
    public void UInt8ToScalarChainToVector()
    {
        DataValue uint8 = DataValue.FromUInt8(128);

        // UInt8 -> Scalar -> Vector should succeed through chaining
        DataValue scalar = TypeCoercion.Widen(uint8, DataKind.Scalar);
        DataValue vector = TypeCoercion.Widen(scalar, DataKind.Vector);

        Assert.Equal(DataKind.Vector, vector.Kind);
        Assert.Equal([128.0f], vector.AsVector());
    }

    // ─────────────── CoerceValue ───────────────

    [Fact]
    public void CoerceValue_SameKind_ReturnsSameValue()
    {
        DataValue value = DataValue.FromScalar(42f);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Scalar);
        Assert.Equal(42f, result.AsScalar());
    }

    [Fact]
    public void CoerceValue_Null_ReturnsTypedNull()
    {
        DataValue value = DataValue.Null(DataKind.String);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Scalar);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }

    [Fact]
    public void CoerceValue_WideningChain_BooleanToScalar()
    {
        DataValue value = DataValue.FromBoolean(true);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Scalar);
        Assert.Equal(DataKind.Scalar, result.Kind);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void CoerceValue_StringToScalar_ParsesNumber()
    {
        DataValue value = DataValue.FromString("3.14");
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Scalar);
        Assert.Equal(DataKind.Scalar, result.Kind);
        Assert.Equal(3.14f, result.AsScalar(), 0.001f);
    }

    [Fact]
    public void CoerceValue_StringToScalar_UnparseableReturnsNull()
    {
        DataValue value = DataValue.FromString("abc");
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Scalar);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
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
        DataValue value = DataValue.FromScalar(42f);
        DataValue result = TypeCoercion.CoerceValue(value, DataKind.Date);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    // ─────────────── CanCoerceStringTo ───────────────

    [Theory]
    [InlineData(DataKind.Scalar, true)]
    [InlineData(DataKind.Boolean, true)]
    [InlineData(DataKind.Date, true)]
    [InlineData(DataKind.Vector, false)]
    [InlineData(DataKind.Image, false)]
    [InlineData(DataKind.Tensor, false)]
    public void CanCoerceStringTo_ReturnsExpected(DataKind kind, bool expected)
    {
        Assert.Equal(expected, TypeCoercion.CanCoerceStringTo(kind));
    }
}
