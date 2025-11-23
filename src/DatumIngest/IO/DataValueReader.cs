using DatumIngest.Model;

namespace DatumIngest.IO;

/// <summary>
/// Low-level <see cref="DataValue"/> deserialization helpers shared across index reading
/// and datum-file decoding paths. All methods are static; this class carries no state.
/// </summary>
internal static class DataValueReader
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
        byte kindByte = reader.ReadByte();
        if (kindByte == DataValueWriter.WireKindByteArray)
        {
            return ReadUInt8Array(reader);
        }
        return ReadDataValueBody(reader, (DataKind)kindByte);
    }

    /// <summary>
    /// Reads a single <see cref="DataValue"/> using an explicit store for reference-type payloads.
    /// </summary>
    internal static DataValue ReadDataValue(BinaryReader reader, IValueStore store)
    {
        byte kindByte = reader.ReadByte();
        if (kindByte == DataValueWriter.WireKindByteArray)
        {
            return ReadUInt8Array(reader, store);
        }
        return ReadDataValueBody(reader, (DataKind)kindByte, store);
    }

    /// <summary>
    /// Reads a <see cref="DataValue"/> body with an explicit store for reference-type payloads.
    /// </summary>
    internal static DataValue ReadDataValueBody(BinaryReader reader, DataKind kind, IValueStore store)
    {
        return kind switch
        {
            DataKind.Type => DataValue.FromType((DataKind)reader.ReadByte()),
            DataKind.Boolean => DataValue.FromBoolean(reader.ReadBoolean()),
            DataKind.UInt8 => DataValue.FromUInt8(reader.ReadByte()),
            DataKind.UInt16 => DataValue.FromUInt16(reader.ReadUInt16()),
            DataKind.UInt32 => DataValue.FromUInt32(reader.ReadUInt32()),
            DataKind.UInt64 => DataValue.FromUInt64(reader.ReadUInt64()),
            DataKind.UInt128 => DataValue.FromUInt128(ReadUInt128(reader)),
            DataKind.Int8 => DataValue.FromInt8(reader.ReadSByte()),
            DataKind.Int16 => DataValue.FromInt16(reader.ReadInt16()),
            DataKind.Int32 => DataValue.FromInt32(reader.ReadInt32()),
            DataKind.Int64 => DataValue.FromInt64(reader.ReadInt64()),
            DataKind.Int128 => DataValue.FromInt128(ReadInt128(reader)),
            DataKind.Float16 => DataValue.FromFloat16(BitConverter.UInt16BitsToHalf(reader.ReadUInt16())),
            DataKind.Float32 => DataValue.FromFloat32(reader.ReadSingle()),
            DataKind.Float64 => DataValue.FromFloat64(reader.ReadDouble()),
            DataKind.Decimal => DataValue.FromDecimal(reader.ReadDecimal()),
            DataKind.String => DataValue.FromString(reader.ReadString(), store),
            DataKind.Date => DataValue.FromDate(DateOnly.FromDayNumber(reader.ReadInt32())),
            DataKind.DateTime => DataValue.FromDateTime(
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()))),
            DataKind.Time => DataValue.FromTime(new TimeOnly(reader.ReadInt64())),
            DataKind.Duration => DataValue.FromDuration(TimeSpan.FromTicks(reader.ReadInt64())),
            DataKind.JsonValue => DataValue.FromJsonValue(reader.ReadString(), store),
            DataKind.Vector => ReadVector(reader, store),
            DataKind.Image => ReadImage(reader, store),
            DataKind.Uuid => DataValue.FromUuid(new Guid(reader.ReadBytes(16))),
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
            DataKind.Type => DataValue.FromType((DataKind)reader.ReadByte()),
            DataKind.Boolean => DataValue.FromBoolean(reader.ReadBoolean()),
            DataKind.Int8 => DataValue.FromInt8(reader.ReadSByte()),
            DataKind.Int16 => DataValue.FromInt16(reader.ReadInt16()),
            DataKind.Int32 => DataValue.FromInt32(reader.ReadInt32()),
            DataKind.Int64 => DataValue.FromInt64(reader.ReadInt64()),
            DataKind.Int128 => DataValue.FromInt128(ReadInt128(reader)),
            DataKind.UInt8 => DataValue.FromUInt8(reader.ReadByte()),
            DataKind.UInt16 => DataValue.FromUInt16(reader.ReadUInt16()),
            DataKind.UInt32 => DataValue.FromUInt32(reader.ReadUInt32()),
            DataKind.UInt64 => DataValue.FromUInt64(reader.ReadUInt64()),
            DataKind.UInt128 => DataValue.FromUInt128(ReadUInt128(reader)),
            DataKind.Float16 => DataValue.FromFloat16(BitConverter.UInt16BitsToHalf(reader.ReadUInt16())),
            DataKind.Float32 => DataValue.FromFloat32(reader.ReadSingle()),
            DataKind.Float64 => DataValue.FromFloat64(reader.ReadDouble()),
            DataKind.Decimal => DataValue.FromDecimal(reader.ReadDecimal()),
            DataKind.String => DataValue.FromString(reader.ReadString()),
            DataKind.Date => DataValue.FromDate(DateOnly.FromDayNumber(reader.ReadInt32())),
            DataKind.DateTime => DataValue.FromDateTime(
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()))),
            DataKind.Time => DataValue.FromTime(new TimeOnly(reader.ReadInt64())),
            DataKind.Duration => DataValue.FromDuration(TimeSpan.FromTicks(reader.ReadInt64())),
            DataKind.JsonValue => DataValue.FromJsonValue(reader.ReadString()),
            DataKind.Vector => ReadVector(reader),
            DataKind.Image => ReadImage(reader),
            DataKind.Uuid => DataValue.FromUuid(new Guid(reader.ReadBytes(16))),
            _ => throw new InvalidDataException($"Unknown DataKind {kind} in datum-index file.")
        };
    }

    private static Int128 ReadInt128(BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (reader.Read(buffer) != 16)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading Int128 value.");
        }
        return System.Buffers.Binary.BinaryPrimitives.ReadInt128LittleEndian(buffer);
    }

    private static UInt128 ReadUInt128(BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (reader.Read(buffer) != 16)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading UInt128 value.");
        }
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(buffer);
    }

    private static DataValue ReadUInt8Array(BinaryReader reader)
    {
        // The no-store body reader is used by zone-map readers, which never carry
        // byte-array min/max values (byte arrays aren't comparable). Throw if a
        // caller hits this — they should be using the store-aware overload.
        _ = reader.ReadInt32();
        throw new InvalidOperationException(
            "Cannot deserialize byte-array body without a target IValueStore. "
            + "Byte arrays are not expected in zone-map / no-store wire-format payloads.");
    }

    private static DataValue ReadUInt8Array(BinaryReader reader, IValueStore store)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return DataValue.FromByteArray(bytes, store);
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
