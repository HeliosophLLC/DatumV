using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using DatumIngest.Model;

namespace DatumIngest.Tests.Compute;

/// <summary>
/// Tests for <see cref="ProtoConverter"/> domain-to-proto conversions.
/// </summary>
public sealed class ProtoConverterTests
{
    /// <summary>
    /// Converts a ColumnInfo with all properties set.
    /// </summary>
    [Fact]
    public void ToProto_ColumnInfo_MapsAllProperties()
    {
        ColumnInfo column = new("temperature", DataKind.Scalar, nullable: true);

        ColumnInfoMessage message = ProtoConverter.ToProto(column);

        Assert.Equal("temperature", message.Name);
        Assert.Equal(DataKindValue.DataKindScalar, message.Kind);
        Assert.True(message.Nullable);
    }

    /// <summary>
    /// Converts a non-nullable ColumnInfo correctly.
    /// </summary>
    [Fact]
    public void ToProto_ColumnInfo_NonNullable_SetsFalse()
    {
        ColumnInfo column = new("id", DataKind.UInt8, nullable: false);

        ColumnInfoMessage message = ProtoConverter.ToProto(column);

        Assert.Equal("id", message.Name);
        Assert.Equal(DataKindValue.DataKindUint8, message.Kind);
        Assert.False(message.Nullable);
    }

    /// <summary>
    /// Converts a Schema with multiple columns.
    /// </summary>
    [Fact]
    public void ToProto_Schema_ConvertsAllColumns()
    {
        Schema schema = new(new[]
        {
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("score", DataKind.Scalar, true),
            new ColumnInfo("date", DataKind.Date, false),
        });

        SchemaMessage message = ProtoConverter.ToProto(schema);

        Assert.Equal(3, message.Columns.Count);
        Assert.Equal("name", message.Columns[0].Name);
        Assert.Equal("score", message.Columns[1].Name);
        Assert.Equal("date", message.Columns[2].Name);
    }

    /// <summary>
    /// Null DataValue sets IsNull flag.
    /// </summary>
    [Fact]
    public void ToProto_NullDataValue_SetsIsNull()
    {
        DataValue value = DataValue.Null(DataKind.Scalar);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.True(message.IsNull);
    }

    /// <summary>
    /// Scalar DataValue converts to ScalarValue field.
    /// </summary>
    [Fact]
    public void ToProto_ScalarValue_SetsScalarField()
    {
        DataValue value = DataValue.FromScalar(3.14f);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.False(message.IsNull);
        Assert.Equal(3.14f, message.ScalarValue, precision: 5);
    }

    /// <summary>
    /// UInt8 DataValue converts to Uint8Value field.
    /// </summary>
    [Fact]
    public void ToProto_UInt8Value_SetsUint8Field()
    {
        DataValue value = DataValue.FromUInt8(42);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal(42u, message.Uint8Value);
    }

    /// <summary>
    /// String DataValue converts to StringValue field.
    /// </summary>
    [Fact]
    public void ToProto_StringValue_SetsStringField()
    {
        DataValue value = DataValue.FromString("hello world");

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal("hello world", message.StringValue);
    }

    /// <summary>
    /// Date DataValue converts to ISO date string.
    /// </summary>
    [Fact]
    public void ToProto_DateValue_SetsDateString()
    {
        DataValue value = DataValue.FromDate(new DateOnly(2024, 6, 15));

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal("2024-06-15", message.DateValue);
    }

