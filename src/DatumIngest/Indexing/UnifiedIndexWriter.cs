using System.Buffers.Binary;
using System.Text;
using DatumIngest.Diagnostics;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.Bloom;
using DatumIngest.IO;

namespace DatumIngest.Indexing;

/// <summary>
/// Writes a v5 unified memory-mapped <c>.datum-index</c> file. The format places a
/// section directory immediately after the fixed-size header so that readers can locate
/// any section by offset without a full scan. Section offsets are backpatched after all
/// data is written.
/// </summary>
/// <remarks>
/// <para>
/// File layout:
/// <code>
/// Header (24 bytes)
///   Magic "DXIX" (4B) + Version 5 (i32) + Flags (i32) + SectionCount (i32) + FileLength (i64)
/// Section Directory (SectionCount × 18 bytes)
///   Per entry: SectionType (1B) + TableIndex (1B) + Offset (i64) + Length (i64)
/// [Sections — contiguous, order determined by writer]
/// </code>
/// </para>
/// </remarks>
internal static class UnifiedIndexWriter
{
    /// <summary>Magic bytes identifying a unified index file: ASCII "DXIX".</summary>
    internal static ReadOnlySpan<byte> MagicBytes => "DXIX"u8;

    /// <summary>Format version for the v6 unified layout.</summary>
    internal const int FormatVersion = 6;

    /// <summary>Size of the fixed file header in bytes.</summary>
    internal const int HeaderSize = 24;

    /// <summary>Size of each section directory entry in bytes.</summary>
    internal const int DirectoryEntrySize = 18;

    /// <summary>
    /// Reserved table index for shared sections (fingerprint, table directory).
    /// </summary>
    internal const byte SharedTableIndex = 0xFF;

    /// <summary>
    /// Writes a unified index file for the given index set.
    /// The stream must be writable and seekable (for backpatching the header and directory).
    /// </summary>
    /// <param name="indexSet">The source index set to serialize.</param>
    /// <param name="output">Writable, seekable output stream.</param>
    public static void Write(SourceIndexSet indexSet, Stream output)
    {
        Write(indexSet, output, sortedIndexSpillWriter: null);
    }

