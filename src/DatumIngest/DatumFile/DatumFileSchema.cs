using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// The ordered schema of a <c>.datum</c> file, stored in the file footer.
/// Describes all columns present — their names, data kinds, flags, shapes, and externalization policy.
/// </summary>
public sealed class DatumFileSchema
{
    private readonly IReadOnlyList<DatumColumnDescriptor> _columns;

    /// <summary>Creates a schema from an ordered list of column descriptors.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="columns"/> is empty.</exception>
    public DatumFileSchema(IReadOnlyList<DatumColumnDescriptor> columns)
    {
        if (columns.Count == 0)
        {
            throw new ArgumentException("A datum file schema must contain at least one column.");
        }

        _columns = columns;
    }

    /// <summary>The ordered list of column descriptors.</summary>
    public IReadOnlyList<DatumColumnDescriptor> Columns => _columns;

    /// <summary>The number of columns in this schema.</summary>
    public int ColumnCount => _columns.Count;

    /// <summary>
    /// Converts this schema to the query-engine <see cref="Schema"/> model.
    /// </summary>
    public Schema ToSchema()
    {
        ColumnInfo[] columns = new ColumnInfo[_columns.Count];

        for (int index = 0; index < _columns.Count; index++)
        {
            DatumColumnDescriptor descriptor = _columns[index];
            columns[index] = new ColumnInfo(descriptor.Name, descriptor.Kind, descriptor.IsNullable);
        }

        return new Schema(columns);
    }

    /// <summary>
    /// Builds a <see cref="DatumFileSchema"/> from the query-engine <see cref="Schema"/> model.
    /// Fixed shapes and dictionary eligibility are not yet known at this stage;
    /// the writer populates them on the first row group flush.
    /// </summary>
    public static DatumFileSchema FromSchema(Schema schema)
    {
        List<DatumColumnDescriptor> descriptors = new(schema.Columns.Count);

        foreach (ColumnInfo column in schema.Columns)
        {
            DatumColumnFlags flags = column.Nullable ? DatumColumnFlags.Nullable : DatumColumnFlags.None;
            descriptors.Add(new DatumColumnDescriptor(column.Name, column.Kind, flags));
        }

        return new DatumFileSchema(descriptors);
    }

    // ──────────────────── Binary serialization ────────────────────

    /// <summary>
    /// Serializes this schema to the binary writer using the footer schema block format.
    /// </summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(_columns.Count);

        foreach (DatumColumnDescriptor column in _columns)
        {
            writer.Write(column.Name);
            writer.Write((byte)column.Kind);
            writer.Write((byte)column.Flags);

            if (column.HasFixedShape && column.FixedShape is not null)
            {
                writer.Write((ushort)column.FixedShape.Length);

                foreach (int dimension in column.FixedShape)
                {
                    writer.Write(dimension);
                }
            }

            if (column.ExternalizesBlobs)
            {
                // Store as uint32 — threshold values above 4 GiB are unsupported.
                writer.Write((uint)Math.Min(column.ExternalizationThresholdBytes, uint.MaxValue));
            }
        }
    }

    /// <summary>
    /// Deserializes a schema from the given binary reader.
    /// </summary>
    internal static DatumFileSchema Deserialize(BinaryReader reader)
    {
        int columnCount = reader.ReadInt32();
        List<DatumColumnDescriptor> descriptors = new(columnCount);

        for (int index = 0; index < columnCount; index++)
        {
            string name = reader.ReadString();
            DataKind kind = (DataKind)reader.ReadByte();
            DatumColumnFlags flags = (DatumColumnFlags)reader.ReadByte();

            int[]? fixedShape = null;

            if ((flags & DatumColumnFlags.FixedShape) != 0)
            {
                ushort rank = reader.ReadUInt16();
                fixedShape = new int[rank];

                for (int dimension = 0; dimension < rank; dimension++)
                {
                    fixedShape[dimension] = reader.ReadInt32();
                }
            }

            long externalizationThreshold = DatumFileConstants.DefaultExternalizationThresholdBytes;

            if ((flags & DatumColumnFlags.ExternBlobs) != 0)
            {
                externalizationThreshold = reader.ReadUInt32();
            }

            descriptors.Add(new DatumColumnDescriptor(name, kind, flags, fixedShape, externalizationThreshold));
        }

        return new DatumFileSchema(descriptors);
    }
}
