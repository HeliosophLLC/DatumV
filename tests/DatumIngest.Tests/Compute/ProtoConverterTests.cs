using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

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
        ColumnInfo column = new("temperature", DataKind.Float32, nullable: true);

        ColumnInfoMessage message = ProtoConverter.ToProto(column);

        Assert.Equal("temperature", message.Name);
        Assert.Equal(DataKindValue.DataKindFloat32, message.Kind);
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
            new ColumnInfo("score", DataKind.Float32, true),
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
        DataValue value = DataValue.Null(DataKind.Float32);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.True(message.IsNull);
    }

    /// <summary>
    /// Float32 DataValue converts to Float32Value field.
    /// </summary>
    [Fact]
    public void ToProto_Float32Value_SetsFloat32Field()
    {
        DataValue value = DataValue.FromFloat32(3.14f);

        DataValueMessage message = ProtoConverter.ToProto(value);

        Assert.False(message.IsNull);
        Assert.Equal(3.14f, message.Float32Value, precision: 5);
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
    [InlineData(DataKind.Float32, DataKindValue.DataKindFloat32)]
    [InlineData(DataKind.Vector, DataKindValue.DataKindVector)]
    [InlineData(DataKind.Matrix, DataKindValue.DataKindMatrix)]
    [InlineData(DataKind.Tensor, DataKindValue.DataKindTensor)]
    [InlineData(DataKind.UInt8Array, DataKindValue.DataKindUint8Array)]
    [InlineData(DataKind.Image, DataKindValue.DataKindImage)]
    [InlineData(DataKind.String, DataKindValue.DataKindString)]
    [InlineData(DataKind.Date, DataKindValue.DataKindDate)]
    [InlineData(DataKind.DateTime, DataKindValue.DataKindDateTime)]
    [InlineData(DataKind.JsonValue, DataKindValue.DataKindJsonValue)]
    [InlineData(DataKind.Uuid, DataKindValue.DataKindUuid)]
    [InlineData(DataKind.Boolean, DataKindValue.DataKindBoolean)]
    [InlineData(DataKind.Time, DataKindValue.DataKindTime)]
    [InlineData(DataKind.Duration, DataKindValue.DataKindDuration)]
    [InlineData(DataKind.Array, DataKindValue.DataKindArray)]
    [InlineData(DataKind.Int8, DataKindValue.DataKindInt8)]
    [InlineData(DataKind.Int16, DataKindValue.DataKindInt16)]
    [InlineData(DataKind.UInt16, DataKindValue.DataKindUint16)]
    [InlineData(DataKind.Int32, DataKindValue.DataKindInt32)]
    [InlineData(DataKind.UInt32, DataKindValue.DataKindUint32)]
    [InlineData(DataKind.Int64, DataKindValue.DataKindInt64)]
    [InlineData(DataKind.UInt64, DataKindValue.DataKindUint64)]
    [InlineData(DataKind.Float64, DataKindValue.DataKindFloat64)]
    [InlineData(DataKind.Struct, DataKindValue.DataKindStruct)]
    [InlineData(DataKind.Type, DataKindValue.DataKindType)]
    public void ToProto_ColumnInfo_MapsEveryDataKind(DataKind kind, DataKindValue expectedProtoKind)
    {
        ColumnInfo column = new("test", kind, false);

        ColumnInfoMessage message = ProtoConverter.ToProto(column);

        Assert.Equal(expectedProtoKind, message.Kind);
    }

    // ───────────────────── Proto enum sync tests ─────────────────────

    /// <summary>
    /// Ensures every <see cref="DataKind"/> enum value has a corresponding entry in
    /// the Protobuf <see cref="DataKindValue"/> enum. If a new DataKind is added without
    /// updating the proto, this test will fail.
    /// </summary>
    [Fact]
    public void DataKindValue_CoversAllDataKinds()
    {
        HashSet<string> protoNames = new(
            Enum.GetNames<DataKindValue>().Select(n => n.Replace("DataKind", "").ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);

        List<string> missing = [];
        foreach (DataKind kind in Enum.GetValues<DataKind>())
        {
            // ToProtoKind is private — verify via ColumnInfo round-trip instead.
            // If the mapping is missing, ToProto falls through to the default
            // (DataKindString), which won't match the expected kind name.
            ColumnInfoMessage message = ProtoConverter.ToProto(new ColumnInfo("test", kind, false));
            string protoName = message.Kind.ToString().Replace("DataKind", "");

            if (!string.Equals(protoName, kind.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(kind.ToString());
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following DataKind values have no matching DataKindValue in the proto: " +
            $"{string.Join(", ", missing)}. Update datum_compute.proto and ProtoConverter.ToProtoKind().");
    }

    /// <summary>
    /// Ensures every <see cref="DataKind"/> enum value has a corresponding entry in
    /// the Protobuf <see cref="ParameterKindValue"/> enum. If a new DataKind is added
    /// without updating the proto, this test will fail.
    /// </summary>
    [Fact]
    public void ParameterKindValue_CoversAllDataKinds()
    {
        // Build a set of known ParameterKindValue names, normalized to match DataKind names.
        HashSet<string> parameterKinds = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Enum.GetNames<ParameterKindValue>())
        {
            // "PARAMETER_KIND_UINT8" → "UINT8", "PARAMETER_KIND_ANY" → "ANY"
            string normalized = name.Replace("ParameterKind", "");
            parameterKinds.Add(normalized);
        }

        List<string> missing = [];
        foreach (string kindName in Enum.GetNames<DataKind>())
        {
            if (!parameterKinds.Contains(kindName))
            {
                missing.Add(kindName);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following DataKind values have no matching ParameterKindValue in the proto: " +
            $"{string.Join(", ", missing)}. Add PARAMETER_KIND_{string.Join(", PARAMETER_KIND_", missing).ToUpperInvariant()} to datum_compute.proto.");
    }

    /// <summary>
    /// Ensures every <see cref="DataKind"/> value can survive a ToProto → FromProto
    /// round-trip through <see cref="DataValueMessage"/>. If a new DataKind is added
    /// without adding a oneof field or converter mapping, this test will fail.
    /// Kinds that do not yet have a FromProto path (Vector, Matrix, Tensor,
    /// UInt8Array, Image) are excluded — they are serialized for the wire but
    /// deserialized by the client SDK, not by ProtoConverter.FromProto.
    /// </summary>
    [Fact]
    public void ToProtoFromProto_AllDataKinds_RoundTrip()
    {
        // These kinds are serialized by ToProto but FromProto does not yet
        // reconstruct them (the gRPC client SDK handles deserialization).
        HashSet<DataKind> clientOnlyKinds =
        [
            DataKind.Unknown,
            DataKind.Vector, DataKind.Matrix, DataKind.Tensor,
            DataKind.UInt8Array, DataKind.Image,
        ];

        List<string> failures = [];

        foreach (DataKind kind in Enum.GetValues<DataKind>())
        {
            if (clientOnlyKinds.Contains(kind)) continue;

            DataValue sample = Indexing.IndexWriterRoundTripTests.CreateSampleValue(kind);
            DataValueMessage message = ProtoConverter.ToProto(sample);
            DataValue roundTripped = ProtoConverter.FromProto(message);

            if (roundTripped.Kind != kind)
            {
                failures.Add($"{kind} (expected Kind={kind}, got Kind={roundTripped.Kind})");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"The following DataKind values failed proto round-trip: " +
            $"{string.Join(", ", failures)}. Update DataValueMessage oneof and ProtoConverter.");
    }

    /// <summary>
    /// Extended numeric DataValues survive a ToProto → FromProto round-trip.
    /// </summary>
    [Fact]
    public void ToProtoFromProto_ExtendedNumericTypes_RoundTrip()
    {
        DataValue int8Value = DataValue.FromInt8(42);
        DataValue int16Value = DataValue.FromInt16(1000);
        DataValue uint16Value = DataValue.FromUInt16(50000);
        DataValue int32Value = DataValue.FromInt32(123456);
        DataValue uint32Value = DataValue.FromUInt32(3000000000);
        DataValue int64Value = DataValue.FromInt64(9876543210L);
        DataValue uint64Value = DataValue.FromUInt64(18000000000000000000UL);
        DataValue float64Value = DataValue.FromFloat64(3.141592653589793);

        Assert.Equal(42, ProtoConverter.FromProto(ProtoConverter.ToProto(int8Value)).AsInt8());
        Assert.Equal(1000, ProtoConverter.FromProto(ProtoConverter.ToProto(int16Value)).AsInt16());
        Assert.Equal(50000, ProtoConverter.FromProto(ProtoConverter.ToProto(uint16Value)).AsUInt16());
        Assert.Equal(123456, ProtoConverter.FromProto(ProtoConverter.ToProto(int32Value)).AsInt32());
        Assert.Equal(3000000000U, ProtoConverter.FromProto(ProtoConverter.ToProto(uint32Value)).AsUInt32());
        Assert.Equal(9876543210L, ProtoConverter.FromProto(ProtoConverter.ToProto(int64Value)).AsInt64());
        Assert.Equal(18000000000000000000UL, ProtoConverter.FromProto(ProtoConverter.ToProto(uint64Value)).AsUInt64());
        Assert.Equal(3.141592653589793, ProtoConverter.FromProto(ProtoConverter.ToProto(float64Value)).AsFloat64());
    }

    /// <summary>
    /// Simulates <c>SELECT *, image_to_tensor_chw(image)</c> on a bitmap-backed
    /// image (as produced by the IDX provider): the same <see cref="ImageHandle"/>
    /// is consumed by a function AND serialized as a raw image column.
    /// Both proto conversions must succeed without corrupting the source bitmap.
    /// </summary>
    [Fact]
    public void ToProto_BitmapBackedImage_SurvivesFunctionThenSerialization()
    {
        SKBitmap bitmap = new(28, 28, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);

        ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);
        DataValue imageValue = DataValue.FromImageHandle(handle);

        // Function consumes the image (reads pixels from the bitmap).
        ImageToTensorChwFunction function = new();
        DataValue tensorValue = function.Execute([imageValue]);

        // Serialize both values through ProtoConverter — the image column
        // must still be encodable after the function read from the same bitmap.
        DataValueMessage imageMessage = ProtoConverter.ToProto(imageValue);
        DataValueMessage tensorMessage = ProtoConverter.ToProto(tensorValue);

        Assert.False(imageMessage.ImageValue.IsEmpty);
        Assert.Equal(3, tensorMessage.TensorValue.Shape.Count);
    }

    /// <summary>
    /// Simulates <c>SELECT image, resize(image, 8, 8)</c> on a bitmap-backed
    /// image: the original image is serialized alongside a resized copy.
    /// </summary>
    [Fact]
    public void ToProto_BitmapBackedImage_SurvivesResizeThenSerialization()
    {
        SKBitmap bitmap = new(28, 28, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Red);

        ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);
        DataValue imageValue = DataValue.FromImageHandle(handle);

        // Resize function consumes the image and produces a new one.
        ResizeImageFunction resize = new();
        DataValue resizedValue = resize.Execute([
            imageValue,
            DataValue.FromFloat32(8),
            DataValue.FromFloat32(8),
        ]);

        // Serialize both — original and resized.
        DataValueMessage originalMessage = ProtoConverter.ToProto(imageValue);
        DataValueMessage resizedMessage = ProtoConverter.ToProto(resizedValue);

        Assert.False(originalMessage.ImageValue.IsEmpty);
        Assert.False(resizedMessage.ImageValue.IsEmpty);
    }
}
