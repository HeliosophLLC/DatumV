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
    public void UInt8ArrayValueStoresByteArray()
    {
        byte[] data = [1, 2, 3, 4];
        DataValue value = DataValue.FromUInt8Array(data);

        Assert.Equal(DataKind.UInt8Array, value.Kind);
        Assert.Equal(data, value.AsUInt8Array());
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
    public void VectorValueStoresFloatArray()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        DataValue value = DataValue.FromVector(data);

        Assert.Equal(DataKind.Vector, value.Kind);
        Assert.Equal(data, value.AsVector());
    }

    [Fact]
    public void MatrixValueStoresFloatArrayWithShape()
    {
        float[] data = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f];
        DataValue value = DataValue.FromMatrix(data, 2, 3);

        Assert.Equal(DataKind.Matrix, value.Kind);
        Assert.Equal(data, value.AsMatrix(out int rows, out int columns));
        Assert.Equal(2, rows);
        Assert.Equal(3, columns);
    }

    [Fact]
    public void MatrixRejectsShapeMismatch()
    {
        float[] data = [1.0f, 2.0f, 3.0f];

        Assert.Throws<ArgumentException>(() => DataValue.FromMatrix(data, 2, 3));
    }

    [Fact]
    public void TensorValueStoresFloatArrayWithArbitraryShape()
    {
        float[] data = new float[24];
        int[] shape = [2, 3, 4];
        DataValue value = DataValue.FromTensor(data, shape);

        Assert.Equal(DataKind.Tensor, value.Kind);
        Assert.Equal(data, value.AsTensor(out int[] resultShape));
        Assert.Equal(shape, resultShape);
    }

    [Fact]
    public void TensorRejectsShapeMismatch()
    {
        float[] data = new float[10];
        int[] shape = [2, 3, 4];

        Assert.Throws<ArgumentException>(() => DataValue.FromTensor(data, shape));
    }

    [Fact]
    public void VectorToTensorIsZeroCopy()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        DataValue vector = DataValue.FromVector(data);
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
        Assert.Same(data, vector.AsVector());
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
    public void JsonValueStoresString()
    {
        string json = "{\"key\": \"value\"}";
        DataValue value = DataValue.FromJsonValue(json);

        Assert.Equal(DataKind.JsonValue, value.Kind);
        Assert.Equal(json, value.AsJsonValue());
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
        DataValue a = DataValue.FromVector([1.0f, 2.0f]);
        DataValue b = DataValue.FromVector([1.0f, 2.0f]);
        DataValue c = DataValue.FromVector([1.0f, 3.0f]);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void TensorEqualityComparesDataAndShape()
    {
        DataValue a = DataValue.FromTensor([1.0f, 2.0f, 3.0f, 4.0f], [2, 2]);
        DataValue b = DataValue.FromTensor([1.0f, 2.0f, 3.0f, 4.0f], [2, 2]);
        DataValue c = DataValue.FromTensor([1.0f, 2.0f, 3.0f, 4.0f], [4]);

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
    public void ToString_Vector_ShowsLength()
    {
        Assert.Equal("Vector[3]", DataValue.FromVector([1f, 2f, 3f]).ToString());
    }

    [Fact]
    public void ToString_Matrix_ShowsShape()
    {
        Assert.Equal("Matrix[2x3]", DataValue.FromMatrix([1f, 2f, 3f, 4f, 5f, 6f], 2, 3).ToString());
    }

    [Fact]
    public void ToString_Null_IncludesKind()
    {
        Assert.Equal("NULL(Float32)", DataValue.Null(DataKind.Float32).ToString());
    }
}
