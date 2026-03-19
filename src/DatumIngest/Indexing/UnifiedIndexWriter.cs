using System.Buffers.Binary;
using System.Text;
using DatumIngest.Diagnostics;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.Bloom;
using DatumIngest.IO;

namespace DatumIngest.Indexing;

/// <summary>
/// Writes a v8 unified memory-mapped <c>.datum-index</c> file. The format places a
/// section directory immediately after the fixed-size header so that readers can locate
/// any section by offset without a full scan. Section offsets are backpatched after all
/// data is written.
/// </summary>
/// <remarks>
/// <para>
/// File layout:
/// <code>
/// Header (24 bytes)
///   Magic "DXIX" (4B) + Version 8 (i32) + Flags (i32) + SectionCount (i32) + FileLength (i64)
/// Section Directory (SectionCount × 18 bytes)
///   Per entry: SectionType (1B) + TableIndex (1B) + Offset (i64) + Length (i64)
/// [Sections — contiguous, order determined by writer]
/// IDXT tail (8 bytes) — atomic-commit signal.
/// </code>
/// </para>
/// <para>
/// PR13d (v8) retired SortedIndexes and BTreePages section types. Per-column
/// acceleration is now stored in companion <c>.datum-bptree-{col}</c>
/// page-COW files alongside the data file (parallel to <c>.datum-pkindex</c>),
/// owned by the table provider. The unified sidecar carries fingerprint,
/// schema, chunk directory + zone maps, bloom filters, and bitmap indexes.
/// </para>
/// </remarks>
internal static class UnifiedIndexWriter
{
    /// <summary>Magic bytes identifying a unified index file: ASCII "DXIX".</summary>
    internal static ReadOnlySpan<byte> MagicBytes => "DXIX"u8;

    /// <summary>
    /// Magic bytes terminating a successfully-committed unified index file: ASCII "IDXT".
    /// Presence of this tail at end-of-file is the atomic-commit signal — a torn write
    /// (writer crashed before tail flush) leaves the file without IDXT and the reader
    /// rejects it as torn, falling back to "no valid index" so a REINDEX rebuilds.
    /// </summary>
    internal static ReadOnlySpan<byte> TailMagicBytes => "IDXT"u8;

    /// <summary>Size of the trailing tail block in bytes.</summary>
    internal const int TailSize = 8;

    /// <summary>Format version for the v8 unified layout (PR13d — sorted/BTree sections retired, moved to per-column files).</summary>
    internal const int FormatVersion = 8;

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
        // Build ordered table list for deterministic index assignment.
        List<KeyValuePair<string, SourceIndex>> tableList = new(indexSet.Tables);
        tableList.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        // Plan sections to determine directory size before writing data.
        List<PlannedSection> plannedSections = PlanSections(indexSet, tableList);

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

        long sectionsEndOffset = output.Position;
        long fileLength = sectionsEndOffset + TailSize;

        // Backpatch: file length in header. Includes the trailing IDXT tail so
        // that header.FileLength == on-disk file length when the commit lands.
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

        // Tail-flip-as-commit. The IDXT tail is the last thing written. A reader
        // that finds DXIX-but-no-IDXT knows the writer crashed mid-commit and
        // treats the file as torn (no valid index → fall back to scan).
        output.Position = sectionsEndOffset;
        Span<byte> tailBuffer = stackalloc byte[TailSize];
        BinaryPrimitives.WriteInt32LittleEndian(tailBuffer[..4], plannedSections.Count);
        TailMagicBytes.CopyTo(tailBuffer[4..]);
        output.Write(tailBuffer);

