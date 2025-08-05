using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Serializes a <see cref="SourceIndexSet"/> to a <c>.datum-index</c> binary format (version 2).
/// Each TOC entry carries a table index byte to associate sections with specific sub-tables.
/// Shared sections (fingerprint, table directory) use <see cref="IndexConstants.SharedTableIndex"/>.
/// Sections are written sequentially, followed by a table of contents at the end
/// of the stream. The TOC offset is stored in the fixed 16-byte header.
/// </summary>
public sealed class IndexWriter
{
    /// <summary>
    /// Writes the given index set to the output stream.
    /// The stream must be writable and seekable.
    /// </summary>
    /// <param name="indexSet">The source index set to serialize.</param>
    /// <param name="output">Writable, seekable output stream.</param>
    public void Write(SourceIndexSet indexSet, Stream output)
    {
        Write(indexSet, output, sortedIndexSpillWriter: null);
    }

    /// <summary>
    /// Writes the given index set to the output stream, optionally streaming sorted indexes
    /// directly from a <see cref="SortedIndexSpillWriter"/> instead of materializing them.
    /// When <paramref name="sortedIndexSpillWriter"/> is provided and has sorted index data,
    /// the k-way merge is streamed directly to <paramref name="output"/>, avoiding the
    /// allocation of the full <see cref="ValueIndexEntry"/> arrays.
    /// </summary>
    /// <param name="indexSet">The source index set to serialize.</param>
    /// <param name="output">Writable, seekable output stream.</param>
    /// <param name="sortedIndexSpillWriter">
    /// Optional spill writer holding sorted index runs on disk. When non-null and containing
    /// data, its entries are streamed directly to the output instead of reading from
    /// <see cref="SourceIndex.SortedIndexes"/>.
    /// </param>
    internal void Write(SourceIndexSet indexSet, Stream output, SortedIndexSpillWriter? sortedIndexSpillWriter)
    {
        using BinaryWriter writer = new(output, System.Text.Encoding.UTF8, leaveOpen: true);

        WriteHeader(writer);

        List<(IndexSectionType Type, byte TableIndex, long Offset, long Length)> sections = new();

        // Shared fingerprint section.
        RecordSection(sections, IndexSectionType.Fingerprint, IndexConstants.SharedTableIndex, writer, () =>
            WriteFingerprint(writer, indexSet.Fingerprint));

        // Build ordered table list for deterministic index assignment.
        List<KeyValuePair<string, SourceIndex>> tableList = new(indexSet.Tables);
        tableList.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        // Table directory mapping index → name.
        RecordSection(sections, IndexSectionType.TableDirectory, IndexConstants.SharedTableIndex, writer, () =>
            WriteTableDirectory(writer, tableList));

        // Per-table sections.
        for (int tableIndex = 0; tableIndex < tableList.Count; tableIndex++)
        {
            SourceIndex index = tableList[tableIndex].Value;
            byte tableIndexByte = (byte)tableIndex;

            RecordSection(sections, IndexSectionType.Schema, tableIndexByte, writer, () =>
                WriteSchema(writer, index.Schema));

            RecordSection(sections, IndexSectionType.ChunkDirectory, tableIndexByte, writer, () =>
                WriteChunkDirectory(writer, index.Chunks));

            if (index.BloomFilters is not null)
            {
                RecordSection(sections, IndexSectionType.BloomFilters, tableIndexByte, writer, () =>
                    WriteBloomFilters(writer, index.BloomFilters));
            }

            if (sortedIndexSpillWriter is not null && sortedIndexSpillWriter.HasSortedIndexes)
            {
                RecordSection(sections, IndexSectionType.SortedIndexes, tableIndexByte, writer, () =>
                    sortedIndexSpillWriter.WriteSortedIndexesToStream(writer));
            }
            else if (index.SortedIndexes is not null)
            {
                RecordSection(sections, IndexSectionType.SortedIndexes, tableIndexByte, writer, () =>
                    WriteSortedIndexes(writer, index.SortedIndexes));
            }

            if (index.ZipDirectory is not null)
            {
                RecordSection(sections, IndexSectionType.ZipDirectory, tableIndexByte, writer, () =>
                    WriteZipDirectory(writer, index.ZipDirectory));
            }
        }

        long tableOfContentsOffset = output.Position;
        WriteTableOfContents(writer, sections);

        // Patch the TOC offset in the header.
        output.Position = 8; // After magic (4) + version (2) + flags (2).
        writer.Write(tableOfContentsOffset);
        writer.Flush();
    }

    private static void WriteHeader(BinaryWriter writer)
    {
        writer.Write(IndexConstants.Magic);
        writer.Write(IndexConstants.FormatVersion);
        writer.Write((ushort)0); // Flags — reserved.
        writer.Write(0L);        // TOC offset placeholder.
    }

    private static void RecordSection(
        List<(IndexSectionType, byte, long, long)> sections,
        IndexSectionType type,
        byte tableIndex,
        BinaryWriter writer,
        Action writeBody)
    {
        long start = writer.BaseStream.Position;
        writeBody();
        long length = writer.BaseStream.Position - start;
        sections.Add((type, tableIndex, start, length));
    }

