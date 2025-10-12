using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Low-level <see cref="DataValue"/> deserialization helpers shared across index reading
/// and datum-file decoding paths. All methods are static; this class carries no state.
/// </summary>
internal static class IndexReader
{
    /// <summary>
    /// Reads a nullable <see cref="DataValue"/> prefixed by a boolean presence byte.
    /// Returns <c>null</c> when the presence byte is <c>false</c>.
    /// </summary>
    internal static DataValue? ReadNullableDataValue(BinaryReader reader)
    {
        bool hasValue = reader.ReadBoolean();

        if (!hasValue)
        {
            return null;
        }

        return ReadDataValue(reader);
    }

    /// <summary>
    /// Reads a single <see cref="DataValue"/> by reading the leading <see cref="DataKind"/> byte
    /// and then dispatching to the appropriate type reader.
    /// </summary>
    internal static DataValue ReadDataValue(BinaryReader reader)
    {
        DataKind kind = (DataKind)reader.ReadByte();
        return ReadDataValueBody(reader, kind);
    }

    /// <summary>
    /// Reads a single <see cref="DataValue"/> using an explicit store for reference-type payloads.
    /// </summary>
    internal static DataValue ReadDataValue(BinaryReader reader, IValueStore store)
    {
        DataKind kind = (DataKind)reader.ReadByte();
        return ReadDataValueBody(reader, kind, store);
    }

