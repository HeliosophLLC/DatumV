using System.Runtime.InteropServices;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution;

/// <summary>
/// Binary serialization for <see cref="Row"/> instances used by spill-to-disk operations.
/// Writes rows to a <see cref="BinaryWriter"/> and reads them back from a <see cref="BinaryReader"/>.
/// Supports all 15 <see cref="DataKind"/> discriminators including typed null values.
/// </summary>
/// <remarks>
/// <para>
/// Schema protocol: the first row written to a stream includes column names. Subsequent rows
/// in the same stream omit names and reuse the cached schema arrays, avoiding repeated string
/// allocation on the read path. Callers must pair <see cref="WriteSchema(BinaryWriter, Row)"/> + N × <see cref="WriteRow"/>
/// with <see cref="ReadSchema(BinaryReader, out ColumnLookup)"/> + N × <see cref="ReadRow(BinaryReader, Pool, ColumnLookup)"/>
/// using the same stream.
/// </para>
/// <para>
/// The wire format extends the <c>IndexWriter.WriteDataValue</c> / <c>IndexReader.ReadDataValue</c>
/// pattern from the indexing subsystem to cover all <see cref="DataKind"/> values (adding
/// <see cref="DataKind.Uuid"/>, <see cref="DataKind.Boolean"/>, <see cref="DataKind.Time"/>,
/// and <see cref="DataKind.Duration"/>) plus a null sentinel byte before each payload.
/// </para>
/// </remarks>
internal static class RowSerializer
{
    /// <summary>
    /// Writes the schema (column count and column names) for a row. Must be called once
    /// before writing rows of this schema to the stream.
    /// </summary>
    /// <param name="writer">The binary writer to write to.</param>
    /// <param name="row">The row whose schema (column names) to write.</param>
    internal static void WriteSchema(BinaryWriter writer, Row row)
    {
        writer.Write(row.FieldCount);
        for (int index = 0; index < row.FieldCount; index++)
        {
            writer.Write(row.ColumnNames[index]);
        }
    }

    /// <summary>
    /// Writes the schema (column count and column names) directly from a
    /// <see cref="ColumnLookup"/>. Used by spill code paths that have a schema in hand
    /// before any rows have been seen.
    /// </summary>
    internal static void WriteSchema(BinaryWriter writer, ColumnLookup columnLookup)
    {
        writer.Write(columnLookup.Count);
        for (int index = 0; index < columnLookup.Count; index++)
        {
            writer.Write(columnLookup.ColumnNames[index]);
        }
    }

    /// <summary>
    /// Writes a <see cref="DataValue"/> to the stream as its raw 20-byte struct image.
    /// The caller is responsible for ensuring any arena-backed payload referenced by
    /// <c>_p0</c>/<c>_p1</c> is reachable from whatever arena is handed to readers — typically
    /// by calling <see cref="DataValueRetention.Stabilize"/> against a long-lived consolidated
    /// arena before passing the value here. Inline and sidecar-backed values round-trip
    /// without any arena dependency.
    /// </summary>
    internal static void WriteStabilizedDataValue(BinaryWriter writer, DataValue value)
    {
        Span<byte> buffer = stackalloc byte[DataValueRawSize];
        MemoryMarshal.Write(buffer, in value);
        writer.Write(buffer);
    }

    /// <summary>
    /// Inverse of <see cref="WriteStabilizedDataValue"/>. The returned value's arena-backed
    /// offsets only resolve when read against the same arena the writer stabilized into.
    /// </summary>
    internal static DataValue ReadStabilizedDataValue(BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[DataValueRawSize];
        int read = reader.Read(buffer);
        if (read != DataValueRawSize)
        {
            throw new EndOfStreamException(
                $"Expected {DataValueRawSize} bytes for a DataValue, got {read}.");
        }
        return MemoryMarshal.Read<DataValue>(buffer);
    }

