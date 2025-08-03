using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Deserializes a <see cref="SourceIndexSet"/> from a <c>.datum-index</c> binary stream (version 2).
/// Reads the header, seeks to the table of contents, reads the table directory to map
/// table indexes to names, then loads each table's sections individually. The shared
/// fingerprint is applied to all tables.
/// </summary>
public sealed class IndexReader
{
    /// <summary>
    /// Reads a source index set from the given stream.
    /// The stream must be readable and seekable.
    /// </summary>
    /// <param name="input">Readable, seekable input stream.</param>
    /// <returns>The deserialized source index set containing all tables.</returns>
    /// <exception cref="InvalidDataException">Thrown when the stream contains invalid or unsupported index data.</exception>
    public SourceIndexSet Read(Stream input)
    {
        using BinaryReader reader = new(input, System.Text.Encoding.UTF8, leaveOpen: true);

        ReadHeader(reader, out long tableOfContentsOffset);

        input.Position = tableOfContentsOffset;
        List<(IndexSectionType Type, byte TableIndex, long Offset, long Length)> tocEntries = ReadTableOfContents(reader);

        // Read shared fingerprint.
        SourceFingerprint fingerprint = ReadSectionAs(
            reader, input, tocEntries, IndexSectionType.Fingerprint, IndexConstants.SharedTableIndex, ReadFingerprint);

        // Read table directory to get index → name mapping.
        IReadOnlyList<string> tableNames = ReadSectionAs(
            reader, input, tocEntries, IndexSectionType.TableDirectory, IndexConstants.SharedTableIndex, ReadTableDirectory);

        // Group TOC entries by table index (excluding shared sections).
        Dictionary<byte, List<(IndexSectionType Type, long Offset, long Length)>> perTableSections = new();
        foreach ((IndexSectionType type, byte tableIndex, long offset, long length) in tocEntries)
        {
            if (tableIndex == IndexConstants.SharedTableIndex)
            {
                continue;
            }

            if (!perTableSections.TryGetValue(tableIndex, out List<(IndexSectionType, long, long)>? list))
            {
                list = new();
                perTableSections[tableIndex] = list;
            }

            list.Add((type, offset, length));
        }

        // Build SourceIndex for each table.
        Dictionary<string, SourceIndex> tables = new(tableNames.Count, StringComparer.Ordinal);

        for (int i = 0; i < tableNames.Count; i++)
        {
            byte tableIndex = (byte)i;

            if (!perTableSections.TryGetValue(tableIndex, out List<(IndexSectionType, long, long)>? sections))
            {
                continue;
            }

            Dictionary<IndexSectionType, (long Offset, long Length)> sectionMap = new(sections.Count);
            foreach ((IndexSectionType type, long offset, long length) in sections)
            {
                sectionMap[type] = (offset, length);
            }

            IndexSchema schema = ReadSectionAs(
                reader, input, sectionMap, IndexSectionType.Schema, ReadSchema);

            IReadOnlyList<IndexChunk> chunks = sectionMap.ContainsKey(IndexSectionType.ChunkDirectory)
                ? ReadSectionAs(reader, input, sectionMap, IndexSectionType.ChunkDirectory, ReadChunkDirectory)
                : Array.Empty<IndexChunk>();

            BloomFilterSet? bloomFilters = sectionMap.ContainsKey(IndexSectionType.BloomFilters)
                ? ReadSectionAs(reader, input, sectionMap, IndexSectionType.BloomFilters, ReadBloomFilters)
                : null;

            SortedValueIndexSet? sortedIndexes = sectionMap.ContainsKey(IndexSectionType.SortedIndexes)
                ? ReadSectionAs(reader, input, sectionMap, IndexSectionType.SortedIndexes, ReadSortedIndexes)
                : null;

            ZipDirectoryCache? zipDirectory = sectionMap.ContainsKey(IndexSectionType.ZipDirectory)
                ? ReadSectionAs(reader, input, sectionMap, IndexSectionType.ZipDirectory, ReadZipDirectory)
                : null;

            tables[tableNames[i]] = new SourceIndex(fingerprint, schema, chunks, bloomFilters, sortedIndexes, zipDirectory);
        }

        return new SourceIndexSet(fingerprint, tables);
    }

    private static void ReadHeader(BinaryReader reader, out long tableOfContentsOffset)
    {
        Span<byte> magicBuffer = stackalloc byte[4];
        int bytesRead = reader.Read(magicBuffer);

        if (bytesRead < 4 || !magicBuffer.SequenceEqual(IndexConstants.Magic))
        {
            throw new InvalidDataException("Not a valid datum-index file: invalid magic bytes.");
        }

        ushort version = reader.ReadUInt16();

        if (version > IndexConstants.FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported datum-index version {version} (max supported: {IndexConstants.FormatVersion}).");
        }

        _ = reader.ReadUInt16(); // Flags — reserved.
        tableOfContentsOffset = reader.ReadInt64();
    }

