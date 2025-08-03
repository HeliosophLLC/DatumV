namespace DatumIngest.DatumFile;

/// <summary>
/// Metadata for a single row group within a <c>.datum</c> file.
/// Contains the row count and per-column chunk descriptors (each carrying the
/// page offset, compressed/uncompressed sizes, encoding, compression, and zone map).
/// One entry per row group is stored in the row group directory block of the footer.
/// </summary>
public sealed class DatumRowGroupDescriptor
{
    /// <summary>
    /// Creates a row group descriptor.
    /// </summary>
    /// <param name="rowCount">Number of rows in this row group.</param>
    /// <param name="columnChunks">
    /// Per-column chunk descriptors in schema column order.
    /// Length must equal the number of columns in the file schema.
    /// </param>
    public DatumRowGroupDescriptor(uint rowCount, DatumColumnChunkDescriptor[] columnChunks)
    {
        RowCount = rowCount;
        ColumnChunks = columnChunks;
    }

    /// <summary>Number of rows in this row group.</summary>
    public uint RowCount { get; }

    /// <summary>
    /// Per-column chunk descriptors in schema column order.
    /// Index <c>i</c> corresponds to <c>DatumFileSchema.Columns[i]</c>.
    /// </summary>
    public DatumColumnChunkDescriptor[] ColumnChunks { get; }

    // ──────────────────── Binary serialization ────────────────────

    /// <summary>Serializes this row group descriptor to the binary writer.</summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(RowCount);

        foreach (DatumColumnChunkDescriptor chunk in ColumnChunks)
        {
            chunk.Serialize(writer);
        }
    }

    /// <summary>Deserializes a row group descriptor from the binary reader.</summary>
    internal static DatumRowGroupDescriptor Deserialize(BinaryReader reader, int columnCount)
    {
        uint rowCount = reader.ReadUInt32();
        DatumColumnChunkDescriptor[] chunks = new DatumColumnChunkDescriptor[columnCount];

        for (int index = 0; index < columnCount; index++)
        {
            chunks[index] = DatumColumnChunkDescriptor.Deserialize(reader);
        }

        return new DatumRowGroupDescriptor(rowCount, chunks);
    }
}