    /// <summary>Size in bytes of the on-disk image of a single <see cref="DataValue"/>.</summary>
    private const int DataValueRawSize = 20;

    /// <summary>
    /// Don't use this
    /// </summary>
    internal static void ReadSchema(
        BinaryReader reader,
        out string[] names,
        out Dictionary<string, int> nameIndex)
    {
        throw new Exception("DON'T USE");
    }

    /// <summary>
    /// Reads a schema (column count and column names) from the stream, producing a <see cref="ColumnLookup"/>
    /// that can be used to construct rows.
    /// </summary>
    /// <param name="reader">The binary reader to read from.</param>
    /// <param name="columnLookup">The populated column lookup.</param>
    internal static void ReadSchema(BinaryReader reader, out ColumnLookup columnLookup)
    {
        int fieldCount = reader.ReadInt32();
        string[] names = new string[fieldCount];

        for (int index = 0; index < fieldCount; index++)
        {
            names[index] = reader.ReadString();
        }

        columnLookup = new ColumnLookup(names);
    }

    /// <summary>
    /// Writes a single row's values to the stream. The schema must have been written
    /// previously via <see cref="WriteSchema(BinaryWriter, Row)"/>.
    /// </summary>
    /// <param name="writer">The binary writer to write to.</param>
    /// <param name="row">The row whose values to serialize.</param>
    internal static void WriteRow(BinaryWriter writer, Row row)
    {
        for (int index = 0; index < row.FieldCount; index++)
        {
            WriteDataValue(writer, row[index]);
        }
    }

    /// <summary>
    /// Reads a single row's values from the stream using a previously read schema.
    /// </summary>
    internal static Row ReadRow(
        BinaryReader reader,
        string[] names,
        Dictionary<string, int> nameIndex)
    {
        throw new Exception("DON'T USE");
    }

    /// <summary>
    /// Reads a single row's values from the stream using a previously read <see cref="ColumnLookup"/>.
    /// </summary>
    /// <param name="reader">The binary reader to read from.</param>
    /// <param name="pool">The pool to rent data values from.</param>
    /// <param name="columnLookup">The column lookup to use for constructing the row.</param>
    /// <returns>A new <see cref="DataValue"/> array with the deserialized values.</returns>
    internal static DataValue[] ReadRow(
        BinaryReader reader,
        Pool pool,
        ColumnLookup columnLookup)
    {
        DataValue[]? values = null;
        
        try
        {
            values = pool.RentDataValues(columnLookup.Count);
            
            for (int index = 0; index < columnLookup.Count; index++)
            {
                values[index] = ReadDataValue(reader);
            }

            return values;
        }
        catch
        {
            if (values != null)
            {
                pool.ReturnDataValues(values);
            }

            throw;
        }

    }