    /// <summary>
    /// DateTime DataValue converts to round-trip format string.
    /// </summary>
    [Fact]
    public void ToProto_DateTimeValue_SetsRoundTripString()
    {
        DateTime dateTime = new(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        DataValue value = DataValue.FromDateTime(dateTime);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Contains("2024-06-15", message.DateTimeValue);
    }

    /// <summary>
    /// JsonValue DataValue converts to JsonValue field.
    /// </summary>
    [Fact]
    public void ToProto_JsonValue_SetsJsonField()
    {
        DataValue value = DataValue.FromJsonValue("{\"key\": 1}");

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal("{\"key\": 1}", message.JsonValue);
    }

    /// <summary>
    /// UInt8Array DataValue converts to ByteString.
    /// </summary>
    [Fact]
    public void ToProto_UInt8ArrayValue_SetsByteString()
    {
        byte[] data = [0x01, 0x02, 0x03];
        DataValue value = DataValue.FromUInt8Array(data);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal(data, message.Uint8ArrayValue.ToByteArray());
    }

    /// <summary>
    /// Image DataValue converts to ByteString.
    /// </summary>
    [Fact]
    public void ToProto_ImageValue_SetsByteString()
    {
        byte[] data = [0xFF, 0xD8, 0xFF, 0xE0];
        DataValue value = DataValue.FromImage(data);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal(data, message.ImageValue.ToByteArray());
    }

    /// <summary>
    /// Vector DataValue converts to VectorMessage with all values.
    /// </summary>
    [Fact]
    public void ToProto_VectorValue_SetsVectorMessage()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        DataValue value = DataValue.FromVector(data);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal(3, message.VectorValue.Values.Count);
        Assert.Equal(1.0f, message.VectorValue.Values[0]);
        Assert.Equal(2.0f, message.VectorValue.Values[1]);
        Assert.Equal(3.0f, message.VectorValue.Values[2]);
    }

    /// <summary>
    /// Matrix DataValue converts to MatrixMessage with dimensions and flat data.
    /// </summary>
    [Fact]
    public void ToProto_MatrixValue_SetsMatrixMessage()
    {
        float[] data = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f];
        DataValue value = DataValue.FromMatrix(data, 2, 3);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal(2, message.MatrixValue.Rows);
        Assert.Equal(3, message.MatrixValue.Columns);
        Assert.Equal(6, message.MatrixValue.Values.Count);
        Assert.Equal(1.0f, message.MatrixValue.Values[0]);
        Assert.Equal(6.0f, message.MatrixValue.Values[5]);
    }

    /// <summary>
    /// Tensor DataValue converts to TensorMessage with shape and flat data.
    /// </summary>
    [Fact]
    public void ToProto_TensorValue_SetsTensorMessage()
    {
        float[] data = new float[24];
        for (int i = 0; i < 24; i++) data[i] = i;
        int[] shape = [2, 3, 4];
        DataValue value = DataValue.FromTensor(data, shape);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.Equal(3, message.TensorValue.Shape.Count);
        Assert.Equal(2, message.TensorValue.Shape[0]);
        Assert.Equal(3, message.TensorValue.Shape[1]);
        Assert.Equal(4, message.TensorValue.Shape[2]);
        Assert.Equal(24, message.TensorValue.Values.Count);
    }

    /// <summary>
    /// All DataKind enum values map to valid proto DataKindValue values.
    /// </summary>
    [Theory]
    [InlineData(DataKind.UInt8, DataKindValue.DataKindUint8)]
    [InlineData(DataKind.Scalar, DataKindValue.DataKindScalar)]
    [InlineData(DataKind.Vector, DataKindValue.DataKindVector)]
    [InlineData(DataKind.Matrix, DataKindValue.DataKindMatrix)]
    [InlineData(DataKind.Tensor, DataKindValue.DataKindTensor)]
    [InlineData(DataKind.UInt8Array, DataKindValue.DataKindUint8Array)]
    [InlineData(DataKind.Image, DataKindValue.DataKindImage)]
    [InlineData(DataKind.String, DataKindValue.DataKindString)]
    [InlineData(DataKind.Date, DataKindValue.DataKindDate)]
    [InlineData(DataKind.DateTime, DataKindValue.DataKindDateTime)]
    [InlineData(DataKind.JsonValue, DataKindValue.DataKindJsonValue)]
    public void ToProto_ColumnInfo_MapsEveryDataKind(DataKind kind, DataKindValue expectedProtoKind)
    {
        ColumnInfo column = new("test", kind, false);

        ColumnInfoMessage message = ProtoConverter.ToProto(column);

        Assert.Equal(expectedProtoKind, message.Kind);
    }
}
