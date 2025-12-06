using DatumIngest.Model;

namespace DatumIngest.IO;

/// <summary>
/// Low-level <see cref="DataValue"/> serialization helpers shared across index writing
/// and datum-file encoding paths. All methods are static; this class carries no state.
/// </summary>
internal static class DataValueWriter
{
    /// <summary>
    /// Wire-format byte for a byte-array payload (was the value of the
    /// retired UInt8Array enum entry, value 56). Lives here as a
    /// pure wire constant so the in-memory <see cref="DataKind"/> type
    /// system can express byte arrays as <see cref="DataKind.UInt8"/> +
    /// <c>DataValueFlags.IsArray</c> without a dedicated enum slot.
    /// </summary>
    internal const byte WireKindByteArray = 56;

    /// <summary>
    /// Writes a 128-bit signed integer as 16 little-endian bytes. <see cref="BinaryWriter"/>
    /// has no <c>Write(Int128)</c> overload, so we round-trip through a 16-byte span.
    /// </summary>
    private static void WriteInt128(BinaryWriter writer, Int128 value)
    {
        Span<byte> buffer = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt128LittleEndian(buffer, value);
        writer.Write(buffer);
    }

    /// <summary>
    /// Writes a 128-bit unsigned integer as 16 little-endian bytes.
    /// </summary>
    private static void WriteUInt128(BinaryWriter writer, UInt128 value)
    {
        Span<byte> buffer = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(buffer, value);
        writer.Write(buffer);
    }

    /// <summary>BufferedWriter overload of <see cref="WriteInt128(BinaryWriter, Int128)"/>.</summary>
    private static void WriteInt128(BufferedWriter writer, Int128 value)
    {
        Span<byte> buffer = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt128LittleEndian(buffer, value);
        writer.Write(buffer);
    }

    /// <summary>BufferedWriter overload of <see cref="WriteUInt128(BinaryWriter, UInt128)"/>.</summary>
    private static void WriteUInt128(BufferedWriter writer, UInt128 value)
    {
        Span<byte> buffer = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(buffer, value);
        writer.Write(buffer);
    }

    internal static void WriteNullableDataValue(BinaryWriter writer, DataValue? value)
    {
        if (!value.HasValue || value.Value.IsNull)
        {
            writer.Write(false); // hasValue = false
            return;
        }

        writer.Write(true); // hasValue = true
        WriteDataValue(writer, value.Value);
    }