    /// <summary>
    /// Serializes a single <see cref="DataValue"/> to the stream. Writes a null sentinel
    /// byte followed by the kind discriminator and typed payload. Null values omit the payload.
    /// </summary>
    /// <param name="writer">The binary writer to write to.</param>
    /// <param name="value">The value to serialize.</param>
    internal static void WriteDataValue(BinaryWriter writer, DataValue value)
    {
        writer.Write(value.IsNull);
        writer.Write((byte)value.Kind);

        if (value.IsNull)
        {
            return;
        }

        switch (value.Kind)
        {
            case DataKind.Unknown:
                break; // no payload — sentinel kind

            case DataKind.Float32:
                writer.Write(value.AsFloat32());
                break;

            case DataKind.Float64:
                writer.Write(value.AsFloat64());
                break;

            case DataKind.UInt8:
                writer.Write(value.AsUInt8());
                break;

            case DataKind.Int8:
                writer.Write(value.AsInt8());
                break;

            case DataKind.Int16:
                writer.Write(value.AsInt16());
                break;

            case DataKind.UInt16:
                writer.Write(value.AsUInt16());
                break;

            case DataKind.Int32:
                writer.Write(value.AsInt32());
                break;

            case DataKind.UInt32:
                writer.Write(value.AsUInt32());
                break;

            case DataKind.Int64:
                writer.Write(value.AsInt64());
                break;

            case DataKind.UInt64:
                writer.Write(value.AsUInt64());
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

            case DataKind.JsonValue:
                writer.Write(value.AsJsonValue());
                break;

            // Byte arrays aren't supported in the no-store spill body writer
            // (no arena to read bytes from). Falls through to the default arm
            // if a caller actually constructs one — a clear NotSupportedException
            // is the right failure mode.

            case DataKind.Vector:
                float[] vector = value.AsVector();
                writer.Write(vector.Length);
                foreach (float element in vector)
                {
                    writer.Write(element);
                }
                break;

            case DataKind.Matrix:
                float[] matrix = value.AsMatrix(out int rows, out int columns);
                writer.Write(rows);
                writer.Write(columns);
                writer.Write(matrix.Length);
                foreach (float element in matrix)
                {
                    writer.Write(element);
                }
                break;

            case DataKind.Tensor:
                float[] tensor = value.AsTensor(out int[] shape);
                writer.Write(shape.Length);
                foreach (int dimension in shape)
                {
                    writer.Write(dimension);
                }
                writer.Write(tensor.Length);
                foreach (float element in tensor)
                {
                    writer.Write(element);
                }
                break;

            case DataKind.Image:
                byte[] imageBytes = value.AsImage();
                writer.Write(imageBytes.Length);
                writer.Write(imageBytes);
                break;

            case DataKind.Uuid:
                Guid uuid = value.AsUuid();
                writer.Write(uuid.ToByteArray());
                break;

            case DataKind.Boolean:
                writer.Write(value.AsBoolean());
                break;

            case DataKind.Time:
                TimeOnly time = value.AsTime();
                writer.Write(time.Ticks);
                break;

            case DataKind.Duration:
                TimeSpan duration = value.AsDuration();
                writer.Write(duration.Ticks);
                break;

            case DataKind.Array:
                DataValue[] arrayElements = value.AsArray();
                writer.Write((byte)value.ArrayElementKind);
                writer.Write(arrayElements.Length);
                foreach (DataValue element in arrayElements)
                {
                    WriteDataValue(writer, element);
                }
                break;

            case DataKind.Struct:
                DataValue[] structFields = value.AsStruct();
                writer.Write(value.StructFieldCount);
                foreach (DataValue field in structFields)
                {
                    WriteDataValue(writer, field);
                }
                break;

            case DataKind.Type:
                writer.Write((byte)value.AsType());
                break;

            default:
                throw new NotSupportedException(
                    $"Cannot serialize DataValue of kind {value.Kind}.");
        }
    }