    private static List<(IndexSectionType Type, byte TableIndex, long Offset, long Length)> ReadTableOfContents(BinaryReader reader)
    {
        int sectionCount = reader.ReadInt32();
        List<(IndexSectionType, byte, long, long)> sections = new(sectionCount);

        for (int i = 0; i < sectionCount; i++)
        {
            IndexSectionType type = (IndexSectionType)reader.ReadByte();
            byte tableIndex = reader.ReadByte();
            long offset = reader.ReadInt64();
            long length = reader.ReadInt64();
            sections.Add((type, tableIndex, offset, length));
        }

        return sections;
    }

    /// <summary>
    /// Reads a section from the TOC entry list, filtering by section type and table index.
    /// Used for shared sections (fingerprint, table directory).
    /// </summary>
    private static T ReadSectionAs<T>(
        BinaryReader reader,
        Stream input,
        List<(IndexSectionType Type, byte TableIndex, long Offset, long Length)> tocEntries,
        IndexSectionType sectionType,
        byte tableIndex,
        Func<BinaryReader, T> readBody)
    {
        foreach ((IndexSectionType type, byte idx, long offset, long length) in tocEntries)
        {
            if (type == sectionType && idx == tableIndex)
            {
                input.Position = offset;
                return readBody(reader);
            }
        }

        throw new InvalidDataException($"Required section '{sectionType}' (table index {tableIndex}) not found in datum-index file.");
    }

    /// <summary>
    /// Reads a section from a per-table section map.
    /// </summary>
    private static T ReadSectionAs<T>(
        BinaryReader reader,
        Stream input,
        Dictionary<IndexSectionType, (long Offset, long Length)> sections,
        IndexSectionType sectionType,
        Func<BinaryReader, T> readBody)
    {
        if (!sections.TryGetValue(sectionType, out (long Offset, long Length) location))
        {
            throw new InvalidDataException($"Required section '{sectionType}' not found in datum-index file.");
        }

        input.Position = location.Offset;
        return readBody(reader);
    }

    private static IReadOnlyList<string> ReadTableDirectory(BinaryReader reader)
    {
        byte tableCount = reader.ReadByte();
        List<string> names = new(tableCount);

        for (int i = 0; i < tableCount; i++)
        {
            names.Add(reader.ReadString());
        }

        return names;
    }

    // ───────────────────────── Section readers ─────────────────────────

    private static SourceFingerprint ReadFingerprint(BinaryReader reader)
    {
        long fileSize = reader.ReadInt64();
        int hashLength = reader.ReadInt32();
        byte[] stripedHash = reader.ReadBytes(hashLength);
        return new SourceFingerprint(fileSize, stripedHash);
    }

    private static IndexSchema ReadSchema(BinaryReader reader)
    {
        long totalRowCount = reader.ReadInt64();
        int columnCount = reader.ReadInt32();
        List<ColumnInfo> columns = new(columnCount);

        for (int i = 0; i < columnCount; i++)
        {
            string name = reader.ReadString();
            DataKind kind = (DataKind)reader.ReadByte();
            bool nullable = reader.ReadBoolean();
            columns.Add(new ColumnInfo(name, kind, nullable));
        }

        Schema schema = new(columns);
        return new IndexSchema(schema, totalRowCount);
    }

    private static IReadOnlyList<IndexChunk> ReadChunkDirectory(BinaryReader reader)
    {
        int chunkCount = reader.ReadInt32();
        List<IndexChunk> chunks = new(chunkCount);

        for (int i = 0; i < chunkCount; i++)
        {
            long rowOffset = reader.ReadInt64();
            long rowCount = reader.ReadInt64();
            long sourceByteOffset = reader.ReadInt64();
            long sourceByteLength = reader.ReadInt64();

            int columnCount = reader.ReadInt32();
            Dictionary<string, ChunkColumnStatistics> columnStatistics =
                new(columnCount, StringComparer.OrdinalIgnoreCase);

            for (int j = 0; j < columnCount; j++)
            {
                string columnName = reader.ReadString();
                ChunkColumnStatistics statistics = ReadChunkColumnStatistics(reader);
                columnStatistics[columnName] = statistics;
            }

            chunks.Add(new IndexChunk(rowOffset, rowCount, sourceByteOffset, sourceByteLength, columnStatistics));
        }

        return chunks;
    }