    /// <summary>
    /// Writes a <see cref="DataValue"/> using an <see cref="IValueStore"/> to resolve
    /// arena-backed payloads (strings, vectors, arrays, structs, images, byte arrays).
    /// Call this from encoders that operate on values holding page-relative offsets.
    /// </summary>
    internal static void WriteDataValue(BinaryWriter writer, DataValue value, IValueStore store)
    {
        // Byte arrays carry Kind == UInt8 + IsArray; emit the wire-format
        // byte-array tag (56) instead of UInt8 (16) so the reader can
        // distinguish from a scalar UInt8.
        if (value.IsByteArrayKind)
        {
            writer.Write(WireKindByteArray);
            byte[] bytes = value.AsUInt8Array(store);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            return;
        }

        writer.Write((byte)value.Kind);

        switch (value.Kind)
        {
            case DataKind.Type:     writer.Write((byte)value.AsType()); break;
            case DataKind.Boolean:  writer.Write(value.AsBoolean()); break;
            case DataKind.Int8:     writer.Write(value.AsInt8()); break;
            case DataKind.Int16:    writer.Write(value.AsInt16()); break;
            case DataKind.Int32:    writer.Write(value.AsInt32()); break;
            case DataKind.Int64:    writer.Write(value.AsInt64()); break;
            case DataKind.Int128:   WriteInt128(writer, value.AsInt128()); break;
            case DataKind.UInt8:    writer.Write(value.AsUInt8()); break;
            case DataKind.UInt16:   writer.Write(value.AsUInt16()); break;
            case DataKind.UInt32:   writer.Write(value.AsUInt32()); break;
            case DataKind.UInt64:   writer.Write(value.AsUInt64()); break;
            case DataKind.UInt128:  WriteUInt128(writer, value.AsUInt128()); break;
            case DataKind.Float16:  writer.Write(BitConverter.HalfToUInt16Bits(value.AsFloat16())); break;
            case DataKind.Float32:  writer.Write(value.AsFloat32()); break;
            case DataKind.Float64:  writer.Write(value.AsFloat64()); break;
            case DataKind.Decimal:  writer.Write(value.AsDecimal()); break;
            case DataKind.Date:     writer.Write(value.AsDate().DayNumber); break;
            case DataKind.Time:     writer.Write(value.AsTime().Ticks); break;
            case DataKind.Duration: writer.Write(value.AsDuration().Ticks); break;
            case DataKind.Uuid:     writer.Write(value.AsUuid().ToByteArray()); break;

            case DataKind.DateTime:
                DateTimeOffset dto = value.AsDateTime();
                writer.Write(dto.Ticks);
                writer.Write((short)dto.Offset.TotalMinutes);
                break;

            case DataKind.String:
                writer.Write(value.AsString(store));
                break;

            case DataKind.Image:
            case DataKind.Audio:
            case DataKind.Video:
                ReadOnlySpan<byte> blobBytes = value.AsByteSpan(store);
                writer.Write(blobBytes.Length);
                writer.Write(blobBytes);
                break;

            default:
                throw new NotSupportedException($"Cannot serialize DataValue of kind {value.Kind}.");
        }
    }

    internal static void WriteDataValue(BinaryWriter writer, DataValue value)
    {
        writer.Write((byte)value.Kind);

        switch (value.Kind)
        {
            case DataKind.Type:
                writer.Write((byte)value.AsType());
                break;

            case DataKind.Boolean:
                writer.Write(value.AsBoolean());
                break;

            case DataKind.Int8:
                writer.Write(value.AsInt8());
                break;

            case DataKind.Int16:
                writer.Write(value.AsInt16());
                break;

            case DataKind.Int32:
                writer.Write(value.AsInt32());
                break;

            case DataKind.Int64:
                writer.Write(value.AsInt64());
                break;

            case DataKind.Int128:
                WriteInt128(writer, value.AsInt128());
                break;

            case DataKind.UInt8:
                writer.Write(value.AsUInt8());
                break;

            case DataKind.UInt16:
                writer.Write(value.AsUInt16());
                break;

            case DataKind.UInt32:
                writer.Write(value.AsUInt32());
                break;

            case DataKind.UInt64:
                writer.Write(value.AsUInt64());
                break;

            case DataKind.UInt128:
                WriteUInt128(writer, value.AsUInt128());
                break;

            case DataKind.Float16:
                writer.Write(BitConverter.HalfToUInt16Bits(value.AsFloat16()));
                break;

            case DataKind.Float32:
                writer.Write(value.AsFloat32());
                break;

            case DataKind.Float64:
                writer.Write(value.AsFloat64());
                break;

            case DataKind.Decimal:
                writer.Write(value.AsDecimal());
                break;


            case DataKind.String:
                writer.Write(value.AsString());
                break;

            case DataKind.Date:
                DateOnly date = value.AsDate();
                writer.Write(date.DayNumber);
                break;

            case DataKind.DateTime:
                DateTimeOffset dateTimeOffset = value.AsDateTime();
                writer.Write(dateTimeOffset.Ticks);
                writer.Write((short)dateTimeOffset.Offset.TotalMinutes);
                break;

            case DataKind.Image:
                byte[] imageBytes = value.AsImage();
                writer.Write(imageBytes.Length);
                writer.Write(imageBytes);
                break;

            case DataKind.Time:
                writer.Write(value.AsTime().Ticks);
                break;

            case DataKind.Duration:
                writer.Write(value.AsDuration().Ticks);
                break;

            // Byte arrays aren't supported in the no-store wire format
            // (zone maps don't carry byte-array min/max). Falls through to the
            // default arm if any caller actually constructs one — a clear
            // NotSupportedException is the right failure mode.

            case DataKind.Uuid:
                writer.Write(value.AsUuid().ToByteArray());
                break;

            default:
                throw new NotSupportedException($"Cannot serialize DataValue of kind {value.Kind}.");
        }
    }

