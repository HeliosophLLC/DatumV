using DatumIngest.Model;

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
/// allocation on the read path. Callers must pair <see cref="WriteSchema"/> + N × <see cref="WriteRow"/>
/// with <see cref="ReadSchema"/> + N × <see cref="ReadRow"/> using the same stream.
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
    /// Reads a schema (column count and column names) from the stream, producing the
    /// shared arrays needed for subsequent <see cref="ReadRow"/> calls.
    /// </summary>
    /// <param name="reader">The binary reader to read from.</param>
    /// <param name="names">The column name array, shared across all rows.</param>
    /// <param name="nameIndex">The case-insensitive name-to-ordinal dictionary, shared across all rows.</param>
    internal static void ReadSchema(
        BinaryReader reader,
        out string[] names,
        out Dictionary<string, int> nameIndex)
    {
        int fieldCount = reader.ReadInt32();
        names = new string[fieldCount];
        nameIndex = new Dictionary<string, int>(fieldCount, StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < fieldCount; index++)
        {
            names[index] = reader.ReadString();
            nameIndex[names[index]] = index;
        }
    }

    /// <summary>
    /// Writes a single row's values to the stream. The schema must have been written
    /// previously via <see cref="WriteSchema"/>.
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
    /// <param name="reader">The binary reader to read from.</param>
    /// <param name="names">The shared column name array from <see cref="ReadSchema"/>.</param>
    /// <param name="nameIndex">The shared name-index dictionary from <see cref="ReadSchema"/>.</param>
    /// <returns>A new <see cref="Row"/> with the deserialized values.</returns>
    internal static Row ReadRow(
        BinaryReader reader,
        string[] names,
        Dictionary<string, int> nameIndex)
    {
        DataValue[] values = new DataValue[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            values[index] = ReadDataValue(reader);
        }

        return new Row(names, values, nameIndex);
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
            case DataKind.Scalar:
                writer.Write(value.AsScalar());
                break;

            case DataKind.UInt8:
                writer.Write(value.AsUInt8());
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

            case DataKind.UInt8Array:
                byte[] bytes = value.AsUInt8Array();
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;

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
            DataKind.Scalar => DataValue.FromScalar(reader.ReadSingle()),
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
            DataKind.Uuid => DataValue.FromUuid(new Guid(reader.ReadBytes(16))),
            DataKind.Boolean => DataValue.FromBoolean(reader.ReadBoolean()),
            DataKind.Time => DataValue.FromTime(new TimeOnly(reader.ReadInt64())),
            DataKind.Duration => DataValue.FromDuration(new TimeSpan(reader.ReadInt64())),
            _ => throw new InvalidDataException(
                $"Unknown DataKind {kind} in spill file."),
        };
    }

    private static DataValue ReadUInt8Array(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return DataValue.FromUInt8Array(bytes);
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
}
