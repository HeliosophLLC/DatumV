using DatumIngest.Model;
using DatumIngest.Compute.Grpc;

namespace DatumIngest.Compute.Services;

/// <summary>
/// Converts between the domain <see cref="DataValue"/> / <see cref="Schema"/>
/// types and their Protobuf message representations.
/// </summary>
internal static class ProtoConverter
{
    /// <summary>
    /// Converts a domain <see cref="ColumnInfo"/> to its Protobuf representation.
    /// </summary>
    /// <param name="column">The domain column info.</param>
    /// <returns>The Protobuf column info message.</returns>
    public static ColumnInfoMessage ToProto(ColumnInfo column)
    {
        return new ColumnInfoMessage
        {
            Name = column.Name,
            Kind = ToProtoKind(column.Kind),
            Nullable = column.Nullable,
        };
    }

    /// <summary>
    /// Converts a domain <see cref="Schema"/> to its Protobuf representation.
    /// </summary>
    /// <param name="schema">The domain schema.</param>
    /// <returns>The Protobuf schema message.</returns>
    public static SchemaMessage ToProto(Schema schema)
    {
        SchemaMessage message = new();
        foreach (ColumnInfo column in schema.Columns)
        {
            message.Columns.Add(ToProto(column));
        }

        return message;
    }

    /// <summary>
    /// Converts a domain <see cref="DataValue"/> to its Protobuf representation.
    /// </summary>
    /// <param name="value">The domain data value.</param>
    /// <returns>The Protobuf data value message.</returns>
    public static DataValueMessage ToProto(DataValue value)
    {
        DataValueMessage message = new();

        if (value.IsNull)
        {
            message.IsNull = true;
            return message;
        }

        switch (value.Kind)
        {
            case DataKind.UInt8:
                message.Uint8Value = value.AsUInt8();
                break;

            case DataKind.Float32:
                message.Float32Value = value.AsFloat32();
                break;

            case DataKind.String:
                message.StringValue = value.AsString();
                break;

            case DataKind.Date:
                message.DateValue = value.AsDate().ToString("yyyy-MM-dd");
                break;

            case DataKind.DateTime:
                message.DateTimeValue = value.AsDateTime().ToString("O");
                break;

            case DataKind.JsonValue:
                message.JsonValue = value.AsJsonValue();
                break;

            case DataKind.UInt8Array:
                message.Uint8ArrayValue = Google.Protobuf.ByteString.CopyFrom(value.AsUInt8Array());
                break;

            case DataKind.Image:
                message.ImageValue = Google.Protobuf.ByteString.CopyFrom(value.AsImage());
                break;

            case DataKind.Vector:
                VectorMessage vector = new();
                vector.Values.AddRange(value.AsVector());
                message.VectorValue = vector;
                break;

            case DataKind.Matrix:
                float[] matrixData = value.AsMatrix(out int rows, out int columns);
                MatrixMessage matrix = new() { Rows = rows, Columns = columns };
                matrix.Values.AddRange(matrixData);
                message.MatrixValue = matrix;
                break;

            case DataKind.Tensor:
                float[] tensorData = value.AsTensor(out int[] shape);
                TensorMessage tensor = new();
                tensor.Shape.AddRange(shape);
                tensor.Values.AddRange(tensorData);
                message.TensorValue = tensor;
                break;

            case DataKind.Uuid:
                message.UuidValue = value.AsUuid().ToString();
                break;

            case DataKind.Boolean:
                message.BooleanValue = value.AsBoolean();
                break;

            case DataKind.Time:
                message.TimeValue = value.AsTime().ToString("HH:mm:ss.FFFFFFF");
                break;

            case DataKind.Duration:
                message.DurationValue = value.AsDuration().TotalSeconds;
                break;

            case DataKind.Array:
                message.ArrayValue = value.ToString() ?? "[]";
                break;

            case DataKind.Int8:
                message.Int8Value = value.AsInt8();
                break;

            case DataKind.Int16:
                message.Int16Value = value.AsInt16();
                break;

            case DataKind.UInt16:
                message.Uint16Value = value.AsUInt16();
                break;

            case DataKind.Int32:
                message.Int32Value = value.AsInt32();
                break;

            case DataKind.UInt32:
                message.Uint32Value = value.AsUInt32();
                break;

            case DataKind.Int64:
                message.Int64Value = value.AsInt64();
                break;

            case DataKind.UInt64:
                message.Uint64Value = value.AsUInt64();
                break;

            case DataKind.Float64:
                message.Float64Value = value.AsFloat64();
                break;
        }

        return message;
    }

