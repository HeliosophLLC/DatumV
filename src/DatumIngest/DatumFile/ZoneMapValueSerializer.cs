using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Wire-format serialization for zone map min/max values. Zone maps hold
/// <see cref="object"/>-boxed managed primitives (e.g. <see cref="long"/>,
/// <see cref="double"/>, <see cref="string"/>, <see cref="DateOnly"/>) — this
/// class writes and reads them using the same wire format as
/// <c>IndexWriter.WriteDataValue</c> for comparable <see cref="DataKind"/>s.
/// </summary>
/// <remarks>
/// Only the comparable types that can appear in zone maps are supported:
/// numeric scalars, <see cref="DataKind.Boolean"/>, <see cref="DataKind.String"/>,
/// temporal types, and <see cref="DataKind.Uuid"/>. Reference types like
/// <see cref="DataKind.Image"/>, <see cref="DataKind.Audio"/>,
/// <see cref="DataKind.Video"/>, <see cref="DataKind.Json"/>, or typed-array
/// columns never appear in zone maps and are rejected.
/// </remarks>
internal static class ZoneMapValueSerializer
{
    internal static void Write(BinaryWriter writer, DataKind kind, object value)
    {
        writer.Write((byte)kind);

        switch (kind)
        {
            case DataKind.Boolean:  writer.Write((bool)value); break;
            case DataKind.Int8:     writer.Write((sbyte)value); break;
            case DataKind.UInt8:    writer.Write((byte)value); break;
            case DataKind.Int16:    writer.Write((short)value); break;
            case DataKind.UInt16:   writer.Write((ushort)value); break;
            case DataKind.Int32:    writer.Write((int)value); break;
            case DataKind.UInt32:   writer.Write((uint)value); break;
            case DataKind.Int64:    writer.Write((long)value); break;
            case DataKind.UInt64:   writer.Write((ulong)value); break;
            case DataKind.Float32:  writer.Write((float)value); break;
            case DataKind.Float64:  writer.Write((double)value); break;
            case DataKind.String:   writer.Write((string)value); break;
            case DataKind.Date:     writer.Write(((DateOnly)value).DayNumber); break;
            case DataKind.DateTime:
                DateTimeOffset dto = (DateTimeOffset)value;
                writer.Write(dto.Ticks);
                writer.Write((short)dto.Offset.TotalMinutes);
                break;
            case DataKind.Time:     writer.Write(((TimeOnly)value).Ticks); break;
            case DataKind.Duration: writer.Write(((TimeSpan)value).Ticks); break;
            case DataKind.Uuid:     writer.Write(((Guid)value).ToByteArray()); break;
            default:
                throw new NotSupportedException(
                    $"Zone map value serialization not supported for kind {kind}.");
        }
    }

    internal static object Read(BinaryReader reader, out DataKind kind)
    {
        kind = (DataKind)reader.ReadByte();

        return kind switch
        {
            DataKind.Boolean => reader.ReadBoolean(),
            DataKind.Int8 => reader.ReadSByte(),
            DataKind.UInt8 => reader.ReadByte(),
            DataKind.Int16 => reader.ReadInt16(),
            DataKind.UInt16 => reader.ReadUInt16(),
            DataKind.Int32 => reader.ReadInt32(),
            DataKind.UInt32 => reader.ReadUInt32(),
            DataKind.Int64 => reader.ReadInt64(),
            DataKind.UInt64 => reader.ReadUInt64(),
            DataKind.Float32 => reader.ReadSingle(),
            DataKind.Float64 => reader.ReadDouble(),
            DataKind.String => reader.ReadString(),
            DataKind.Date => DateOnly.FromDayNumber(reader.ReadInt32()),
            DataKind.DateTime => ReadDateTimeOffset(reader),
            DataKind.Time => new TimeOnly(reader.ReadInt64()),
            DataKind.Duration => new TimeSpan(reader.ReadInt64()),
            DataKind.Uuid => new Guid(reader.ReadBytes(16)),
            _ => throw new NotSupportedException(
                $"Zone map value deserialization not supported for kind {kind}."),
        };
    }

    private static DateTimeOffset ReadDateTimeOffset(BinaryReader reader)
    {
        long ticks = reader.ReadInt64();
        short offsetMinutes = reader.ReadInt16();
        return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
    }
}