    private static ChunkColumnStatistics ReadChunkColumnStatistics(BinaryReader reader)
    {
        long nullCount = reader.ReadInt64();
        long rowCount = reader.ReadInt64();
        long estimatedCardinality = reader.ReadInt64();
        DataValue? minimum = ReadNullableDataValue(reader);
        DataValue? maximum = ReadNullableDataValue(reader);
        return new ChunkColumnStatistics(minimum, maximum, nullCount, rowCount, estimatedCardinality);
    }

    // ───────────────────────── DataValue deserialization ─────────────────────────

    private static BloomFilterSet ReadBloomFilters(BinaryReader reader)
    {
        int columnCount = reader.ReadInt32();
        int chunkCount = reader.ReadInt32();

        Dictionary<string, BloomFilter[]> filters = new(columnCount, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < columnCount; i++)
        {
            string columnName = reader.ReadString();
            BloomFilter[] columnFilters = new BloomFilter[chunkCount];

            for (int j = 0; j < chunkCount; j++)
            {
                int bitCount = reader.ReadInt32();
                int hashCount = reader.ReadInt32();
                int byteCount = reader.ReadInt32();
                byte[] bits = reader.ReadBytes(byteCount);
                columnFilters[j] = new BloomFilter(bits, bitCount, hashCount);
            }

            filters[columnName] = columnFilters;
        }

        return new BloomFilterSet(filters, chunkCount);
    }

    private static SortedValueIndexSet ReadSortedIndexes(BinaryReader reader)
    {
        int columnCount = reader.ReadInt32();
        Dictionary<string, SortedValueIndex> indexes = new(columnCount, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < columnCount; i++)
        {
            string columnName = reader.ReadString();
            int entryCount = reader.ReadInt32();
            ValueIndexEntry[] entries = new ValueIndexEntry[entryCount];

            for (int j = 0; j < entryCount; j++)
            {
                DataValue key = ReadDataValue(reader);
                int chunkIndex = reader.ReadInt32();
                long rowOffsetInChunk = reader.ReadInt64();
                entries[j] = new ValueIndexEntry(key, chunkIndex, rowOffsetInChunk);
            }

            indexes[columnName] = new SortedValueIndex(entries);
        }

        return new SortedValueIndexSet(indexes);
    }

    private static ZipDirectoryCache ReadZipDirectory(BinaryReader reader)
    {
        int entryCount = reader.ReadInt32();
        ZipDirectoryEntry[] entries = new ZipDirectoryEntry[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            string fileName = reader.ReadString();
            long compressedSize = reader.ReadInt64();
            long uncompressedSize = reader.ReadInt64();
            long localHeaderOffset = reader.ReadInt64();
            uint crc32 = reader.ReadUInt32();
            entries[i] = new ZipDirectoryEntry(fileName, compressedSize, uncompressedSize, localHeaderOffset, crc32);
        }

        return new ZipDirectoryCache(entries);
    }

    internal static DataValue? ReadNullableDataValue(BinaryReader reader)
    {
        bool hasValue = reader.ReadBoolean();

        if (!hasValue)
        {
            return null;
        }

        return ReadDataValue(reader);
    }

    internal static DataValue ReadDataValue(BinaryReader reader)
    {
        DataKind kind = (DataKind)reader.ReadByte();

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
            DataKind.Boolean => DataValue.FromBoolean(reader.ReadBoolean()),
            DataKind.Time => DataValue.FromTime(new TimeOnly(reader.ReadInt64())),
            DataKind.Duration => DataValue.FromDuration(TimeSpan.FromTicks(reader.ReadInt64())),
            DataKind.Uuid => DataValue.FromUuid(new Guid(reader.ReadBytes(16))),
            _ => throw new InvalidDataException($"Unknown DataKind {kind} in datum-index file.")
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
        for (int i = 0; i < length; i++)
        {
            values[i] = reader.ReadSingle();
        }
        return DataValue.FromVector(values);
    }

    private static DataValue ReadMatrix(BinaryReader reader)
    {
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int i = 0; i < dataLength; i++)
        {
            values[i] = reader.ReadSingle();
        }
        return DataValue.FromMatrix(values, rows, columns);
    }

    private static DataValue ReadTensor(BinaryReader reader)
    {
        int rank = reader.ReadInt32();
        int[] shape = new int[rank];
        for (int i = 0; i < rank; i++)
        {
            shape[i] = reader.ReadInt32();
        }
        int dataLength = reader.ReadInt32();
        float[] values = new float[dataLength];
        for (int i = 0; i < dataLength; i++)
        {
            values[i] = reader.ReadSingle();
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
