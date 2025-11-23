using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.Bloom;
using DatumIngest.IO;

namespace DatumIngest.Indexing;

/// <summary>
/// Opens a v5 unified <c>.datum-index</c> file as a memory-mapped file,
/// parses the section directory, and reconstructs <see cref="SourceIndex"/> instances
/// for each table. All data that supports mmap-backed access (sorted indexes)
/// is exposed through <see cref="SortedIndex"/> instances that read from the
/// shared <see cref="MemoryMappedViewAccessor"/> without heap allocation.
/// </summary>
internal static class UnifiedIndexReader
{
    /// <summary>
    /// Opens a v5 unified index file and returns a <see cref="MappedSourceIndexSet"/>
    /// that owns the memory-mapped file and exposes the deserialized index set.
    /// </summary>
    /// <param name="filePath">Path to the <c>.datum-index</c> file.</param>
    /// <returns>A disposable set that owns the mmap and provides the index data.</returns>
    /// <exception cref="InvalidDataException">Bad magic bytes, unsupported version, or corrupt section data.</exception>
    public static MappedSourceIndexSet Open(string filePath)
    {
        MemoryMappedFile memoryMappedFile = MemoryMappedFile.CreateFromFile(
            filePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);

        try
        {
            MemoryMappedViewAccessor sharedAccessor = memoryMappedFile.CreateViewAccessor(
                0, 0, MemoryMappedFileAccess.Read);

            try
            {
                SourceIndexSet indexSet = ReadIndexSet(memoryMappedFile, sharedAccessor);
                return new MappedSourceIndexSet(memoryMappedFile, sharedAccessor, indexSet);
            }
            catch
            {
                sharedAccessor.Dispose();
                throw;
            }
        }
        catch
        {
            memoryMappedFile.Dispose();
            throw;
        }
    }

