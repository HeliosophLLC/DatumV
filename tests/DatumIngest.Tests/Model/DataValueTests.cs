using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

public class DataValueTests : ServiceTestBase
{
    [Fact]
    public void DataValue_Is20Bytes()
    {
        Assert.Equal(20, Unsafe.SizeOf<DataValue>());
    }

    [Fact]
    public void ScalarValueStoresFloat()
    {
        DataValue value = DataValue.FromFloat32(3.14f);

        Assert.Equal(DataKind.Float32, value.Kind);
        Assert.Equal(3.14f, value.AsFloat32());
    }

    [Fact]
    public void ScalarValueEquality()
    {
        DataValue a = DataValue.FromFloat32(1.0f);
        DataValue b = DataValue.FromFloat32(1.0f);
        DataValue c = DataValue.FromFloat32(2.0f);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void UInt8ValueStoresByte()
    {
        DataValue value = DataValue.FromUInt8(255);

        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.Equal((byte)255, value.AsUInt8());
    }

    [Fact]
    public void Point2DStoresInlineXY()
    {
        DataValue value = DataValue.FromPoint2D(1.5f, -2.25f);

        Assert.Equal(DataKind.Point2D, value.Kind);
        Assert.True(value.IsInline);
        System.Numerics.Vector2 v = value.AsPoint2D();
        Assert.Equal(1.5f, v.X);
        Assert.Equal(-2.25f, v.Y);
    }

    [Fact]
    public void Point3DStoresInlineXYZ()
    {
        DataValue value = DataValue.FromPoint3D(1.5f, -2.25f, 3.75f);

        Assert.Equal(DataKind.Point3D, value.Kind);
        Assert.True(value.IsInline);
        System.Numerics.Vector3 v = value.AsPoint3D();
        Assert.Equal(1.5f, v.X);
        Assert.Equal(-2.25f, v.Y);
        Assert.Equal(3.75f, v.Z);
    }

    [Fact]
    public void Point2DEqualityAndHash()
    {
        DataValue a = DataValue.FromPoint2D(1f, 2f);
        DataValue b = DataValue.FromPoint2D(1f, 2f);
        DataValue c = DataValue.FromPoint2D(1f, 3f);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Point3DEqualityAndHash()
    {
        DataValue a = DataValue.FromPoint3D(1f, 2f, 3f);
        DataValue b = DataValue.FromPoint3D(1f, 2f, 3f);
        DataValue c = DataValue.FromPoint3D(1f, 2f, 4f);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void PointKindMismatchIsNotEqual()
    {
        DataValue p2 = DataValue.FromPoint2D(1f, 2f);
        DataValue p3 = DataValue.FromPoint3D(1f, 2f, 0f);

        Assert.NotEqual(p2, p3);
    }

    [Fact]
    public void PointDisplayString()
    {
        Assert.Equal("(1, 2)", DataValue.FromPoint2D(1f, 2f).ToDisplayString());
        Assert.Equal("(1, 2, 3)", DataValue.FromPoint3D(1f, 2f, 3f).ToDisplayString());
    }

    [Fact]
    public void ByteArrayValueStoresByteArray()
    {
        byte[] data = [1, 2, 3, 4];
        Arena arena = new();
        DataValue value = DataValue.FromByteArray(data, arena);

        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsByteArrayKind);
        Assert.Equal(data, value.AsUInt8Array(arena));
    }

    [Fact]
    public void StringValueStoresString()
    {
        DataValue value = DataValue.FromString("hello");

        Assert.Equal(DataKind.String, value.Kind);
        Assert.Equal("hello", value.AsString());
    }

    [Fact]
    public void StringValueEquality()
    {
        DataValue a = DataValue.FromString("test");
        DataValue b = DataValue.FromString("test");
        DataValue c = DataValue.FromString("other");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Float32ArrayValueStoresFloatArray()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        DataValue value = DataValue.FromInlineArray<float>(data, DataKind.Float32);

        Assert.Equal(DataKind.Float32, value.Kind);
        Assert.True(value.IsArray);
        Assert.Equal(data, value.AsArraySpan<float>().ToArray());
    }

    // Matrix and Tensor kinds were retired; their tests were deleted.
    // Multi-rank float arrays will land via the typed-array consolidation
    // (Float32 + IsArray + HasFixedShape).
#if FALSE_RETIRED_MATRIX_TENSOR
    [Fact]
    public void VectorToTensorIsZeroCopy()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        DataValue vector = DataValue.FromInlineArray<float>(data, DataKind.Float32);
        DataValue tensor = vector.ToTensor();

        Assert.Equal(DataKind.Tensor, tensor.Kind);
        float[] tensorData = tensor.AsTensor(out int[] shape);

        // Zero-copy: same array reference
        Assert.Same(data, tensorData);
        Assert.Equal([3], shape);
    }

    [Fact]
    public void MatrixToTensorIsZeroCopy()
    {
        float[] data = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f];
        DataValue matrix = DataValue.FromMatrix(data, 2, 3);
        DataValue tensor = matrix.ToTensor();

        Assert.Equal(DataKind.Tensor, tensor.Kind);
        float[] tensorData = tensor.AsTensor(out int[] shape);

        Assert.Same(data, tensorData);
        Assert.Equal([2, 3], shape);
    }

    [Fact]
    public void TensorToVectorRequiresRankOne()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        DataValue tensor = DataValue.FromTensor(data, [3]);
        DataValue vector = tensor.ToVector();

