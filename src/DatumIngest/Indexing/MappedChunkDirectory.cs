using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Text;
using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Indexing.Sorted;

namespace DatumIngest.Indexing;

/// <summary>
/// A memory-mapped chunk directory that reads chunk metadata and per-column zone
/// map statistics directly from a <see cref="MemoryMappedViewAccessor"/> on demand,
/// without materializing all entries into managed heap objects upfront.
/// Implements <see cref="IReadOnlyList{T}"/> to replace the fully-materialized
/// <c>List&lt;IndexChunk&gt;</c> produced by the eager reader path.
/// </summary>
/// <remarks>
/// <para>
/// At construction, only the small fixed-size column headers and the string table
/// (for string/JSON min/max values) are parsed. Per-chunk fixed fields and per-column
/// zone map entries are read on demand when individual <see cref="IndexChunk"/>
/// instances are accessed via the indexer. Each <see cref="IndexChunk"/> receives a
/// <see cref="MappedChunkStatisticsDictionary"/> that reads statistics from the
/// accessor only when a specific column is looked up.
/// </para>
/// <para>
/// This design reduces managed heap pressure during chunk pruning: the scan operator
/// iterates chunks and looks up 1–2 filter columns from the statistics dictionary.
/// Only the accessed columns are decoded; the remaining columns' zone map bytes are
/// never touched. For large datasets with thousands of chunks and dozens of columns,
/// this avoids allocating tens of thousands of <see cref="ChunkColumnStatistics"/>
/// records that would otherwise be created upfront by the eager reader.
/// </para>
/// </remarks>
internal sealed class MappedChunkDirectory : IReadOnlyList<IndexChunk>
{
    /// <summary>
    /// Width of the per-chunk fixed fields written by <see cref="UnifiedIndexWriter.WriteChunkDirectory"/>:
    /// 2 × <c>i64</c> (rowOffset + rowCount) = 16 bytes. Must stay in sync with the writer's
    /// per-chunk emission loop.
    /// </summary>
    private const int ChunkFixedFieldWidth = 16;

    /// <summary>Width of the three scalar statistics per zone map entry: 3 × <c>i64</c> = 24 bytes.</summary>
    private const int ScalarStatisticsWidth = 24;

    private readonly MemoryMappedViewAccessor _accessor;
    private readonly int _chunkCount;
    private readonly long _chunkFixedFieldsOffset;
    private readonly ZoneMapColumnDescriptor[] _columns;
    private readonly Dictionary<string, int> _columnIndexByName;
    private readonly string[]? _stringTable;

    /// <summary>
    /// Precomputed metadata for a single zone map column, including the absolute
    /// offset of its contiguous zone map data within the memory-mapped file.
    /// </summary>
    /// <param name="Name">Column name.</param>
    /// <param name="Kind">The <see cref="DataKind"/> of this column's keys.</param>
    /// <param name="KeyWidth">Fixed-width encoded key size in bytes.</param>
    /// <param name="ZoneMapBaseOffset">Absolute file offset of this column's first zone map entry.</param>
    /// <param name="EntryStride">Total bytes per zone map entry: <c>2 × (1 + KeyWidth) + 24</c>.</param>
    /// <param name="IsStringType">Whether the column stores string table references.</param>
    private readonly record struct ZoneMapColumnDescriptor(
        string Name,
        DataKind Kind,
        int KeyWidth,
        long ZoneMapBaseOffset,
        int EntryStride,
        bool IsStringType);

    /// <inheritdoc/>
    public int Count => _chunkCount;