    /// <summary>
    /// Writes a nullable <see cref="DataValue"/> using a <see cref="BufferedWriter"/>.
    /// </summary>
    internal static void WriteNullableDataValue(BufferedWriter writer, DataValue? value)
    {
        if (!value.HasValue || value.Value.IsNull)
        {
            writer.Write(false); // hasValue = false
            return;
        }

        writer.Write(true); // hasValue = true
        WriteDataValue(writer, value.Value);
    }

    /// <summary>
    /// Writes a <see cref="DataValue"/> using a <see cref="BufferedWriter"/>
    /// for high-throughput serialization on hot index-writing paths.
    /// </summary>
    internal static void WriteDataValue(BufferedWriter writer, DataValue value)
    {
        writer.Write((byte)value.Kind);

        switch (value.Kind)
        {
            case DataKind.Type:
                writer.Write((byte)value.AsType());
                break;

            case DataKind.Boolean:
                writer.Write(value.AsBoolean());
                break;

            case DataKind.Int8:
                writer.Write(value.AsInt8());
                break;

            case DataKind.Int16:
                writer.Write(value.AsInt16());
                break;

            case DataKind.Int32:
                writer.Write(value.AsInt32());
                break;

            case DataKind.Int64:
                writer.Write(value.AsInt64());
                break;

            case DataKind.Int128:
                WriteInt128(writer, value.AsInt128());
                break;

            case DataKind.UInt8:
                writer.Write(value.AsUInt8());
                break;

            case DataKind.UInt16:
                writer.Write(value.AsUInt16());
                break;

            case DataKind.UInt32:
                writer.Write(value.AsUInt32());
                break;

            case DataKind.UInt64:
                writer.Write(value.AsUInt64());
                break;
                
            case DataKind.UInt128:
                WriteUInt128(writer, value.AsUInt128());
                break;

            case DataKind.Float16:
                writer.Write(BitConverter.HalfToUInt16Bits(value.AsFloat16()));
                break;

            case DataKind.Float32:
                writer.Write(value.AsFloat32());
                break;

            case DataKind.Float64:
                writer.Write(value.AsFloat64());
                break;

            case DataKind.Decimal:
                writer.Write(value.AsDecimal());
                break;


            case DataKind.String:
                writer.Write(value.AsString());
                break;

            case DataKind.Date:
                DateOnly dateValue = value.AsDate();
                writer.Write(dateValue.DayNumber);
                break;

            case DataKind.DateTime:
                DateTimeOffset dateTimeValue = value.AsDateTime();
                writer.Write(dateTimeValue.Ticks);
                writer.Write((short)dateTimeValue.Offset.TotalMinutes);
                break;

            case DataKind.Time:
                writer.Write(value.AsTime().Ticks);
                break;

            case DataKind.Duration:
                writer.Write(value.AsDuration().Ticks);
                break;

            // Byte arrays aren't supported in the no-store wire format
            // (zone maps don't carry byte-array min/max). Falls through to the
            // default arm if any caller actually constructs one — a clear
            // NotSupportedException is the right failure mode.

            case DataKind.Image:
                byte[] imageData = value.AsImage();
                writer.Write(imageData.Length);
                writer.Write(imageData);
                break;

            case DataKind.Uuid:
                writer.Write(value.AsUuid().ToByteArray());
                break;

            default:
                throw new NotSupportedException($"Cannot serialize DataValue of kind {value.Kind}.");
        }
    }
}