    /// <summary>
    /// Reads a <see cref="DataValue"/> body with an explicit store for reference-type payloads.
    /// </summary>
    internal static DataValue ReadDataValueBody(BinaryReader reader, DataKind kind, IValueStore store)
    {
        return kind switch
        {
            DataKind.Float32 => DataValue.FromFloat32(reader.ReadSingle()),
            DataKind.UInt8 => DataValue.FromUInt8(reader.ReadByte()),
            DataKind.String => DataValue.FromString(reader.ReadString(), store),
            DataKind.Date => DataValue.FromDate(DateOnly.FromDayNumber(reader.ReadInt32())),
            DataKind.DateTime => DataValue.FromDateTime(
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()))),
            DataKind.JsonValue => DataValue.FromJsonValue(reader.ReadString(), store),
            DataKind.UInt8Array => ReadUInt8Array(reader, store),
            DataKind.Vector => ReadVector(reader, store),
            DataKind.Matrix => ReadMatrix(reader, store),
            DataKind.Tensor => ReadTensor(reader, store),
            DataKind.Image => ReadImage(reader, store),
            DataKind.Boolean => DataValue.FromBoolean(reader.ReadBoolean()),
            DataKind.Time => DataValue.FromTime(new TimeOnly(reader.ReadInt64())),
            DataKind.Duration => DataValue.FromDuration(TimeSpan.FromTicks(reader.ReadInt64())),
            DataKind.Uuid => DataValue.FromUuid(new Guid(reader.ReadBytes(16))),
            DataKind.Float64 => DataValue.FromFloat64(reader.ReadDouble()),
            DataKind.Int8 => DataValue.FromInt8(reader.ReadSByte()),
            DataKind.Int16 => DataValue.FromInt16(reader.ReadInt16()),
            DataKind.UInt16 => DataValue.FromUInt16(reader.ReadUInt16()),
            DataKind.Int32 => DataValue.FromInt32(reader.ReadInt32()),
            DataKind.UInt32 => DataValue.FromUInt32(reader.ReadUInt32()),
            DataKind.Int64 => DataValue.FromInt64(reader.ReadInt64()),
            DataKind.UInt64 => DataValue.FromUInt64(reader.ReadUInt64()),
            DataKind.Type => DataValue.FromType((DataKind)reader.ReadByte()),
            _ => throw new InvalidDataException($"Unknown DataKind {kind} in datum-index file.")
        };
    }

    /// <summary>
    /// Reads a <see cref="DataValue"/> body when the leading <see cref="DataKind"/> byte
    /// has already been consumed by the caller.
    /// </summary>
    internal static DataValue ReadDataValueBody(BinaryReader reader, DataKind kind)
    {
        return kind switch
        {
            DataKind.Float32 => DataValue.FromFloat32(reader.ReadSingle()),
            DataKind.UInt8 => DataValue.FromUInt8(reader.ReadByte()),
            DataKind.String => DataValue.FromString(reader.ReadString()),
            DataKind.Date => DataValue.FromDate(DateOnly.FromDayNumber(reader.ReadInt32())),
            DataKind.DateTime => DataValue.FromDateTime(
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()))),
            DataKind.JsonValue => DataValue.FromJsonValue(reader.ReadString()),
            DataKind.UInt8Array => ReadUInt8Array(reader),
            DataKind.Vector => ReadVector(reader),
            DataKind.Matrix => ReadMatrix(reader),
            DataKind.Tensor => ReadTensor(reader),
            DataKind.Image => ReadImage(reader),
            DataKind.Boolean => DataValue.FromBoolean(reader.ReadBoolean()),
            DataKind.Time => DataValue.FromTime(new TimeOnly(reader.ReadInt64())),
            DataKind.Duration => DataValue.FromDuration(TimeSpan.FromTicks(reader.ReadInt64())),
            DataKind.Uuid => DataValue.FromUuid(new Guid(reader.ReadBytes(16))),
            DataKind.Float64 => DataValue.FromFloat64(reader.ReadDouble()),
            DataKind.Int8 => DataValue.FromInt8(reader.ReadSByte()),
            DataKind.Int16 => DataValue.FromInt16(reader.ReadInt16()),
            DataKind.UInt16 => DataValue.FromUInt16(reader.ReadUInt16()),
            DataKind.Int32 => DataValue.FromInt32(reader.ReadInt32()),
            DataKind.UInt32 => DataValue.FromUInt32(reader.ReadUInt32()),
            DataKind.Int64 => DataValue.FromInt64(reader.ReadInt64()),
            DataKind.UInt64 => DataValue.FromUInt64(reader.ReadUInt64()),
            DataKind.Type => DataValue.FromType((DataKind)reader.ReadByte()),
            _ => throw new InvalidDataException($"Unknown DataKind {kind} in datum-index file.")
        };
    }

    private static DataValue ReadUInt8Array(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return DataValue.FromUInt8Array(bytes);
    }

    private static DataValue ReadUInt8Array(BinaryReader reader, IValueStore store)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return DataValue.FromUInt8Array(bytes, store);
    }

    private static DataValue ReadVector(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        float[] values = new float[length];
        for (int i = 0; i < length; i++)
            values[i] = reader.ReadSingle();
        return DataValue.FromVector(values);
    }

    private static DataValue ReadVector(BinaryReader reader, IValueStore store)
    {
        int length = reader.ReadInt32();
        float[] values = new float[length];
        for (int i = 0; i < length; i++)
            values[i] = reader.ReadSingle();
        return DataValue.FromVector(values, store);
    }

    private static DataValue ReadMatrix(BinaryReader reader)
    {
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int i = 0; i < dataLength; i++)
            values[i] = reader.ReadSingle();
        return DataValue.FromMatrix(values, rows, columns);
    }

    private static DataValue ReadMatrix(BinaryReader reader, IValueStore store)
    {
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int i = 0; i < dataLength; i++)
            values[i] = reader.ReadSingle();
        return DataValue.FromMatrix(values, rows, columns, store);
    }

    private static DataValue ReadTensor(BinaryReader reader)
    {
        int rank = reader.ReadInt32();
        int[] shape = new int[rank];
        for (int i = 0; i < rank; i++)
            shape[i] = reader.ReadInt32();
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int i = 0; i < dataLength; i++)
            values[i] = reader.ReadSingle();
        return DataValue.FromTensor(values, shape);
    }

    private static DataValue ReadTensor(BinaryReader reader, IValueStore store)
    {
        int rank = reader.ReadInt32();
        int[] shape = new int[rank];
        for (int i = 0; i < rank; i++)
            shape[i] = reader.ReadInt32();
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int i = 0; i < dataLength; i++)
            values[i] = reader.ReadSingle();
        return DataValue.FromTensor(values, shape, store);
    }

    private static DataValue ReadImage(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return DataValue.FromImage(bytes);
    }

    private static DataValue ReadImage(BinaryReader reader, IValueStore store)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return DataValue.FromImage(bytes, store);
    }
}