    private static void WriteTableOfContents(
        BinaryWriter writer,
        List<(IndexSectionType Type, byte TableIndex, long Offset, long Length)> sections)
    {
        writer.Write(sections.Count);

        foreach ((IndexSectionType type, byte tableIndex, long offset, long length) in sections)
        {
            writer.Write((byte)type);
            writer.Write(tableIndex);
            writer.Write(offset);
            writer.Write(length);
        }
    }

    private static void WriteTableDirectory(
        BinaryWriter writer,
        List<KeyValuePair<string, SourceIndex>> tableList)
    {
        writer.Write((byte)tableList.Count);

        foreach (KeyValuePair<string, SourceIndex> entry in tableList)
        {
            writer.Write(entry.Key);
        }
    }

    // ───────────────────────── Section writers ─────────────────────────

    private static void WriteFingerprint(BinaryWriter writer, SourceFingerprint fingerprint)
    {
        writer.Write(fingerprint.FileSize);
        writer.Write(fingerprint.StripedHash.Length);
        writer.Write(fingerprint.StripedHash);
    }

    private static void WriteSchema(BinaryWriter writer, IndexSchema schema)
    {
        writer.Write(schema.TotalRowCount);
        IReadOnlyList<ColumnInfo> columns = schema.Schema.Columns;
        writer.Write(columns.Count);

        foreach (ColumnInfo column in columns)
        {
            writer.Write(column.Name);
            writer.Write((byte)column.Kind);
            writer.Write(column.Nullable);
        }
    }

    private static void WriteChunkDirectory(BinaryWriter writer, IReadOnlyList<IndexChunk> chunks)
    {
        writer.Write(chunks.Count);

        foreach (IndexChunk chunk in chunks)
        {
            writer.Write(chunk.RowOffset);
            writer.Write(chunk.RowCount);
            writer.Write(chunk.SourceByteOffset);
            writer.Write(chunk.SourceByteLength);

            writer.Write(chunk.ColumnStatistics.Count);

            foreach (KeyValuePair<string, ChunkColumnStatistics> entry in chunk.ColumnStatistics)
            {
                writer.Write(entry.Key);
                WriteChunkColumnStatistics(writer, entry.Value);
            }
        }
    }

    private static void WriteChunkColumnStatistics(BinaryWriter writer, ChunkColumnStatistics statistics)
    {
        writer.Write(statistics.NullCount);
        writer.Write(statistics.RowCount);
        writer.Write(statistics.EstimatedCardinality);
        WriteNullableDataValue(writer, statistics.Minimum);
        WriteNullableDataValue(writer, statistics.Maximum);
    }

    // ───────────────────────── DataValue serialization ─────────────────────────

    private static void WriteBloomFilters(BinaryWriter writer, BloomFilterSet bloomFilterSet)
    {
        IReadOnlyDictionary<string, BloomFilter[]> filters = bloomFilterSet.Filters;
        writer.Write(filters.Count);       // Number of columns.
        writer.Write(bloomFilterSet.ChunkCount); // Number of chunks.

        foreach (KeyValuePair<string, BloomFilter[]> column in filters)
        {
            writer.Write(column.Key);      // Column name.

            foreach (BloomFilter filter in column.Value)
            {
                writer.Write(filter.BitCount);
                writer.Write(filter.HashCount);
                writer.Write(filter.SizeInBytes);
                writer.Write(filter.Bits);
            }
        }
    }

    private static void WriteSortedIndexes(BinaryWriter writer, SortedValueIndexSet sortedIndexes)
    {
        IReadOnlyDictionary<string, SortedValueIndex> indexes = sortedIndexes.Indexes;
        writer.Write(indexes.Count);

        foreach (KeyValuePair<string, SortedValueIndex> column in indexes)
        {
            writer.Write(column.Key);
            ReadOnlySpan<ValueIndexEntry> entries = column.Value.Entries;
            writer.Write(entries.Length);

            foreach (ValueIndexEntry entry in entries)
            {
                WriteDataValue(writer, entry.Key);
                writer.Write(entry.ChunkIndex);
                writer.Write(entry.RowOffsetInChunk);
            }
        }
    }

    private static void WriteZipDirectory(BinaryWriter writer, ZipDirectoryCache zipDirectory)
    {
        writer.Write(zipDirectory.Count);

        foreach (ZipDirectoryEntry entry in zipDirectory.Entries)
        {
            writer.Write(entry.FileName);
            writer.Write(entry.CompressedSize);
            writer.Write(entry.UncompressedSize);
            writer.Write(entry.LocalHeaderOffset);
            writer.Write(entry.Crc32);
        }
    }

    internal static void WriteNullableDataValue(BinaryWriter writer, DataValue? value)
    {
        if (value is null || value.IsNull)
        {
            writer.Write(false); // hasValue = false
            return;
        }

        writer.Write(true); // hasValue = true
        WriteDataValue(writer, value);
    }

    internal static void WriteDataValue(BinaryWriter writer, DataValue value)
    {
        writer.Write((byte)value.Kind);

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

            case DataKind.Boolean:
                writer.Write(value.AsBoolean());
                break;

            case DataKind.Time:
                writer.Write(value.AsTime().Ticks);
                break;

            case DataKind.Duration:
                writer.Write(value.AsDuration().Ticks);
                break;

            case DataKind.Uuid:
                writer.Write(value.AsUuid().ToByteArray());
                break;

            default:
                throw new NotSupportedException($"Cannot serialize DataValue of kind {value.Kind}.");
        }
    }
}