        output.Flush();
    }

    // ───────────────────────── Section planning ─────────────────────────

    private static List<PlannedSection> PlanSections(
        SourceIndexSet indexSet,
        List<KeyValuePair<string, SourceIndex>> tableList)
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
    /// </summary>
    private static void WriteChunkDirectory(
        BinaryWriter writer,
        IReadOnlyList<IndexChunk> chunks,
        Schema schema)
    {
        long sectionStartPos = writer.BaseStream.Position;
        if (DatumActivity.Operators.HasListeners())
            DatumActivity.Operators.Trace($"ChunkDir.Write  section starts at stream pos {sectionStartPos}");

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
        if (DatumActivity.Operators.HasListeners())
            DatumActivity.Operators.Trace(
                $"ChunkDir.Write  chunkFixedFields at stream pos {chunkFixedFieldsPos} (rel {chunkFixedFieldsPos - sectionStartPos}), " +
                $"chunks={chunks.Count}, zoneMapColumns={zoneMapColumns.Count}");

        // Write chunk fixed fields (rowOffset, rowCount) — 16 bytes per chunk.
        foreach (IndexChunk chunk in chunks)
        {
            writer.Write(chunk.RowOffset);
            writer.Write(chunk.RowCount);
        }

        long zoneMapsStartPos = writer.BaseStream.Position;
        if (DatumActivity.Operators.HasListeners())
            DatumActivity.Operators.Trace(
                $"ChunkDir.Write  zoneMaps at stream pos {zoneMapsStartPos} (rel {zoneMapsStartPos - sectionStartPos}), " +
                $"per-chunk width emitted={(zoneMapsStartPos - chunkFixedFieldsPos) / Math.Max(1, chunks.Count)}");

        // Collect string table entries for string/JSON min/max values.
        List<byte[]> stringTableEntries = new();
        Dictionary<string, int> stringTableIndex = new(StringComparer.Ordinal);

        // Write per-column zone maps: ChunkCount records per column.
        foreach ((string columnName, DataKind kind, int keyWidth) in zoneMapColumns)
        {
            bool isStringType = kind == DataKind.String;
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

            if (DatumActivity.Operators.HasListeners())
            {
                long colBytes = writer.BaseStream.Position - colStartPos;
                int expectedStride = 2 * (1 + keyWidth) + 24;
                long actualStride = chunks.Count > 0 ? colBytes / chunks.Count : 0;
                DatumActivity.Operators.Trace(
                    $"ChunkDir.Write  col='{columnName}' kind={kind} keyWidth={keyWidth}  " +
                    $"bytes={colBytes}  actualStride={actualStride}  expectedStride={expectedStride}  " +
                    $"{(actualStride == expectedStride ? "OK" : "STRIDE MISMATCH")}");
            }
        }

        long stringTablePos = writer.BaseStream.Position;
        if (DatumActivity.Operators.HasListeners())
            DatumActivity.Operators.Trace(
                $"ChunkDir.Write  stringTable at stream pos {stringTablePos} (rel {stringTablePos - sectionStartPos}), " +
                $"entries={stringTableEntries.Count}");

        // Write string table.
        writer.Write(stringTableEntries.Count);

        foreach (byte[] entry in stringTableEntries)
        {
            writer.Write(entry.Length);
            writer.Write(entry);
        }

        if (DatumActivity.Operators.HasListeners())
        {
            long endPos = writer.BaseStream.Position;
            DatumActivity.Operators.Trace(
                $"ChunkDir.Write  section ends at stream pos {endPos} (total section bytes {endPos - sectionStartPos})");
        }
    }

    /// <summary>
    /// Writes a single zone map key (min or max) in fixed-width encoding.
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
        if (!value.HasValue || value.Value.IsNull)
        {
            writer.Write((byte)0x00);
            Span<byte> zeroes = stackalloc byte[keyWidth];
            zeroes.Clear();
            writer.Write(zeroes);
            return;
        }

        writer.Write((byte)0x01);

        if (isStringType)
        {
            string stringValue = value.Value.AsString();

            byte[] utf8Bytes = Encoding.UTF8.GetBytes(stringValue);

            if (!stringTableIndex.TryGetValue(stringValue, out int entryIndex))
            {
                entryIndex = stringTableEntries.Count;
                stringTableEntries.Add(utf8Bytes);
                stringTableIndex[stringValue] = entryIndex;
            }

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

    // ───────────────────────── Bitmap indexes ─────────────────────────

    /// <summary>
    /// Writes bitmap indexes with per-value, per-chunk compressed bitsets.
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
