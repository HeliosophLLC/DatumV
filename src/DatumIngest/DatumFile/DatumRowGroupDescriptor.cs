namespace DatumIngest.DatumFile;

/// <summary>
/// Metadata for a single row group within a <c>.datum</c> file.
/// Contains the row count and per-column chunk descriptors (each carrying the
/// page offset, compressed/uncompressed sizes, encoding, compression, and zone map).
/// One entry per row group is stored in the row group directory block of the footer.
/// </summary>
/// <remarks>
/// When a file has the <see cref="DatumFileFlags.HasTombstones"/> flag set, each row
/// group additionally carries an <see cref="ActiveRowCount"/> and an optional
/// <see cref="TombstoneBitmap"/> indicating which rows have been logically deleted.
/// </remarks>
public sealed class DatumRowGroupDescriptor
{
    /// <summary>
    /// Creates a row group descriptor with no tombstones.
    /// </summary>
    /// <param name="rowCount">Number of rows in this row group.</param>
    /// <param name="columnChunks">
    /// Per-column chunk descriptors in schema column order.
    /// Length must equal the number of columns in the file schema.
    /// </param>
    public DatumRowGroupDescriptor(uint rowCount, DatumColumnChunkDescriptor[] columnChunks)
        : this(rowCount, rowCount, null, columnChunks)
    {
    }

    /// <summary>
    /// Creates a row group descriptor with explicit tombstone state.
    /// </summary>
    /// <param name="rowCount">Total (physical) number of rows in this row group.</param>
    /// <param name="activeRowCount">
    /// Number of non-deleted rows. Equals <paramref name="rowCount"/> when no rows are deleted.
    /// </param>
    /// <param name="tombstoneBitmap">
    /// Per-row deletion bitmap, one bit per row (LSB-first within each byte).
    /// A set bit means the row is deleted. <see langword="null"/> when no rows are deleted.
    /// Length must be <c>ceil(rowCount / 8)</c> when present.
    /// </param>
    /// <param name="columnChunks">
    /// Per-column chunk descriptors in schema column order.
    /// </param>
    public DatumRowGroupDescriptor(
        uint rowCount,
        uint activeRowCount,
        byte[]? tombstoneBitmap,
        DatumColumnChunkDescriptor[] columnChunks)
    {
        RowCount = rowCount;
        ActiveRowCount = activeRowCount;
        TombstoneBitmap = tombstoneBitmap;
        ColumnChunks = columnChunks;
    }

    /// <summary>Total (physical) number of rows in this row group, including deleted rows.</summary>
    public uint RowCount { get; }

    /// <summary>
    /// Number of non-deleted rows. Equals <see cref="RowCount"/> when no rows are tombstoned.
    /// </summary>
    public uint ActiveRowCount { get; }

    /// <summary>
    /// Per-row deletion bitmap (LSB-first within each byte). A set bit means the row
    /// at that index has been logically deleted. <see langword="null"/> when no rows are deleted.
    /// </summary>
    public byte[]? TombstoneBitmap { get; }

    /// <summary>
    /// Per-column chunk descriptors in schema column order.
    /// Index <c>i</c> corresponds to <c>DatumFileSchema.Columns[i]</c>.
    /// </summary>
    public DatumColumnChunkDescriptor[] ColumnChunks { get; }

    /// <summary>
    /// Returns <see langword="true"/> when the row at <paramref name="rowIndex"/> has been
    /// logically deleted. Returns <see langword="false"/> when no tombstone bitmap exists.
    /// </summary>
    /// <param name="rowIndex">Zero-based row index within this row group.</param>
    public bool IsRowDeleted(int rowIndex)
    {
        if (TombstoneBitmap is null) return false;
        int byteIndex = rowIndex >> 3;
        int bitIndex = rowIndex & 7;
        return (TombstoneBitmap[byteIndex] & (1 << bitIndex)) != 0;
    }

    // ──────────────────── Binary serialization ────────────────────

    /// <summary>
    /// Serializes this row group descriptor to the binary writer.
    /// When <paramref name="hasTombstones"/> is <see langword="true"/>, the tombstone
    /// fields (<c>ActiveRowCount</c> and bitmap) are included after the row count.
    /// </summary>
    /// <param name="writer">The target binary writer.</param>
    /// <param name="hasTombstones">
    /// Whether the file has the <see cref="DatumFileFlags.HasTombstones"/> flag set.
    /// </param>
    internal void Serialize(BinaryWriter writer, bool hasTombstones = false)
    {
        writer.Write(RowCount);

        if (hasTombstones)
        {
            writer.Write(ActiveRowCount);
            int bitmapLength = TombstoneBitmap?.Length ?? 0;
            writer.Write((ushort)bitmapLength);
            if (bitmapLength > 0)
            {
                writer.Write(TombstoneBitmap!);
            }
        }

        foreach (DatumColumnChunkDescriptor chunk in ColumnChunks)
        {
            chunk.Serialize(writer);
        }
    }

    /// <summary>Deserializes a row group descriptor from the binary reader.</summary>
    /// <param name="reader">The source binary reader.</param>
    /// <param name="columnCount">Number of columns in the file schema.</param>
    /// <param name="hasTombstones">
    /// Whether the file has the <see cref="DatumFileFlags.HasTombstones"/> flag set.
    /// </param>
    /// <param name="store">Optional value store for zone map DataValues.</param>
    internal static DatumRowGroupDescriptor Deserialize(BinaryReader reader, int columnCount, bool hasTombstones = false, Model.IValueStore? store = null)
    {
        uint rowCount = reader.ReadUInt32();

        uint activeRowCount = rowCount;
        byte[]? tombstoneBitmap = null;

        if (hasTombstones)
        {
            activeRowCount = reader.ReadUInt32();
            ushort bitmapLength = reader.ReadUInt16();
            if (bitmapLength > 0)
            {
                tombstoneBitmap = reader.ReadBytes(bitmapLength);
            }
        }

        DatumColumnChunkDescriptor[] chunks = new DatumColumnChunkDescriptor[columnCount];

        for (int index = 0; index < columnCount; index++)
        {
            chunks[index] = DatumColumnChunkDescriptor.Deserialize(reader, store);
        }

        return new DatumRowGroupDescriptor(rowCount, activeRowCount, tombstoneBitmap, chunks);
    }
}