    /// <summary>
    /// Maps the domain <see cref="DataKind"/> enum to the Protobuf <see cref="DataKindValue"/>.
    /// </summary>
    private static DataKindValue ToProtoKind(DataKind kind)
    {
        return kind switch
        {
            DataKind.UInt8 => DataKindValue.DataKindUint8,
            DataKind.Float32 => DataKindValue.DataKindFloat32,
            DataKind.Vector => DataKindValue.DataKindVector,
            DataKind.Matrix => DataKindValue.DataKindMatrix,
            DataKind.Tensor => DataKindValue.DataKindTensor,
            DataKind.UInt8Array => DataKindValue.DataKindUint8Array,
            DataKind.Image => DataKindValue.DataKindImage,
            DataKind.String => DataKindValue.DataKindString,
            DataKind.Date => DataKindValue.DataKindDate,
            DataKind.DateTime => DataKindValue.DataKindDateTime,
            DataKind.JsonValue => DataKindValue.DataKindJsonValue,
            DataKind.Uuid => DataKindValue.DataKindUuid,
            DataKind.Boolean => DataKindValue.DataKindBoolean,
            DataKind.Time => DataKindValue.DataKindTime,
            DataKind.Duration => DataKindValue.DataKindDuration,
            DataKind.Array => DataKindValue.DataKindArray,
            DataKind.Int8 => DataKindValue.DataKindInt8,
            DataKind.Int16 => DataKindValue.DataKindInt16,
            DataKind.UInt16 => DataKindValue.DataKindUint16,
            DataKind.Int32 => DataKindValue.DataKindInt32,
            DataKind.UInt32 => DataKindValue.DataKindUint32,
            DataKind.Int64 => DataKindValue.DataKindInt64,
            DataKind.UInt64 => DataKindValue.DataKindUint64,
            DataKind.Float64 => DataKindValue.DataKindFloat64,
            _ => DataKindValue.DataKindString,
        };
    }

    /// <summary>
    /// Converts a Protobuf <see cref="DataValueMessage"/> to the domain <see cref="DataValue"/>.
    /// Used for deserializing parameter values received from gRPC clients.
    /// </summary>
    /// <param name="message">The Protobuf data value message.</param>
    /// <returns>The domain data value.</returns>
    public static DataValue FromProto(DataValueMessage message)
    {
        if (message.IsNull)
        {
            return DataValue.UnknownNull();
        }

        return message.ValueCase switch
        {
            DataValueMessage.ValueOneofCase.Uint8Value => DataValue.FromUInt8((byte)message.Uint8Value),
            DataValueMessage.ValueOneofCase.Float32Value => DataValue.FromFloat32(message.Float32Value),
            DataValueMessage.ValueOneofCase.StringValue => DataValue.FromString(message.StringValue),
            DataValueMessage.ValueOneofCase.BooleanValue => DataValue.FromBoolean(message.BooleanValue),
            DataValueMessage.ValueOneofCase.DateValue => DataValue.FromDate(DateOnly.Parse(message.DateValue)),
            DataValueMessage.ValueOneofCase.DateTimeValue => DataValue.FromDateTime(DateTimeOffset.Parse(message.DateTimeValue)),
            DataValueMessage.ValueOneofCase.TimeValue => DataValue.FromTime(TimeOnly.Parse(message.TimeValue)),
            DataValueMessage.ValueOneofCase.DurationValue => DataValue.FromDuration(TimeSpan.FromSeconds(message.DurationValue)),
            DataValueMessage.ValueOneofCase.UuidValue => DataValue.FromUuid(Guid.Parse(message.UuidValue)),
            DataValueMessage.ValueOneofCase.JsonValue => DataValue.FromJsonValue(message.JsonValue),
            DataValueMessage.ValueOneofCase.ArrayValue => DataValue.FromJsonValue(message.ArrayValue),
            DataValueMessage.ValueOneofCase.Int8Value => DataValue.FromInt8((sbyte)message.Int8Value),
            DataValueMessage.ValueOneofCase.Int16Value => DataValue.FromInt16((short)message.Int16Value),
            DataValueMessage.ValueOneofCase.Uint16Value => DataValue.FromUInt16((ushort)message.Uint16Value),
            DataValueMessage.ValueOneofCase.Int32Value => DataValue.FromInt32(message.Int32Value),
            DataValueMessage.ValueOneofCase.Uint32Value => DataValue.FromUInt32(message.Uint32Value),
            DataValueMessage.ValueOneofCase.Int64Value => DataValue.FromInt64(message.Int64Value),
            DataValueMessage.ValueOneofCase.Uint64Value => DataValue.FromUInt64(message.Uint64Value),
            DataValueMessage.ValueOneofCase.Float64Value => DataValue.FromFloat64(message.Float64Value),
            _ => DataValue.UnknownNull(),
        };
    }
}