    /// <summary>
    /// Deserializes a single <see cref="DataValue"/> from the stream. Reads the null sentinel,
    /// kind discriminator, and (if non-null) the typed payload.
    /// </summary>
    /// <param name="reader">The binary reader to read from.</param>
    /// <returns>The deserialized <see cref="DataValue"/>.</returns>
    internal static DataValue ReadDataValue(BinaryReader reader)
    {
        bool isNull = reader.ReadBoolean();
        DataKind kind = (DataKind)reader.ReadByte();

        if (isNull)
        {
            return DataValue.Null(kind);
        }

        return kind switch
        {
            DataKind.Unknown => default,
            DataKind.Float32 => DataValue.FromFloat32(reader.ReadSingle()),
            DataKind.Float64 => DataValue.FromFloat64(reader.ReadDouble()),
            DataKind.UInt8 => DataValue.FromUInt8(reader.ReadByte()),
            DataKind.Int8 => DataValue.FromInt8(reader.ReadSByte()),
            DataKind.Int16 => DataValue.FromInt16(reader.ReadInt16()),
            DataKind.UInt16 => DataValue.FromUInt16(reader.ReadUInt16()),
            DataKind.Int32 => DataValue.FromInt32(reader.ReadInt32()),
            DataKind.UInt32 => DataValue.FromUInt32(reader.ReadUInt32()),
            DataKind.Int64 => DataValue.FromInt64(reader.ReadInt64()),
            DataKind.UInt64 => DataValue.FromUInt64(reader.ReadUInt64()),
            DataKind.String => DataValue.FromString(reader.ReadString()),
            DataKind.Date => DataValue.FromDate(DateOnly.FromDayNumber(reader.ReadInt32())),
            DataKind.DateTime => DataValue.FromDateTime(
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()))),
            DataKind.JsonValue => DataValue.FromJsonValue(reader.ReadString()),
            DataKind.Vector => ReadVector(reader),
            DataKind.Matrix => ReadMatrix(reader),
            DataKind.Tensor => ReadTensor(reader),
            DataKind.Image => ReadImage(reader),
            DataKind.Uuid => DataValue.FromUuid(new Guid(reader.ReadBytes(16))),
            DataKind.Boolean => DataValue.FromBoolean(reader.ReadBoolean()),
            DataKind.Time => DataValue.FromTime(new TimeOnly(reader.ReadInt64())),
            DataKind.Duration => DataValue.FromDuration(new TimeSpan(reader.ReadInt64())),
            DataKind.Array => ReadArray(reader),
            DataKind.Struct => ReadStruct(reader),
            DataKind.Type => DataValue.FromType((DataKind)reader.ReadByte()),
            _ => throw new InvalidDataException(
                $"Unknown DataKind {kind} in spill file."),
        };
    }

    private static DataValue ReadUInt8Array(BinaryReader reader)
    {
        // Byte arrays require a target arena to land in — the no-store body reader is
        // a remnant of the arena-migration throw-stub era. The spill path uses the
        // store-aware overload further down. If this fires, the caller is reading
        // a kind that shouldn't be in the spill format without a destination arena.
        _ = reader.ReadInt32();
        throw new InvalidOperationException(
            "Cannot deserialize byte-array body without a target IValueStore. "
            + "The store-aware overload is required for arena-backed byte payloads.");
    }

    private static DataValue ReadVector(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        float[] values = new float[length];
        for (int index = 0; index < length; index++)
        {
            values[index] = reader.ReadSingle();
        }
        return DataValue.FromVector(values);
    }

    private static DataValue ReadMatrix(BinaryReader reader)
    {
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int index = 0; index < dataLength; index++)
        {
            values[index] = reader.ReadSingle();
        }
        return DataValue.FromMatrix(values, rows, columns);
    }

    private static DataValue ReadTensor(BinaryReader reader)
    {
        int rank = reader.ReadInt32();
        int[] shape = new int[rank];
        for (int index = 0; index < rank; index++)
        {
            shape[index] = reader.ReadInt32();
        }
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int index = 0; index < dataLength; index++)
        {
            values[index] = reader.ReadSingle();
        }
        return DataValue.FromTensor(values, shape);
    }

    private static DataValue ReadImage(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return DataValue.FromImage(bytes);
    }

    private static DataValue ReadArray(BinaryReader reader)
    {
        DataKind elementKind = (DataKind)reader.ReadByte();
        int length = reader.ReadInt32();
        DataValue[] elements = new DataValue[length];
        for (int index = 0; index < length; index++)
        {
            elements[index] = ReadDataValue(reader);
        }

        return DataValue.FromArray(elementKind, elements);
    }

    private static DataValue ReadStruct(BinaryReader reader)
    {
        short fieldCount = reader.ReadInt16();
        DataValue[] fields = new DataValue[fieldCount];
        for (int index = 0; index < fieldCount; index++)
        {
            fields[index] = ReadDataValue(reader);
        }

        return DataValue.FromStruct(fieldCount, fields);
    }
}