    /// <summary>
    /// Writes a unified index file for the given index set, optionally streaming sorted
    /// indexes directly from a <see cref="SortedIndexSpillWriter"/> instead of materializing
    /// all <see cref="ValueIndexEntry"/> arrays in memory. When the spill writer is provided
    /// and contains data, columns are categorized into flat sorted indexes (below the B+Tree
    /// threshold) and B+Tree indexes (above the threshold). Sorted columns are materialized
    /// one at a time to allow the two-pass v5 encoding (keys, then locators). B+Tree columns
    /// are streamed directly into <see cref="BPlusTreeBulkLoader"/> without materialization.
    /// </summary>
    /// <param name="indexSet">The source index set to serialize.</param>
    /// <param name="output">Writable, seekable output stream.</param>
    /// <param name="sortedIndexSpillWriter">
    /// Optional spill writer holding sorted index runs on disk. When non-null and containing
    /// data, its entries are streamed directly into the output; otherwise the writer falls
    /// back to <see cref="SourceIndex.BPlusTreeIndexes"/> when present.
    /// </param>
    internal static void Write(
        SourceIndexSet indexSet,
        Stream output,
        SortedIndexSpillWriter? sortedIndexSpillWriter)
    {
        // Build ordered table list for deterministic index assignment.
        List<KeyValuePair<string, SourceIndex>> tableList = new(indexSet.Tables);
        tableList.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        // Plan sections to determine directory size before writing data.
        List<PlannedSection> plannedSections = PlanSections(indexSet, tableList, sortedIndexSpillWriter);

        int directorySize = plannedSections.Count * DirectoryEntrySize;
        long dataStartOffset = HeaderSize + directorySize;

        // Write placeholder header + placeholder directory.
        output.Position = 0;
        Span<byte> headerBuffer = stackalloc byte[HeaderSize];
        headerBuffer.Clear();
        MagicBytes.CopyTo(headerBuffer);
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer[4..], FormatVersion);
        // Flags = 0 (reserved).
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer[12..], plannedSections.Count);
        // FileLength placeholder = 0.
        output.Write(headerBuffer);

        // Skip directory space — we'll backpatch it.
        output.Position = dataStartOffset;

        // Write each section, recording actual offsets.
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);

        (UnifiedIndexSectionType Type, byte TableIndex, long Offset, long Length)[] actualEntries =
            new (UnifiedIndexSectionType, byte, long, long)[plannedSections.Count];

        for (int sectionIndex = 0; sectionIndex < plannedSections.Count; sectionIndex++)
        {
            PlannedSection plan = plannedSections[sectionIndex];
            long sectionStart = output.Position;

            plan.WriteAction(writer);
            writer.Flush();

            long sectionLength = output.Position - sectionStart;
            actualEntries[sectionIndex] = (plan.Type, plan.TableIndex, sectionStart, sectionLength);
        }

        long fileLength = output.Position;

        // Backpatch: file length in header.
        output.Position = 16; // Offset of FileLength field.
        Span<byte> fileLengthBuffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(fileLengthBuffer, fileLength);
        output.Write(fileLengthBuffer);

        // Backpatch: section directory.
        output.Position = HeaderSize;
        Span<byte> entryBuffer = stackalloc byte[DirectoryEntrySize];

        foreach ((UnifiedIndexSectionType type, byte tableIndex, long offset, long length) in actualEntries)
        {
            entryBuffer[0] = (byte)type;
            entryBuffer[1] = tableIndex;
            BinaryPrimitives.WriteInt64LittleEndian(entryBuffer[2..], offset);
            BinaryPrimitives.WriteInt64LittleEndian(entryBuffer[10..], length);
            output.Write(entryBuffer);
        }

        output.Flush();
    }

    // ───────────────────────── Section planning ─────────────────────────

    private static List<PlannedSection> PlanSections(
        SourceIndexSet indexSet,
        List<KeyValuePair<string, SourceIndex>> tableList,
        SortedIndexSpillWriter? sortedIndexSpillWriter)
    {
        List<PlannedSection> sections = new();

        // Shared sections.
        sections.Add(new PlannedSection(
            UnifiedIndexSectionType.Fingerprint, SharedTableIndex,
            w => WriteFingerprint(w, indexSet.Fingerprint)));

        sections.Add(new PlannedSection(
            UnifiedIndexSectionType.TableDirectory, SharedTableIndex,
            w => WriteTableDirectory(w, tableList)));

        // Per-table sections.
        for (int tableIndex = 0; tableIndex < tableList.Count; tableIndex++)
        {
            SourceIndex index = tableList[tableIndex].Value;
            byte tableIndexByte = (byte)tableIndex;

            sections.Add(new PlannedSection(
                UnifiedIndexSectionType.Schema, tableIndexByte,
                w => WriteSchema(w, index.Schema)));

            sections.Add(new PlannedSection(
                UnifiedIndexSectionType.ChunkDirectory, tableIndexByte,
                w => WriteChunkDirectory(w, index.Chunks, index.Schema.Schema)));

            if (index.BloomFilters is not null)
            {
                BloomFilterSet bloomFilters = index.BloomFilters;
                sections.Add(new PlannedSection(
                    UnifiedIndexSectionType.BloomFilters, tableIndexByte,
                    w => WriteBloomFilters(w, bloomFilters)));
            }

            if (sortedIndexSpillWriter is not null && sortedIndexSpillWriter.HasSortedIndexes)
            {
                // Categorize columns into sorted (flat) vs B+Tree based on entry count.
                HashSet<string>? bTreeColumns = CategorizeBPlusTreeColumns(sortedIndexSpillWriter);

                bool hasSortedColumns = bTreeColumns is null
                    || bTreeColumns.Count < CountColumnsWithEntries(sortedIndexSpillWriter);

                if (hasSortedColumns)
                {
                    SortedIndexSpillWriter spillCapture = sortedIndexSpillWriter;
                    Schema schema = index.Schema.Schema;
                    sections.Add(new PlannedSection(
                        UnifiedIndexSectionType.SortedIndexes, tableIndexByte,
                        w => WriteStreamedSortedIndexes(w, spillCapture, schema, bTreeColumns)));
                }

                if (bTreeColumns is not null && bTreeColumns.Count > 0)
                {
                    SortedIndexSpillWriter spillCapture = sortedIndexSpillWriter;
                    Schema schema = index.Schema.Schema;
                    sections.Add(new PlannedSection(
                        UnifiedIndexSectionType.BTreePages, tableIndexByte,
                        w => WriteStreamedBTreePages(w, spillCapture, schema, bTreeColumns)));
                }
            }
            else if (index.BPlusTreeIndexes is not null)
            {
                BPlusTreeIndexSet bPlusTreeIndexes = index.BPlusTreeIndexes;
                sections.Add(new PlannedSection(
                    UnifiedIndexSectionType.BTreePages, tableIndexByte,
                    w => WriteBTreePages(w, bPlusTreeIndexes)));
            }

            if (index.BitmapIndexes is not null)
            {
                BitmapIndexSet bitmapIndexes = index.BitmapIndexes;
                sections.Add(new PlannedSection(
                    UnifiedIndexSectionType.BitmapIndexes, tableIndexByte,
                    w => WriteBitmapIndexes(w, bitmapIndexes)));
            }
        }

        return sections;
    }

    private readonly record struct PlannedSection(
        UnifiedIndexSectionType Type,
        byte TableIndex,
        Action<BinaryWriter> WriteAction);

    // ───────────────────────── Fingerprint ─────────────────────────

    private static void WriteFingerprint(BinaryWriter writer, SourceFingerprint fingerprint)
    {
        writer.Write(fingerprint.FileSize);
        writer.Write(fingerprint.StripedHash);
    }

    // ───────────────────────── Table directory ─────────────────────────

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

    // ───────────────────────── Schema ─────────────────────────

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

    // ───────────────────────── Chunk directory ─────────────────────────

    /// <summary>
    /// Writes the chunk directory with fixed-width per-column zone maps.
    /// Layout:
    /// <code>
    /// [ChunkCount: i32] [ColumnCount: i32]
    /// Per-column header: [Name: length-prefixed UTF-8] [DataKind: 1B] [KeyWidth: i32]
    /// Chunk fixed fields: ChunkCount × 32B (rowOffset, rowCount)
    /// Per-column zone maps: ChunkCount × (2×KeyWidth + 24) per column
    ///   encodedMin(KeyWidth) + encodedMax(KeyWidth) + nullCount(i64) + rowCount(i64) + estCardinality(i64)
    /// Zone map string table: for string/json min/max values
    /// </code>
    /// </summary>
    private static void WriteChunkDirectory(
        BinaryWriter writer,
        IReadOnlyList<IndexChunk> chunks,
        Schema schema)
    {
        long sectionStartPos = writer.BaseStream.Position;
        if (ExecutionTracer.IsEnabled)
            ExecutionTracer.Write($"ChunkDir.Write  section starts at stream pos {sectionStartPos}");

        writer.Write(chunks.Count);

        // Determine indexable columns from the schema.
        List<(string Name, DataKind Kind, int KeyWidth)> zoneMapColumns = new();

        foreach (ColumnInfo column in schema.Columns)
        {
            if (TryGetZoneMapKeyWidth(column.Kind, out int keyWidth))
            {
                zoneMapColumns.Add((column.Name, column.Kind, keyWidth));
            }
        }

        writer.Write(zoneMapColumns.Count);

        // Write per-column headers.
        foreach ((string name, DataKind kind, int keyWidth) in zoneMapColumns)
        {
            writer.Write(name);
            writer.Write((byte)kind);
            writer.Write(keyWidth);
        }

        long chunkFixedFieldsPos = writer.BaseStream.Position;
        if (ExecutionTracer.IsEnabled)
            ExecutionTracer.Write(
                $"ChunkDir.Write  chunkFixedFields at stream pos {chunkFixedFieldsPos} (rel {chunkFixedFieldsPos - sectionStartPos}), " +
                $"chunks={chunks.Count}, zoneMapColumns={zoneMapColumns.Count}");

        // Write chunk fixed fields (rowOffset, rowCount) — 16 bytes per chunk.
        foreach (IndexChunk chunk in chunks)
        {
            writer.Write(chunk.RowOffset);
            writer.Write(chunk.RowCount);
        }

        long zoneMapsStartPos = writer.BaseStream.Position;
        if (ExecutionTracer.IsEnabled)
            ExecutionTracer.Write(
                $"ChunkDir.Write  zoneMaps at stream pos {zoneMapsStartPos} (rel {zoneMapsStartPos - sectionStartPos}), " +
                $"per-chunk width emitted={(zoneMapsStartPos - chunkFixedFieldsPos) / Math.Max(1, chunks.Count)}");

        // Collect string table entries for string/JSON min/max values.
        List<byte[]> stringTableEntries = new();
        Dictionary<string, int> stringTableIndex = new(StringComparer.Ordinal);

        // Write per-column zone maps: ChunkCount records per column.
        foreach ((string columnName, DataKind kind, int keyWidth) in zoneMapColumns)
        {
            bool isStringType = kind is DataKind.String or DataKind.JsonValue;
            long colStartPos = writer.BaseStream.Position;

            foreach (IndexChunk chunk in chunks)
            {
                ChunkColumnStatistics? statistics = chunk.ColumnStatistics.TryGetValue(
                    columnName, out ChunkColumnStatistics? stats) ? stats : null;

                // Encode min.
                WriteZoneMapKey(writer, statistics?.Minimum, kind, keyWidth,
                    isStringType, stringTableEntries, stringTableIndex);

                // Encode max.
                WriteZoneMapKey(writer, statistics?.Maximum, kind, keyWidth,
                    isStringType, stringTableEntries, stringTableIndex);

                // Statistics scalars.
                writer.Write(statistics?.NullCount ?? 0L);
                writer.Write(statistics?.RowCount ?? 0L);
                writer.Write(statistics?.EstimatedCardinality ?? 0L);
            }

            if (ExecutionTracer.IsEnabled)
            {
                long colBytes = writer.BaseStream.Position - colStartPos;
                int expectedStride = 2 * (1 + keyWidth) + 24;
                long actualStride = chunks.Count > 0 ? colBytes / chunks.Count : 0;
                ExecutionTracer.Write(
                    $"ChunkDir.Write  col='{columnName}' kind={kind} keyWidth={keyWidth}  " +
                    $"bytes={colBytes}  actualStride={actualStride}  expectedStride={expectedStride}  " +
                    $"{(actualStride == expectedStride ? "OK" : "STRIDE MISMATCH")}");
            }
        }

        long stringTablePos = writer.BaseStream.Position;
        if (ExecutionTracer.IsEnabled)
            ExecutionTracer.Write(
                $"ChunkDir.Write  stringTable at stream pos {stringTablePos} (rel {stringTablePos - sectionStartPos}), " +
                $"entries={stringTableEntries.Count}");

        // Write string table.
        writer.Write(stringTableEntries.Count);

        foreach (byte[] entry in stringTableEntries)
        {
            writer.Write(entry.Length);
            writer.Write(entry);
        }

        if (ExecutionTracer.IsEnabled)
        {
            long endPos = writer.BaseStream.Position;
            ExecutionTracer.Write(
                $"ChunkDir.Write  section ends at stream pos {endPos} (total section bytes {endPos - sectionStartPos})");
        }
    }

    /// <summary>
    /// Writes a single zone map key (min or max) in fixed-width encoding.
    /// For null/missing values, writes all-zero bytes (which sort before any real value
    /// in the sort-preserving encoding). A separate null flag is embedded in the first byte
    /// of the key slot: 0x00 = null/missing, 0x01 = has value, followed by the encoded key.
    /// </summary>
    private static void WriteZoneMapKey(
        BinaryWriter writer,
        DataValue? value,
        DataKind kind,
        int keyWidth,
        bool isStringType,
        List<byte[]> stringTableEntries,
        Dictionary<string, int> stringTableIndex)
    {
        // Total slot size = 1 (null flag) + keyWidth.
        if (!value.HasValue || value.Value.IsNull)
        {
            // Null flag + zero-filled key.
            writer.Write((byte)0x00);
            Span<byte> zeroes = stackalloc byte[keyWidth];
            zeroes.Clear();
            writer.Write(zeroes);
            return;
        }

        writer.Write((byte)0x01);

        if (isStringType)
        {
            // String zone maps store a string table reference.
            string stringValue = kind == DataKind.String
                ? value.Value.AsString()
                : value.Value.AsJsonValue();

            byte[] utf8Bytes = Encoding.UTF8.GetBytes(stringValue);

            if (!stringTableIndex.TryGetValue(stringValue, out int entryIndex))
            {
                entryIndex = stringTableEntries.Count;
                stringTableEntries.Add(utf8Bytes);
                stringTableIndex[stringValue] = entryIndex;
            }

            // Write string table reference: offset (i32) + length (i32) = 8 bytes = keyWidth for strings.
            Span<byte> referenceBuffer = stackalloc byte[keyWidth];
            BinaryPrimitives.WriteInt32LittleEndian(referenceBuffer, entryIndex);
            BinaryPrimitives.WriteInt32LittleEndian(referenceBuffer[4..], utf8Bytes.Length);
            writer.Write(referenceBuffer);
        }
        else
        {
            Span<byte> keyBuffer = stackalloc byte[keyWidth];
            SortedIndexKeyEncoder.Encode(value.Value, keyBuffer);
            writer.Write(keyBuffer);
        }
    }

    /// <summary>
    /// Returns the zone map key width for a given data kind, or <c>false</c> if the
    /// kind cannot be used for zone maps.
    /// </summary>
    private static bool TryGetZoneMapKeyWidth(DataKind kind, out int keyWidth)
    {
        try
        {
            keyWidth = SortedIndexKeyEncoder.GetKeyWidth(kind);
            return true;
        }
        catch (NotSupportedException)
        {
            keyWidth = 0;
            return false;
        }
    }

    // ───────────────────────── Bloom filters ─────────────────────────

    /// <summary>
    /// Writes bloom filters with uniform-size bitsets per column for O(1) chunk access.
    /// Layout:
    /// <code>
    /// [ColumnCount: i32]
    /// Per column:
    ///   [Name: length-prefixed UTF-8] [HashCount: i32] [BitCount: i32]
    ///   [ChunkCount: i32] [FilterByteSize: i32]
    ///   ChunkCount × FilterByteSize bytes (contiguous, uniform-size filters)
    /// </code>
    /// </summary>
    private static void WriteBloomFilters(BinaryWriter writer, BloomFilterSet bloomFilterSet)
    {
        IReadOnlyDictionary<string, BloomFilter[]> filters = bloomFilterSet.Filters;
        writer.Write(filters.Count);

        Span<byte> zeroPadding = stackalloc byte[256];
        zeroPadding.Clear();

        foreach (KeyValuePair<string, BloomFilter[]> column in filters)
        {
            writer.Write(column.Key);

            BloomFilter[] chunkFilters = column.Value;

            // Determine uniform size: the maximum filter byte size across all chunks.
            int uniformBitCount = 0;
            int uniformHashCount = 0;
            int uniformByteSize = 0;

            foreach (BloomFilter filter in chunkFilters)
            {
                if (filter.SizeInBytes > uniformByteSize)
                {
                    uniformBitCount = filter.BitCount;
                    uniformHashCount = filter.HashCount;
                    uniformByteSize = filter.SizeInBytes;
                }
            }

            writer.Write(uniformHashCount);
            writer.Write(uniformBitCount);
            writer.Write(chunkFilters.Length);
            writer.Write(uniformByteSize);

            // Write each chunk's filter, padding smaller ones to uniformByteSize.
            foreach (BloomFilter filter in chunkFilters)
            {
                writer.Write(filter.Bits);

                int padding = uniformByteSize - filter.SizeInBytes;

                if (padding > 0)
                {

                    int remaining = padding;

                    while (remaining > 0)
                    {
                        int chunk = Math.Min(remaining, zeroPadding.Length);
                        writer.Write(zeroPadding[..chunk]);
                        remaining -= chunk;
                    }
                }
            }
        }
    }

    // ───────────────── Streamed sorted indexes (spill writer) ──────────────────

    /// <summary>
    /// Writes sorted indexes in v5 format by streaming entries from a
    /// <see cref="SortedIndexSpillWriter"/>. Each column is materialized one at a time via
    /// <see cref="SortedIndexSpillWriter.GetMergedEntries"/> to allow the two-pass encoding
    /// (keys first, then locators). Columns promoted to B+Tree are excluded.
    /// </summary>
    private static void WriteStreamedSortedIndexes(
        BinaryWriter writer,
        SortedIndexSpillWriter spillWriter,
        Schema schema,
        IReadOnlySet<string>? excludeColumns)
    {
        // Count eligible columns.
        int columnCount = 0;

        foreach (string columnName in spillWriter.IndexedColumnNames)
        {
            if (excludeColumns is null || !excludeColumns.Contains(columnName))
            {
                columnCount++;
            }
        }

        writer.Write(columnCount);

        Span<byte> sortedKeyBuffer = stackalloc byte[16];

        foreach (string columnName in spillWriter.IndexedColumnNames)
        {
            if (excludeColumns is not null && excludeColumns.Contains(columnName))
            {
                continue;
            }

            // Materialize this column's entries so we can do the two-pass v5 encoding.
            ValueIndexEntry[] entries = spillWriter.GetMergedEntries(columnName);

            DataKind kind = SortedIndexSpillWriter.ResolveDataKind(columnName, schema);
            int keyWidth = SortedIndexKeyEncoder.GetKeyWidth(kind);

            writer.Write(columnName);
            writer.Write((byte)kind);
            writer.Write((long)entries.Length);

            long directoryPosition = writer.BaseStream.Position;
            writer.Write(0L); // keysOffset placeholder
            writer.Write(0L); // locatorsOffset placeholder
            writer.Write(0L); // stringTableOffset placeholder
            writer.Write(0L); // stringTableLength placeholder

            long keysOffset = writer.BaseStream.Position;

            if (kind is DataKind.String or DataKind.JsonValue)
            {
                List<byte[]> stringTable = new();
                Dictionary<string, (int Offset, int Length)> stringDedup = new(StringComparer.Ordinal);
                int currentStringOffset = 0;
                Span<byte> referenceSlice = sortedKeyBuffer[..keyWidth];

                foreach (ValueIndexEntry entry in entries)
                {
                    string stringValue = kind == DataKind.String
                        ? entry.Key.AsString()
                        : entry.Key.AsJsonValue();

                    if (!stringDedup.TryGetValue(stringValue, out (int Offset, int Length) reference))
                    {
                        byte[] utf8Bytes = Encoding.UTF8.GetBytes(stringValue);
                        reference = (currentStringOffset, utf8Bytes.Length);
                        stringDedup[stringValue] = reference;
                        stringTable.Add(utf8Bytes);
                        currentStringOffset += utf8Bytes.Length;
                    }

                    SortedIndexKeyEncoder.EncodeStringReference(reference.Offset, reference.Length, referenceSlice);
                    writer.Write(referenceSlice);
                }

                long locatorsOffset = writer.BaseStream.Position;

                foreach (ValueIndexEntry entry in entries)
                {
                    writer.Write(entry.ChunkIndex);
                    writer.Write(entry.RowOffsetInChunk);
                }

                long stringTableOffset = writer.BaseStream.Position;

                foreach (byte[] utf8Bytes in stringTable)
                {
                    writer.Write(utf8Bytes);
                }

                long stringTableLength = writer.BaseStream.Position - stringTableOffset;

                long savedPosition = writer.BaseStream.Position;
                writer.BaseStream.Position = directoryPosition;
                writer.Write(keysOffset);
                writer.Write(locatorsOffset);
                writer.Write(stringTableOffset);
                writer.Write(stringTableLength);
                writer.BaseStream.Position = savedPosition;
            }
            else
            {
                Span<byte> keySlice = sortedKeyBuffer[..keyWidth];

                foreach (ValueIndexEntry entry in entries)
                {
                    SortedIndexKeyEncoder.Encode(entry.Key, keySlice);
                    writer.Write(keySlice);
                }

                long locatorsOffset = writer.BaseStream.Position;

                foreach (ValueIndexEntry entry in entries)
                {
                    writer.Write(entry.ChunkIndex);
                    writer.Write(entry.RowOffsetInChunk);
                }

                long savedPosition = writer.BaseStream.Position;
                writer.BaseStream.Position = directoryPosition;
                writer.Write(keysOffset);
                writer.Write(locatorsOffset);
                writer.Write(0L);
                writer.Write(0L);
                writer.BaseStream.Position = savedPosition;
            }
        }
    }

    // ─────────────── Streamed B+Tree pages (spill writer) ────────────────

    /// <summary>
    /// Writes B+Tree indexes by streaming merged entries from a
    /// <see cref="SortedIndexSpillWriter"/> into <see cref="BPlusTreeBulkLoader"/>.
    /// Each column's entries are consumed in a single streaming pass without materializing
    /// the full sorted array.
    /// </summary>
    private static void WriteStreamedBTreePages(
        BinaryWriter writer,
        SortedIndexSpillWriter spillWriter,
        Schema schema,
        IReadOnlySet<string> columnFilter)
    {
        int columnCount = 0;

        foreach (string columnName in spillWriter.IndexedColumnNames)
        {
            if (columnFilter.Contains(columnName))
            {
                columnCount++;
            }
        }

        writer.Write(columnCount);

        foreach (string columnName in spillWriter.IndexedColumnNames)
        {
            if (!columnFilter.Contains(columnName))
            {
                continue;
            }

            DataKind keyKind = SortedIndexSpillWriter.ResolveDataKind(columnName, schema);
            long totalEntries = spillWriter.GetTotalEntryCount(columnName);

            BPlusTreeBulkLoader.Build(
                spillWriter.EnumerateMergedEntries(columnName),
                columnName,
                keyKind,
                writer,
                totalEntries);
        }
    }

    // ────────────────── B+Tree column categorization ──────────────────

    /// <summary>
    /// Determines which columns from the spill writer should be written as B+Tree indexes.
    /// Columns whose total entry count exceeds <see cref="IndexConstants.BPlusTreeAutoThreshold"/>
    /// are promoted to B+Tree; all others remain as flat sorted indexes.
    /// </summary>
    /// <returns>
    /// A set of column names that should use B+Tree, or <c>null</c> if no columns qualify.
    /// </returns>
    private static HashSet<string>? CategorizeBPlusTreeColumns(SortedIndexSpillWriter spillWriter)
    {
        HashSet<string> bTreeColumns = new(StringComparer.OrdinalIgnoreCase);

        foreach (string columnName in spillWriter.IndexedColumnNames)
        {
            if (spillWriter.GetTotalEntryCount(columnName) > IndexConstants.BPlusTreeAutoThreshold)
            {
                bTreeColumns.Add(columnName);
            }
        }

        return bTreeColumns.Count > 0 ? bTreeColumns : null;
    }

    /// <summary>
    /// Counts the number of columns in the spill writer that have at least one entry.
    /// </summary>
    private static int CountColumnsWithEntries(SortedIndexSpillWriter spillWriter)
    {
        int count = 0;

        foreach (string _ in spillWriter.IndexedColumnNames)
        {
            count++;
        }

        return count;
    }

    // ───────────────────────── B+Tree pages ─────────────────────────

    /// <summary>
    /// Writes B+Tree indexes as contiguous 8 KiB page arrays.
    /// Layout:
    /// <code>
    /// [ColumnCount: i32]
    /// Per column:
    ///   [SectionHeader: column name, kind, root, entry count, height, page size, page count]
    ///   PageCount × 8192 bytes (contiguous raw pages)
    /// </code>
    /// </summary>
    private static void WriteBTreePages(BinaryWriter writer, BPlusTreeIndexSet bPlusTreeIndexes)
    {
        IReadOnlyCollection<string> columnNames = bPlusTreeIndexes.ColumnNames;
        writer.Write(columnNames.Count);

        foreach (string columnName in columnNames)
        {
            if (!bPlusTreeIndexes.TryGetIndex(columnName, out BPlusTreeColumnIndex? columnIndex))
            {
                continue;
            }

            BPlusTreeReader reader = columnIndex.Reader;
            BPlusTreeBulkLoader.WriteSectionHeader(writer, reader.Header);

            foreach (byte[] rawPage in reader.RawPages)
            {
                writer.Write(rawPage);
            }
        }
    }

    // ───────────────────────── Bitmap indexes ─────────────────────────

    /// <summary>
    /// Writes bitmap indexes with per-value, per-chunk compressed bitsets.
    /// Format matches v3 for now — the reader will access these from the mmap'd view.
    /// </summary>
    private static void WriteBitmapIndexes(BinaryWriter writer, BitmapIndexSet bitmapIndexes)
    {
        IReadOnlyCollection<string> columnNames = bitmapIndexes.ColumnNames;
        writer.Write(columnNames.Count);

        foreach (string columnName in columnNames)
        {
            if (!bitmapIndexes.TryGetIndex(columnName, out BitmapColumnIndex? columnIndex))
            {
                continue;
            }

            writer.Write(columnName);
            writer.Write(columnIndex.DistinctValues.Count);
            writer.Write(columnIndex.ChunkCount);

            IReadOnlyList<int> chunkRowCounts = columnIndex.ChunkRowCounts;

            for (int chunkIndex = 0; chunkIndex < columnIndex.ChunkCount; chunkIndex++)
            {
                writer.Write(chunkRowCounts[chunkIndex]);
            }

            IReadOnlyDictionary<DataValue, byte[][]> compressedBitmaps = columnIndex.CompressedBitmaps;

            foreach (KeyValuePair<DataValue, byte[][]> entry in compressedBitmaps)
            {
                DataValueWriter.WriteDataValue(writer, entry.Key);

                byte[][] chunkBitmaps = entry.Value;

                for (int chunkIndex = 0; chunkIndex < chunkBitmaps.Length; chunkIndex++)
                {
                    writer.Write(chunkBitmaps[chunkIndex].Length);
                    writer.Write(chunkBitmaps[chunkIndex]);
                }
            }
        }
    }
}