    private static SourceIndexSet ReadIndexSet(
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor sharedAccessor)
    {
        // Read and validate header (24 bytes).
        Span<byte> headerBytes = stackalloc byte[UnifiedIndexWriter.HeaderSize];
        sharedAccessor.ReadArray(0, headerBytes);

        if (!headerBytes[..4].SequenceEqual(UnifiedIndexWriter.MagicBytes))
        {
            throw new InvalidDataException("Not a v5 unified index file: bad magic bytes.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[4..]);

        if (version != UnifiedIndexWriter.FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported unified index version {version} (expected {UnifiedIndexWriter.FormatVersion}).");
        }

        // int flags = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[8..]);  // Reserved.
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(headerBytes[12..]);
        // long fileLength = BinaryPrimitives.ReadInt64LittleEndian(headerBytes[16..]);

        if (sectionCount < 0)
        {
            throw new InvalidDataException(
                $"Invalid section count {sectionCount} in unified index header.");
        }

        // Verify the file is large enough to hold the section directory.
        long directoryEnd = UnifiedIndexWriter.HeaderSize
            + (long)sectionCount * UnifiedIndexWriter.DirectoryEntrySize;

        if (directoryEnd > sharedAccessor.Capacity)
        {
            throw new InvalidDataException(
                $"Section directory requires {directoryEnd} bytes but file is only {sharedAccessor.Capacity} bytes.");
        }

        // Read section directory.
        SectionDirectoryEntry[] directory = new SectionDirectoryEntry[sectionCount];
        long directoryOffset = UnifiedIndexWriter.HeaderSize;

        Span<byte> entryBuffer = stackalloc byte[UnifiedIndexWriter.DirectoryEntrySize];

        for (int index = 0; index < sectionCount; index++)
        {
            sharedAccessor.ReadArray(directoryOffset + index * UnifiedIndexWriter.DirectoryEntrySize, entryBuffer);

            directory[index] = new SectionDirectoryEntry(
                (UnifiedIndexSectionType)entryBuffer[0],
                entryBuffer[1],
                BinaryPrimitives.ReadInt64LittleEndian(entryBuffer[2..]),
                BinaryPrimitives.ReadInt64LittleEndian(entryBuffer[10..]));
        }

        // Validate that all section entries point within the file.
        long fileCapacity = sharedAccessor.Capacity;

        for (int index = 0; index < directory.Length; index++)
        {
            SectionDirectoryEntry entry = directory[index];

            if (entry.Offset < 0 || entry.Length < 0 || entry.Offset + entry.Length > fileCapacity)
            {
                throw new InvalidDataException(
                    $"Section {entry.Type} (table {entry.TableIndex}) has invalid bounds: "
                    + $"offset={entry.Offset}, length={entry.Length}.");
            }
        }

        // Read shared fingerprint.
        SourceFingerprint fingerprint = ReadFingerprint(
            sharedAccessor, FindSection(directory, UnifiedIndexSectionType.Fingerprint, UnifiedIndexWriter.SharedTableIndex));

        // Read table directory using a stream view (variable-length UTF-8 names).
        IReadOnlyList<string> tableNames = ReadTableDirectory(
            memoryMappedFile, FindSection(directory, UnifiedIndexSectionType.TableDirectory, UnifiedIndexWriter.SharedTableIndex));

        // Group directory entries by table index (excluding shared sections).
        Dictionary<byte, List<SectionDirectoryEntry>> perTableSections = new();

        foreach (SectionDirectoryEntry entry in directory)
        {
            if (entry.TableIndex == UnifiedIndexWriter.SharedTableIndex)
            {
                continue;
            }

            if (!perTableSections.TryGetValue(entry.TableIndex, out List<SectionDirectoryEntry>? list))
            {
                list = new();
                perTableSections[entry.TableIndex] = list;
            }

            list.Add(entry);
        }

        // Build SourceIndex for each table.
        Dictionary<string, SourceIndex> tables = new(tableNames.Count, StringComparer.Ordinal);

        for (int tableIndex = 0; tableIndex < tableNames.Count; tableIndex++)
        {
            byte tableIndexByte = (byte)tableIndex;

            if (!perTableSections.TryGetValue(tableIndexByte, out List<SectionDirectoryEntry>? sections))
            {
                continue;
            }

            Dictionary<UnifiedIndexSectionType, SectionDirectoryEntry> sectionMap = new(sections.Count);

            foreach (SectionDirectoryEntry entry in sections)
            {
                sectionMap[entry.Type] = entry;
            }

            if (!sectionMap.TryGetValue(UnifiedIndexSectionType.Schema, out SectionDirectoryEntry schemaEntry))
            {
                throw new InvalidDataException(
                    $"Missing required Schema section for table '{tableNames[tableIndex]}'.");
            }

            IndexSchema schema = ReadSchema(memoryMappedFile, schemaEntry);

            IReadOnlyList<IndexChunk> chunks = sectionMap.ContainsKey(UnifiedIndexSectionType.ChunkDirectory)
                ? MappedChunkDirectory.Create(
                    sharedAccessor, memoryMappedFile,
                    sectionMap[UnifiedIndexSectionType.ChunkDirectory].Offset,
                    sectionMap[UnifiedIndexSectionType.ChunkDirectory].Length)
                : Array.Empty<IndexChunk>();

            BloomFilterSet? bloomFilters = sectionMap.TryGetValue(
                UnifiedIndexSectionType.BloomFilters, out SectionDirectoryEntry bloomEntry)
                ? ReadBloomFilters(memoryMappedFile, sharedAccessor, bloomEntry)
                : null;

            Dictionary<string, SortedIndex>? mappedSortedIndexes = null;

            if (sectionMap.TryGetValue(
                UnifiedIndexSectionType.SortedIndexes, out SectionDirectoryEntry sortedEntry))
            {
                mappedSortedIndexes = ReadSortedIndexes(
                    memoryMappedFile, sharedAccessor, sortedEntry);
            }

            BPlusTreeIndexSet? bPlusTreeIndexes = sectionMap.TryGetValue(
                UnifiedIndexSectionType.BTreePages, out SectionDirectoryEntry bTreeEntry)
                ? ReadBPlusTreePages(memoryMappedFile, sharedAccessor, bTreeEntry)
                : null;

            BitmapIndexSet? bitmapIndexes = sectionMap.TryGetValue(
                UnifiedIndexSectionType.BitmapIndexes, out SectionDirectoryEntry bitmapEntry)
                ? ReadBitmapIndexes(memoryMappedFile, sharedAccessor, bitmapEntry)
                : null;

            tables[tableNames[tableIndex]] = new SourceIndex(
                fingerprint, schema, chunks, bloomFilters,
                bPlusTreeIndexes, bitmapIndexes, mappedSortedIndexes);
        }

        return new SourceIndexSet(fingerprint, tables);
    }

    // ───────────────────────── Section lookup ─────────────────────────

    private static SectionDirectoryEntry FindSection(
        SectionDirectoryEntry[] directory,
        UnifiedIndexSectionType type,
        byte tableIndex)
    {
        foreach (SectionDirectoryEntry entry in directory)
        {
            if (entry.Type == type && entry.TableIndex == tableIndex)
            {
                return entry;
            }
        }

        throw new InvalidDataException(
            $"Missing required section {type} for table index {tableIndex}.");
    }

    // ───────────────────────── Fingerprint ─────────────────────────

    private static SourceFingerprint ReadFingerprint(
        MemoryMappedViewAccessor accessor,
        SectionDirectoryEntry entry)
    {
        long fileSize = accessor.ReadInt64(entry.Offset);
        byte[] stripedHash = new byte[32];
        accessor.ReadArray(entry.Offset + 8, stripedHash.AsSpan());
        return new SourceFingerprint(fileSize, stripedHash);
    }

    // ───────────────────────── Table directory ─────────────────────────

    private static IReadOnlyList<string> ReadTableDirectory(
        MemoryMappedFile memoryMappedFile,
        SectionDirectoryEntry entry)
    {
        using MemoryMappedViewStream stream = memoryMappedFile.CreateViewStream(
            entry.Offset, entry.Length, MemoryMappedFileAccess.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        int tableCount = reader.ReadByte();
        List<string> names = new(tableCount);

        for (int index = 0; index < tableCount; index++)
        {
            names.Add(reader.ReadString());
        }

        return names;
    }

    // ───────────────────────── Schema ─────────────────────────

    private static IndexSchema ReadSchema(
        MemoryMappedFile memoryMappedFile,
        SectionDirectoryEntry entry)
    {
        using MemoryMappedViewStream stream = memoryMappedFile.CreateViewStream(
            entry.Offset, entry.Length, MemoryMappedFileAccess.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        long totalRowCount = reader.ReadInt64();
        int columnCount = reader.ReadInt32();
        List<ColumnInfo> columns = new(columnCount);

        for (int index = 0; index < columnCount; index++)
        {
            string name = reader.ReadString();
            DataKind kind = (DataKind)reader.ReadByte();
            bool nullable = reader.ReadBoolean();
            columns.Add(new ColumnInfo(name, kind, nullable));
        }

        Schema schema = new(columns);
        return new IndexSchema(schema, totalRowCount);
    }

    // ───────────────────────── Bloom filters ─────────────────────────

    private static BloomFilterSet ReadBloomFilters(
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor sharedAccessor,
        SectionDirectoryEntry entry)
    {
        using MemoryMappedViewStream stream = memoryMappedFile.CreateViewStream(
            entry.Offset, entry.Length, MemoryMappedFileAccess.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        int columnCount = reader.ReadInt32();
        Dictionary<string, BloomFilter[]> filters = new(columnCount, StringComparer.OrdinalIgnoreCase);
        int chunkCount = 0;

        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            string columnName = reader.ReadString();
            int hashCount = reader.ReadInt32();
            int bitCount = reader.ReadInt32();
            int columnChunkCount = reader.ReadInt32();
            int filterByteSize = reader.ReadInt32();

            chunkCount = columnChunkCount;

            // Compute the absolute offset of the first filter for this column.
            // The stream is at the start of the per-chunk filter data now.
            long filtersBaseOffset = entry.Offset + stream.Position;

            BloomFilter[] columnFilters = new BloomFilter[columnChunkCount];

            for (int chunkIndex = 0; chunkIndex < columnChunkCount; chunkIndex++)
            {
                long filterOffset = filtersBaseOffset + (long)chunkIndex * filterByteSize;
                columnFilters[chunkIndex] = new BloomFilter(sharedAccessor, filterOffset, bitCount, hashCount);
            }

            // Advance the stream past all filter data for this column.
            stream.Position += (long)columnChunkCount * filterByteSize;

            filters[columnName] = columnFilters;
        }

        return new BloomFilterSet(filters, chunkCount);
    }

    // ───────────────────────── Sorted indexes ─────────────────────────

    /// <summary>
    /// Reads sorted indexes, creating <see cref="SortedIndex"/> instances that
    /// operate directly on the shared <see cref="MemoryMappedViewAccessor"/>.
    /// </summary>
    private static Dictionary<string, SortedIndex>? ReadSortedIndexes(
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor sharedAccessor,
        SectionDirectoryEntry entry)
    {
        using MemoryMappedViewStream stream = memoryMappedFile.CreateViewStream(
            entry.Offset, entry.Length, MemoryMappedFileAccess.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        int columnCount = reader.ReadInt32();

        if (columnCount == 0)
        {
            return null;
        }

        Dictionary<string, SortedIndex> mappedIndexes = new(columnCount, StringComparer.OrdinalIgnoreCase);

        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            string columnName = reader.ReadString();
            DataKind kind = (DataKind)reader.ReadByte();
            long entryCount = reader.ReadInt64();
            long keysOffset = reader.ReadInt64();
            long locatorsOffset = reader.ReadInt64();
            long stringTableOffset = reader.ReadInt64();
            long stringTableLength = reader.ReadInt64();

            mappedIndexes[columnName] = new SortedIndex(
                sharedAccessor, kind, entryCount,
                keysOffset, locatorsOffset, stringTableOffset, stringTableLength);

            // Skip past this column's key, locator, and string table data so
            // the stream is positioned at the next column's header.
            int keyWidth = SortedIndexKeyEncoder.GetKeyWidth(kind);
            long dataBytes = entryCount * keyWidth
                + entryCount * SortedIndex.LocatorWidth
                + stringTableLength;
            stream.Seek(dataBytes, SeekOrigin.Current);
        }

        return mappedIndexes;
    }

    // ───────────────────────── B+Tree pages ─────────────────────────

    private static BPlusTreeIndexSet ReadBPlusTreePages(
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor sharedAccessor,
        SectionDirectoryEntry entry)
    {
        using MemoryMappedViewStream stream = memoryMappedFile.CreateViewStream(
            entry.Offset, entry.Length, MemoryMappedFileAccess.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        int columnCount = reader.ReadInt32();
        Dictionary<string, BPlusTreeColumnIndex> indexes = new(columnCount, StringComparer.OrdinalIgnoreCase);

        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            BPlusTreeSectionHeader header = BPlusTreeBulkLoader.ReadSectionHeader(reader);

            long pagesBaseOffset = entry.Offset + stream.Position;

            // Advance past the raw page bytes so the next column header is read correctly.
            stream.Seek((long)header.PageCount * header.PageSize, SeekOrigin.Current);

            BPlusTreeReader bPlusTreeReader = new(header, sharedAccessor, pagesBaseOffset);
            indexes[header.ColumnName] = new BPlusTreeColumnIndex(bPlusTreeReader);
        }

        return new BPlusTreeIndexSet(indexes);
    }

    // ───────────────────────── Bitmap indexes ─────────────────────────

    private static BitmapIndexSet ReadBitmapIndexes(
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor sharedAccessor,
        SectionDirectoryEntry entry)
    {
        using MemoryMappedViewStream stream = memoryMappedFile.CreateViewStream(
            entry.Offset, entry.Length, MemoryMappedFileAccess.Read);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        int columnCount = reader.ReadInt32();
        Dictionary<string, BitmapColumnIndex> indexes = new(columnCount, StringComparer.OrdinalIgnoreCase);

        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            string columnName = reader.ReadString();
            int distinctValueCount = reader.ReadInt32();
            int chunkCount = reader.ReadInt32();

            int[] chunkRowCounts = new int[chunkCount];

            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                chunkRowCounts[chunkIndex] = reader.ReadInt32();
            }

            if (distinctValueCount == 0)
            {
                indexes[columnName] = new BitmapColumnIndex(
                    sharedAccessor,
                    new Dictionary<DataValue, BitmapColumnIndex.ChunkLocation[]>(),
                    chunkCount, chunkRowCounts);
                continue;
            }

            // Peek at the first key's DataKind to determine string vs non-string path.
            DataKind firstKind = (DataKind)reader.ReadByte();

            if (firstKind == DataKind.String)
            {
                // String-keyed: read keys as raw strings, bypassing ReferenceStore.
                Dictionary<string, BitmapColumnIndex.ChunkLocation[]> stringLocations = new(distinctValueCount, StringComparer.Ordinal);

                // Read the first key (kind byte already consumed).
                string firstKey = reader.ReadString();
                stringLocations[firstKey] = ReadChunkLocations(reader, stream, entry.Offset, chunkCount);

                for (int valueIndex = 1; valueIndex < distinctValueCount; valueIndex++)
                {
                    reader.ReadByte(); // skip kind byte (same as firstKind)
                    string key = reader.ReadString();
                    stringLocations[key] = ReadChunkLocations(reader, stream, entry.Offset, chunkCount);
                }

                indexes[columnName] = new BitmapColumnIndex(
                    sharedAccessor, firstKind, stringLocations, chunkCount, chunkRowCounts);
            }
            else
            {
                // Non-string: read keys as DataValues (scope-safe for non-reference types).
                Dictionary<DataValue, BitmapColumnIndex.ChunkLocation[]> chunkLocations = new(distinctValueCount);

                // Reconstruct the first key from its kind byte + remaining bytes.
                DataValue firstValue = DataValueReader.ReadDataValueBody(reader, firstKind);
                chunkLocations[firstValue] = ReadChunkLocations(reader, stream, entry.Offset, chunkCount);

                for (int valueIndex = 1; valueIndex < distinctValueCount; valueIndex++)
                {
                    DataValue value = DataValueReader.ReadDataValue(reader);
                    chunkLocations[value] = ReadChunkLocations(reader, stream, entry.Offset, chunkCount);
                }

                indexes[columnName] = new BitmapColumnIndex(
                    sharedAccessor, chunkLocations, chunkCount, chunkRowCounts);
            }
        }

        return new BitmapIndexSet(indexes);
    }

    private static BitmapColumnIndex.ChunkLocation[] ReadChunkLocations(
        BinaryReader reader, MemoryMappedViewStream stream, long sectionOffset, int chunkCount)
    {
        BitmapColumnIndex.ChunkLocation[] locations =
            new BitmapColumnIndex.ChunkLocation[chunkCount];

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int bitmapLength = reader.ReadInt32();
            long bitmapOffset = sectionOffset + stream.Position;
            locations[chunkIndex] = new BitmapColumnIndex.ChunkLocation(bitmapOffset, bitmapLength);

            if (bitmapLength > 0)
            {
                stream.Seek(bitmapLength, SeekOrigin.Current);
            }
        }

        return locations;
    }

    // ───────────────────────── Internal types ─────────────────────────

    /// <summary>
    /// Parsed section directory entry from the v5 file header.
    /// </summary>
    private readonly record struct SectionDirectoryEntry(
        UnifiedIndexSectionType Type,
        byte TableIndex,
        long Offset,
        long Length);
}