        Assert.Equal(DataKind.Vector, vector.Kind);
        Assert.Same(data, vector.AsArraySpan<float>().ToArray());
    }

    [Fact]
    public void TensorToVectorRejectsHigherRank()
    {
        float[] data = new float[6];
        DataValue tensor = DataValue.FromTensor(data, [2, 3]);

        Assert.Throws<InvalidOperationException>(() => tensor.ToVector());
    }

    [Fact]
    public void TensorToMatrixRequiresRankTwo()
    {
        float[] data = new float[6];
        DataValue tensor = DataValue.FromTensor(data, [2, 3]);
        DataValue matrix = tensor.ToMatrix();

        Assert.Equal(DataKind.Matrix, matrix.Kind);
        float[] matrixData = matrix.AsMatrix(out int rows, out int columns);
        Assert.Same(data, matrixData);
        Assert.Equal(2, rows);
        Assert.Equal(3, columns);
    }

    [Fact]
    public void TensorToMatrixRejectsNonRankTwo()
    {
        float[] data = new float[24];
        DataValue tensor = DataValue.FromTensor(data, [2, 3, 4]);

        Assert.Throws<InvalidOperationException>(() => tensor.ToMatrix());
    }
#endif

    [Fact]
    public void DateValueStoresDateOnly()
    {
        DateOnly date = new(2026, 3, 15);
        DataValue value = DataValue.FromDate(date);

        Assert.Equal(DataKind.Date, value.Kind);
        Assert.Equal(date, value.AsDate());
    }

    [Fact]
    public void DateTimeValueStoresDateTime()
    {
        DateTimeOffset dateTime = new(2026, 3, 15, 10, 30, 0, TimeSpan.Zero);
        DataValue value = DataValue.FromDateTime(dateTime);

        Assert.Equal(DataKind.DateTime, value.Kind);
        Assert.Equal(dateTime, value.AsDateTime());
    }

    [Fact]
    public void UuidValueStoresGuid()
    {
        Guid guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        DataValue value = DataValue.FromUuid(guid);

        Assert.Equal(DataKind.Uuid, value.Kind);
        Assert.Equal(guid, value.AsUuid());
    }

    [Fact]
    public void UuidEquality()
    {
        Guid guid = Guid.NewGuid();
        DataValue a = DataValue.FromUuid(guid);
        DataValue b = DataValue.FromUuid(guid);
        DataValue c = DataValue.FromUuid(Guid.NewGuid());

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DateTimeWithNonZeroOffsetRoundTrips()
    {
        DateTimeOffset dateTime = new(2026, 4, 6, 14, 30, 0, TimeSpan.FromHours(5));
        DataValue value = DataValue.FromDateTime(dateTime);

        Assert.Equal(DataKind.DateTime, value.Kind);
        Assert.Equal(dateTime, value.AsDateTime());
        Assert.Equal(TimeSpan.FromHours(5), value.AsDateTime().Offset);
    }

    [Fact]
    public void DateTimeWithNegativeOffsetRoundTrips()
    {
        DateTimeOffset dateTime = new(2026, 4, 6, 8, 0, 0, TimeSpan.FromHours(-8));
        DataValue value = DataValue.FromDateTime(dateTime);

        Assert.Equal(dateTime, value.AsDateTime());
        Assert.Equal(TimeSpan.FromHours(-8), value.AsDateTime().Offset);
    }

    [Fact]
    public void DateTimeEquality()
    {
        DateTimeOffset dateTime = new(2026, 4, 6, 12, 0, 0, TimeSpan.FromHours(2));
        DataValue a = DataValue.FromDateTime(dateTime);
        DataValue b = DataValue.FromDateTime(dateTime);
        DataValue c = DataValue.FromDateTime(dateTime.ToUniversalTime());

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void NullValueIsSupported()
    {
        DataValue value = DataValue.Null(DataKind.String);

        Assert.True(value.IsNull);
        Assert.Equal(DataKind.String, value.Kind);
    }

    [Fact]
    public void NullValuesOfSameKindAreEqual()
    {
        DataValue a = DataValue.Null(DataKind.Float32);
        DataValue b = DataValue.Null(DataKind.Float32);

        Assert.Equal(a, b);
    }

    [Fact]
    public void NullValueOfDifferentKindIsNotEqual()
    {
        DataValue a = DataValue.Null(DataKind.Float32);
        DataValue b = DataValue.Null(DataKind.String);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AsFloat32ThrowsOnWrongKind()
    {
        DataValue value = DataValue.FromString("hello");

        Assert.Throws<InvalidOperationException>(() => value.AsFloat32());
    }

    [Fact]
    public void AsStringThrowsOnNull()
    {
        DataValue value = DataValue.Null(DataKind.String);

        Assert.Throws<InvalidOperationException>(() => value.AsString());
    }

    [Fact]
    public void VectorEquality()
    {
        DataValue a = DataValue.FromInlineArray<float>([1.0f, 2.0f], DataKind.Float32);
        DataValue b = DataValue.FromInlineArray<float>([1.0f, 2.0f], DataKind.Float32);
        DataValue c = DataValue.FromInlineArray<float>([1.0f, 3.0f], DataKind.Float32);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void GetHashCodeIsConsistentWithEquality()
    {
        DataValue a = DataValue.FromFloat32(42.0f);
        DataValue b = DataValue.FromFloat32(42.0f);

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ─────────────── ToString tests ───────────────

    [Fact]
    public void ToString_Scalar_FormatsValue()
    {
        Assert.Equal("42", DataValue.FromFloat32(42f).ToString());
    }

    [Fact]
    public void ToString_UInt8_FormatsValue()
    {
        Assert.Equal("255", DataValue.FromUInt8(255).ToString());
    }

    [Fact]
    public void ToString_String_ReturnsPayload()
    {
        Assert.Equal("hello", DataValue.FromString("hello").ToString());
    }

    [Fact]
    public void ToString_Null_IncludesKind()
    {
        Assert.Equal("NULL(Float32)", DataValue.Null(DataKind.Float32).ToString());
    }
}