    /// <inheritdoc/>
    public IndexChunk this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _chunkCount);
            return ReadChunk(index);
        }
    }

    private MappedChunkDirectory(
        MemoryMappedViewAccessor accessor,
        int chunkCount,
        long chunkFixedFieldsOffset,
        ZoneMapColumnDescriptor[] columns,
        Dictionary<string, int> columnIndexByName,
        string[]? stringTable)
    {
        _accessor = accessor;
        _chunkCount = chunkCount;
        _chunkFixedFieldsOffset = chunkFixedFieldsOffset;
        _columns = columns;
        _columnIndexByName = columnIndexByName;
        _stringTable = stringTable;
    }

    /// <summary>
    /// Creates a mapped chunk directory from a section in the memory-mapped file.
    /// Parses column headers and the string table at construction; all per-chunk
    /// data is read on demand from the shared accessor.
    /// </summary>
    /// <param name="accessor">The shared <see cref="MemoryMappedViewAccessor"/> spanning the entire file.</param>
    /// <param name="memoryMappedFile">The memory-mapped file, used for a temporary stream to parse headers.</param>
    /// <param name="sectionOffset">Absolute byte offset of the chunk directory section.</param>
    /// <param name="sectionLength">Byte length of the chunk directory section.</param>
    internal static MappedChunkDirectory Create(
        MemoryMappedViewAccessor accessor,
        MemoryMappedFile memoryMappedFile,
        long sectionOffset,
        long sectionLength)
    {
        // Parse column headers and string table via a temporary stream
        // (column names are variable-length, BinaryWriter.WriteString format).
        int chunkCount;
        int zoneMapColumnCount;
        long chunkFixedFieldsOffset;
        ZoneMapColumnDescriptor[] columns;
        Dictionary<string, int> columnIndexByName;
        string[]? stringTable;

        if (ExecutionTracer.IsEnabled)
            ExecutionTracer.Write(
                $"ChunkDir.Read   section at absolute offset {sectionOffset}, length {sectionLength}");

        using (MemoryMappedViewStream stream = memoryMappedFile.CreateViewStream(
            sectionOffset, sectionLength, MemoryMappedFileAccess.Read))
        {
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

            chunkCount = reader.ReadInt32();
            zoneMapColumnCount = reader.ReadInt32();

            columns = new ZoneMapColumnDescriptor[zoneMapColumnCount];
            columnIndexByName = new(zoneMapColumnCount, StringComparer.OrdinalIgnoreCase);

            for (int columnIndex = 0; columnIndex < zoneMapColumnCount; columnIndex++)
            {
                string name = reader.ReadString();
                DataKind kind = (DataKind)reader.ReadByte();
                int keyWidth = reader.ReadInt32();
                bool isStringType = kind == DataKind.String;
                int entryStride = 2 * (1 + keyWidth) + ScalarStatisticsWidth;

                columns[columnIndex] = new ZoneMapColumnDescriptor(
                    name, kind, keyWidth, ZoneMapBaseOffset: 0, entryStride, isStringType);
                columnIndexByName[name] = columnIndex;
            }

            // Chunk fixed fields start immediately after the column headers.
            chunkFixedFieldsOffset = sectionOffset + stream.Position;

            if (ExecutionTracer.IsEnabled)
                ExecutionTracer.Write(
                    $"ChunkDir.Read   chunkFixedFields at stream pos {stream.Position} (abs {chunkFixedFieldsOffset}), " +
                    $"chunks={chunkCount}, zoneMapColumns={zoneMapColumnCount}, perChunkWidth={ChunkFixedFieldWidth}");

            // Zone maps start after chunk fixed fields.
            long zoneMapStart = chunkFixedFieldsOffset + (long)chunkCount * ChunkFixedFieldWidth;
            long currentZoneMapOffset = zoneMapStart;

            if (ExecutionTracer.IsEnabled)
                ExecutionTracer.Write(
                    $"ChunkDir.Read   zoneMaps at stream pos {zoneMapStart - sectionOffset} (abs {zoneMapStart})");

            for (int columnIndex = 0; columnIndex < zoneMapColumnCount; columnIndex++)
            {
                ZoneMapColumnDescriptor column = columns[columnIndex];
                columns[columnIndex] = column with { ZoneMapBaseOffset = currentZoneMapOffset };

                if (ExecutionTracer.IsEnabled)
                    ExecutionTracer.Write(
                        $"ChunkDir.Read   col='{column.Name}' kind={column.Kind} keyWidth={column.KeyWidth}  " +
                        $"stride={column.EntryStride}  baseAbs={currentZoneMapOffset}  totalBytes={(long)chunkCount * column.EntryStride}");

                currentZoneMapOffset += (long)chunkCount * column.EntryStride;
            }

            // Read string table (after all zone maps).
            long stringTableStreamPosition = currentZoneMapOffset - sectionOffset;

            if (ExecutionTracer.IsEnabled)
                ExecutionTracer.Write(
                    $"ChunkDir.Read   stringTable at stream pos {stringTableStreamPosition} (abs {currentZoneMapOffset}), " +
                    $"sectionLength={sectionLength}, remaining={sectionLength - stringTableStreamPosition}");

            if (stringTableStreamPosition >= sectionLength)
            {
                throw new InvalidDataException(
                    $"Chunk directory string-table position {stringTableStreamPosition} lands at or past " +
                    $"section length {sectionLength}. Writer and reader disagree on zone-map layout. " +
                    $"chunkCount={chunkCount}, zoneMapColumnCount={zoneMapColumnCount}, chunkFixedFieldWidth={ChunkFixedFieldWidth}.");
            }

            stream.Position = stringTableStreamPosition;

            int stringTableEntryCount = reader.ReadInt32();
            stringTable = null;

            if (ExecutionTracer.IsEnabled)
                ExecutionTracer.Write($"ChunkDir.Read   stringTable count={stringTableEntryCount}");

            if (stringTableEntryCount > 0)
            {
                stringTable = new string[stringTableEntryCount];

                for (int index = 0; index < stringTableEntryCount; index++)
                {
                    int length = reader.ReadInt32();
                    byte[] bytes = reader.ReadBytes(length);
                    stringTable[index] = Encoding.UTF8.GetString(bytes);
                }
            }
        }

        return new MappedChunkDirectory(
            accessor, chunkCount, chunkFixedFieldsOffset, columns, columnIndexByName, stringTable);
    }

    // ───────────────────────── Chunk access ─────────────────────────

    /// <summary>
    /// Reads a single chunk's fixed fields from the accessor and wraps its
    /// column statistics with a lazy mapped dictionary.
    /// </summary>
    private IndexChunk ReadChunk(int chunkIndex)
    {
        long offset = _chunkFixedFieldsOffset + (long)chunkIndex * ChunkFixedFieldWidth;

        long rowOffset = _accessor.ReadInt64(offset);
        long rowCount = _accessor.ReadInt64(offset + 8);

        MappedChunkStatisticsDictionary statistics = new(this, chunkIndex);

        return new IndexChunk(rowOffset, rowCount, statistics);
    }

    /// <summary>
    /// Reads a single zone map entry for the given column and chunk, returning a
    /// <see cref="ChunkColumnStatistics"/> with decoded min/max values.
    /// </summary>
    internal ChunkColumnStatistics ReadZoneMapEntry(int columnIndex, int chunkIndex)
    {
        ZoneMapColumnDescriptor column = _columns[columnIndex];
        long entryOffset = column.ZoneMapBaseOffset + (long)chunkIndex * column.EntryStride;

        // Read min key: null flag (1B) + encoded key (keyWidth B).
        byte minNullFlag = _accessor.ReadByte(entryOffset);
        long cursor = entryOffset + 1;

        Span<byte> minKeyBytes = stackalloc byte[column.KeyWidth];
        _accessor.ReadArray(cursor, minKeyBytes);
        cursor += column.KeyWidth;

        // Read max key: null flag (1B) + encoded key (keyWidth B).
        byte maxNullFlag = _accessor.ReadByte(cursor);
        cursor += 1;

        Span<byte> maxKeyBytes = stackalloc byte[column.KeyWidth];
        _accessor.ReadArray(cursor, maxKeyBytes);
        cursor += column.KeyWidth;

        // Read scalar statistics: nullCount (i64) + rowCount (i64) + estCardinality (i64).
        long nullCount = _accessor.ReadInt64(cursor);
        long rowCount = _accessor.ReadInt64(cursor + 8);
        long estimatedCardinality = _accessor.ReadInt64(cursor + 16);

        DataValue? minimum = DecodeZoneMapKey(minNullFlag, minKeyBytes, column.Kind, column.IsStringType);
        DataValue? maximum = DecodeZoneMapKey(maxNullFlag, maxKeyBytes, column.Kind, column.IsStringType);

        return new ChunkColumnStatistics(minimum, maximum, nullCount, rowCount, estimatedCardinality);
    }

    /// <summary>
    /// Decodes a zone map key from its null flag and encoded bytes.
    /// </summary>
    private DataValue? DecodeZoneMapKey(byte nullFlag, ReadOnlySpan<byte> keyBytes, DataKind kind, bool isStringType)
    {
        if (nullFlag == 0x00)
        {
            return null;
        }

        if (isStringType)
        {
            int entryIndex = BinaryPrimitives.ReadInt32LittleEndian(keyBytes);
            int length = BinaryPrimitives.ReadInt32LittleEndian(keyBytes[4..]);

            if (_stringTable is null || entryIndex < 0 || entryIndex >= _stringTable.Length)
            {
                throw new InvalidDataException(
                    $"Zone map string table reference out of range: {entryIndex}.");
            }

            string value = _stringTable[entryIndex];

            return DataValue.FromString(value);
        }

        return SortedIndexKeyEncoder.Decode(kind, keyBytes);
    }

    // ───────────────────────── IEnumerable ─────────────────────────

    /// <inheritdoc/>
    public IEnumerator<IndexChunk> GetEnumerator()
    {
        for (int index = 0; index < _chunkCount; index++)
        {
            yield return ReadChunk(index);
        }
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ═══════════════════════════════════════════════════════════════════
    //  Nested: lazy statistics dictionary
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// An <see cref="IReadOnlyDictionary{TKey,TValue}"/> that reads per-column
    /// zone map statistics from the memory-mapped accessor on demand. Each call
    /// to <see cref="TryGetValue"/> or the enumerator decodes a single zone map
    /// entry, producing a short-lived <see cref="ChunkColumnStatistics"/> that
    /// the caller can consume without long-lived heap retention.
    /// </summary>
    private sealed class MappedChunkStatisticsDictionary : IReadOnlyDictionary<string, ChunkColumnStatistics>
    {
        private readonly MappedChunkDirectory _directory;
        private readonly int _chunkIndex;

        internal MappedChunkStatisticsDictionary(MappedChunkDirectory directory, int chunkIndex)
        {
            _directory = directory;
            _chunkIndex = chunkIndex;
        }

        /// <inheritdoc/>
        public int Count => _directory._columns.Length;

        /// <inheritdoc/>
        public IEnumerable<string> Keys
        {
            get
            {
                foreach (ZoneMapColumnDescriptor column in _directory._columns)
                {
                    yield return column.Name;
                }
            }
        }

        /// <inheritdoc/>
        public IEnumerable<ChunkColumnStatistics> Values
        {
            get
            {
                for (int columnIndex = 0; columnIndex < _directory._columns.Length; columnIndex++)
                {
                    yield return _directory.ReadZoneMapEntry(columnIndex, _chunkIndex);
                }
            }
        }

        /// <inheritdoc/>
        public ChunkColumnStatistics this[string key]
        {
            get
            {
                if (TryGetValue(key, out ChunkColumnStatistics? value))
                {
                    return value;
                }

                throw new KeyNotFoundException($"Column '{key}' not found in chunk statistics.");
            }
        }

        /// <inheritdoc/>
        public bool ContainsKey(string key) => _directory._columnIndexByName.ContainsKey(key);

        /// <inheritdoc/>
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out ChunkColumnStatistics value)
        {
            if (_directory._columnIndexByName.TryGetValue(key, out int columnIndex))
            {
                value = _directory.ReadZoneMapEntry(columnIndex, _chunkIndex);
                return true;
            }

            value = null;
            return false;
        }

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<string, ChunkColumnStatistics>> GetEnumerator()
        {
            for (int columnIndex = 0; columnIndex < _directory._columns.Length; columnIndex++)
            {
                ZoneMapColumnDescriptor column = _directory._columns[columnIndex];
                ChunkColumnStatistics statistics = _directory.ReadZoneMapEntry(columnIndex, _chunkIndex);
                yield return new KeyValuePair<string, ChunkColumnStatistics>(column.Name, statistics);
            }
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
