using DatumIngest.Execution;
using DatumIngest.Indexing.BTree;
using DatumIngest.IO;
using DatumIngest.Model;
using ZstdSharp;

namespace DatumIngest.Indexing.Sorted;

/// <summary>
/// Accumulates sorted index entries chunk-by-chunk, spilling each sorted run to a
/// temporary file on disk. At finalization, performs a k-way merge of all sorted runs
/// into a single <see cref="SortedIndex"/> with constant memory overhead
/// regardless of total row count.
/// </summary>
/// <remarks>
/// <para>
/// During the row-reading pass, callers invoke <see cref="AddEntry"/> for each non-null
/// value in an indexed column. Entries are buffered in memory for the current chunk only.
/// When <see cref="FlushChunk"/> is called (at chunk boundaries), the buffered entries are
/// sorted by key and appended to a per-column temporary file. Memory is then released.
/// </para>
/// <para>
/// At finalization (<see cref="SortedIndex"/>), each column's temp file is
/// read back as a sequence of pre-sorted runs and merged using a priority queue into a
/// single sorted array of <see cref="ValueIndexEntry"/>, producing the final
/// <see cref="SortedIndex"/>.
/// </para>
/// </remarks>
internal sealed class SortedIndexSpillWriter : IDisposable
{
    /// <summary>
    /// Maximum string length for auto-indexed string columns. Strings observed to exceed
    /// this length cause the column to be dropped from indexing.
    /// </summary>
    internal const int AutoIndexMaxStringLength = 16;

    /// <summary>
    /// Reusable comparison for sorting <see cref="ValueIndexEntry"/> by key. Fast-paths
    /// the common float (scalar) case to avoid the overhead of the general-purpose
    /// <see cref="StatisticsPredicateEvaluator.CompareValues"/> dispatch.
    /// </summary>
    private static readonly Comparison<ValueIndexEntry> EntryKeyComparison =
        (a, b) => CompareDataValues(a.Key, b.Key);

    /// <summary>
    /// Reusable comparer for <see cref="DataValue"/> used by priority queue merges.
    /// </summary>
    private static readonly Comparer<DataValue> ValueKeyComparer =
        Comparer<DataValue>.Create(CompareDataValues);

    /// <summary>
    /// Compares two <see cref="DataValue"/> instances, fast-pathing the scalar (float)
    /// case that dominates numeric index builds.
    /// </summary>
    private static int CompareDataValues(DataValue left, DataValue right)
    {
        return DataValueComparer.Compare(left, right);
    }

    private readonly string _spillDirectory;
    private readonly Dictionary<string, List<ValueIndexEntry>> _currentChunkEntries;
    private readonly Dictionary<string, BinaryWriter> _spillWriters;
    private readonly Dictionary<string, int> _spillRunCounts;
    private readonly Dictionary<string, long> _spillTotalEntries;
    private readonly Dictionary<string, List<SpillRunMetadata>> _spillRunMetadata;
    private readonly HashSet<string> _droppedColumns;
    private bool _preparedForReading;
    private bool _disposed;

    /// <summary>
    /// Creates a new spill writer that will use temporary files for sorted index accumulation.
    /// </summary>
    internal SortedIndexSpillWriter()
    {
        _spillDirectory = Path.Combine(Path.GetTempPath(), $"datum-idx-{Guid.NewGuid():N}");
        _currentChunkEntries = new(StringComparer.OrdinalIgnoreCase);
        _spillWriters = new(StringComparer.OrdinalIgnoreCase);
        _spillRunCounts = new(StringComparer.OrdinalIgnoreCase);
        _spillTotalEntries = new(StringComparer.OrdinalIgnoreCase);
        _spillRunMetadata = new(StringComparer.OrdinalIgnoreCase);
        _droppedColumns = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Initializes per-column tracking for the specified index columns.
    /// </summary>
    /// <param name="indexColumns">Column names that should have sorted indexes built.</param>
    /// <param name="chunkCapacity">
    /// Expected number of entries per chunk. When positive, used to pre-size the
    /// per-column entry lists and avoid geometric-doubling allocations during the
    /// first chunk.
    /// </param>
    internal void Initialize(IReadOnlySet<string> indexColumns, int chunkCapacity = 0)
    {
        foreach (string column in indexColumns)
        {
            _currentChunkEntries.TryAdd(column, chunkCapacity > 0
                ? new List<ValueIndexEntry>(chunkCapacity)
                : new List<ValueIndexEntry>());
            _spillRunCounts.TryAdd(column, 0);
            _spillTotalEntries.TryAdd(column, 0);
            _spillRunMetadata.TryAdd(column, new List<SpillRunMetadata>());
        }
    }

    /// <summary>
    /// Adds a single index entry for the current chunk. Entries are held in memory
    /// until <see cref="FlushChunk"/> is called.
    /// </summary>
    /// <param name="columnName">The column this entry belongs to.</param>
    /// <param name="entry">The value/chunk/offset entry to record.</param>
    /// <returns><c>true</c> if the entry was accepted; <c>false</c> if the column
    /// has been dropped (e.g. string too long).</returns>
    internal bool AddEntry(string columnName, ValueIndexEntry entry)
    {
        if (_droppedColumns.Contains(columnName))
        {
            return false;
        }

        if (!_currentChunkEntries.TryGetValue(columnName, out List<ValueIndexEntry>? entries))
        {
            return false;
        }

        entries.Add(entry);
        return true;
    }

    /// <summary>
    /// Checks whether an auto-indexed string column should be dropped because the
    /// observed value exceeds <see cref="AutoIndexMaxStringLength"/>. Call this for
    /// string values before or after <see cref="AddEntry"/>; if it returns <c>true</c>,
    /// the column is marked as dropped and all accumulated entries are discarded.
    /// </summary>
    /// <param name="columnName">The column to check.</param>
    /// <param name="value">The string value observed.</param>
    /// <returns><c>true</c> if the column was dropped; <c>false</c> if it remains eligible.</returns>
    internal bool CheckAndDropLongString(string columnName, DataValue value)
    {
        if (_droppedColumns.Contains(columnName))
        {
            return true;
        }

        if (value.Kind != DataKind.String || value.IsNull)
        {
            return false;
        }

        if (value.AsString().Length > AutoIndexMaxStringLength)
        {
            DropColumn(columnName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drops a column from indexing, discarding any accumulated entries.
    /// </summary>
    internal void DropColumn(string columnName)
    {
        _droppedColumns.Add(columnName);
        _currentChunkEntries.Remove(columnName);
        _spillRunCounts.Remove(columnName);
        _spillTotalEntries.Remove(columnName);
        _spillRunMetadata.Remove(columnName);

        if (_spillWriters.TryGetValue(columnName, out BinaryWriter? writer))
        {
            writer.Dispose();
            _spillWriters.Remove(columnName);

            string spillPath = GetSpillPath(columnName);
            if (File.Exists(spillPath))
            {
                File.Delete(spillPath);
            }
        }
    }

    /// <summary>
    /// Sorts the current chunk's entries per column and appends them as a sorted run
    /// to the corresponding temp file on disk. Clears the in-memory buffers.
    /// </summary>
    internal void FlushChunk()
    {
        foreach (KeyValuePair<string, List<ValueIndexEntry>> pair in _currentChunkEntries)
        {
            List<ValueIndexEntry> entries = pair.Value;

            if (entries.Count == 0)
            {
                continue;
            }

            string columnName = pair.Key;

            // Sort the chunk's entries by key.
            entries.Sort(EntryKeyComparison);

            // Append to the spill file.
            BinaryWriter writer = GetOrCreateSpillWriter(columnName);
            writer.Write(entries.Count);

            // Record the data-start position (after the count prefix) so the
            // merge phase can jump directly to each run without scanning.
            long dataStartPosition = writer.BaseStream.Position;

            foreach (ValueIndexEntry entry in entries)
            {
                DataValueWriter.WriteDataValue(writer, entry.Key);
                writer.Write(entry.ChunkIndex);
                writer.Write(entry.RowOffsetInChunk);
            }

            _spillRunCounts[columnName]++;
            _spillTotalEntries[columnName] += entries.Count;
            _spillRunMetadata[columnName].Add(new SpillRunMetadata(dataStartPosition, entries.Count));

            entries.Clear();
        }
    }

    /// <summary>
    /// Returns whether the specified column is being tracked for indexing
    /// (i.e. it was initialized and has not been dropped).
    /// </summary>
    internal bool IsIndexed(string columnName)
    {
        return _currentChunkEntries.ContainsKey(columnName) && !_droppedColumns.Contains(columnName);
    }

    /// <summary>
    /// Returns the internal entry list for a column if it is currently indexed and not
    /// dropped, or <c>null</c> otherwise. The returned list reference is stable across
    /// chunk boundaries (<see cref="FlushChunk"/> clears the list but does not replace it),
    /// so callers may cache it by ordinal for the lifetime of the index build.
    /// </summary>
    /// <param name="columnName">The column to resolve.</param>
    /// <returns>The entry accumulation list, or <c>null</c> if the column is not indexed.</returns>
    internal List<ValueIndexEntry>? GetEntryListOrNull(string columnName)
    {
        if (_droppedColumns.Contains(columnName))
        {
            return null;
        }

        _currentChunkEntries.TryGetValue(columnName, out List<ValueIndexEntry>? entries);
        return entries;
    }

    /// <summary>
    /// Returns whether any sorted index entries have been accumulated.
    /// Valid after <see cref="PrepareForReading"/> (or before, though unflushed entries
    /// in the current chunk are not counted until they are flushed).
    /// </summary>
    internal bool HasSortedIndexes
    {
        get
        {
            foreach (long total in _spillTotalEntries.Values)
            {
                if (total > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Returns the total number of entries accumulated for the specified column across all
    /// flushed chunks. Unflushed entries in the current chunk are not included.
    /// </summary>
    /// <param name="columnName">The column to query.</param>
    /// <returns>The total entry count, or 0 if the column is not tracked.</returns>
    internal long GetTotalEntryCount(string columnName)
    {
        return _spillTotalEntries.TryGetValue(columnName, out long count) ? count : 0;
    }

    /// <summary>
    /// Returns the names of all indexed columns that have at least one spilled entry.
    /// </summary>
    internal IEnumerable<string> IndexedColumnNames
    {
        get
        {
            foreach (KeyValuePair<string, long> pair in _spillTotalEntries)
            {
                if (pair.Value > 0 && _spillRunCounts.TryGetValue(pair.Key, out int runCount) && runCount > 0)
                {
                    yield return pair.Key;
                }
            }
        }
    }

    /// <summary>
    /// K-way merges pre-sorted runs for a column into a single materialized array.
    /// Callers that require multiple passes over the data (e.g. the v5 format writes keys
    /// before locators) should use this instead of <see cref="EnumerateMergedEntries(string)"/>.
    /// </summary>
    /// <param name="columnName">The column to merge.</param>
    /// <returns>A sorted array of all entries for the column, or an empty array if the column
    /// has no spilled data.</returns>
    internal ValueIndexEntry[] GetMergedEntries(string columnName)
    {
        PrepareForReading();

        if (!_spillRunCounts.TryGetValue(columnName, out int runCount) || runCount == 0)
        {
            return [];
        }

        long totalEntries = _spillTotalEntries[columnName];
        return MergeSortedRuns(columnName, runCount, totalEntries);
    }

    /// <summary>
    /// K-way merges pre-sorted runs for a column and yields entries in sorted order
    /// without materializing the full array. Suitable for single-pass consumers such as
    /// <see cref="BPlusTreeBulkLoader"/>.
    /// </summary>
    /// <param name="columnName">The column to merge.</param>
    /// <returns>Entries in ascending key order, or an empty sequence if the column has no
    /// spilled data.</returns>
    internal IEnumerable<ValueIndexEntry> EnumerateMergedEntries(string columnName)
    {
        PrepareForReading();

        if (!_spillRunCounts.TryGetValue(columnName, out int runCount) || runCount == 0)
        {
            yield break;
        }

        foreach (ValueIndexEntry entry in EnumerateMergedEntries(columnName, runCount))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Resolves the <see cref="DataKind"/> for the specified column from the schema.
    /// Falls back to <see cref="DataKind.String"/> if the column is not found.
    /// </summary>
    internal static DataKind ResolveDataKind(string columnName, Schema schema)
    {
        foreach (ColumnInfo column in schema.Columns)
        {
            if (string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return column.Kind;
            }
        }

        return DataKind.String;
    }

    /// <summary>
    /// Streams B+Tree index sections to the output writer by performing a k-way merge
    /// of spilled sorted runs per column and feeding them to <see cref="BPlusTreeBulkLoader"/>.
    /// Each column produces one B+Tree with section header + compressed pages.
    /// </summary>
    /// <param name="output">The seekable binary writer to receive B+Tree pages.</param>
    /// <param name="schema">The table schema, used to resolve <see cref="DataKind"/> per column.</param>
    /// <param name="columnFilter">
    /// When not <c>null</c>, only columns in this set are written.
    /// When <c>null</c>, all indexed columns with entries are written.
    /// </param>
    internal void WriteBPlusTreeIndexesToStream(
        BufferedIndexWriter output,
        Schema schema,
        IReadOnlySet<string>? columnFilter = null)
    {
        PrepareForReading();

        // Count how many columns will be written.
        int columnCount = 0;

        foreach (KeyValuePair<string, int> pair in _spillRunCounts)
        {
            if (pair.Value > 0
                && (columnFilter is null || columnFilter.Contains(pair.Key)))
            {
                columnCount++;
            }
        }

        output.Write(columnCount);

        foreach (KeyValuePair<string, int> pair in _spillRunCounts)
        {
            string columnName = pair.Key;
            int runCount = pair.Value;

            if (runCount == 0)
            {
                continue;
            }

            if (columnFilter is not null && !columnFilter.Contains(columnName))
            {
                continue;
            }

            DataKind keyKind = ResolveDataKind(columnName, schema);

            BPlusTreeBulkLoader.Build(
                EnumerateMergedEntries(columnName, runCount),
                columnName,
                keyKind,
                output,
                _spillTotalEntries[columnName]);
        }
    }

    /// <summary>
    /// Performs a k-way merge of spilled sorted runs for a column and yields entries
    /// in sorted order without materializing the full array.
    /// </summary>
    private IEnumerable<ValueIndexEntry> EnumerateMergedEntries(string columnName, int runCount)
    {
        string spillPath = GetSpillPath(columnName);
        using FileStream fileStream = File.OpenRead(spillPath);
        using BinaryReader reader = new(fileStream);

        if (runCount == 1)
        {
            int count = reader.ReadInt32();

            for (int index = 0; index < count; index++)
            {
                yield return ReadEntry(reader);
            }

            yield break;
        }

        List<SpillRunMetadata> runMetadata = _spillRunMetadata[columnName];
        BufferedRunReader[] runs = new BufferedRunReader[runCount];

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            runs[runIndex] = new BufferedRunReader(
                reader, fileStream,
                runMetadata[runIndex].DataStartPosition,
                runMetadata[runIndex].EntryCount);
        }

        PriorityQueue<int, DataValue> queue = new(ValueKeyComparer);

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            if (runs[runIndex].TryAdvance())
            {
                queue.Enqueue(runIndex, runs[runIndex].Current.Key);
            }
        }

        while (queue.Count > 0)
        {
            int runIndex = queue.Dequeue();
            yield return runs[runIndex].Current;

            if (runs[runIndex].TryAdvance())
            {
                queue.Enqueue(runIndex, runs[runIndex].Current.Key);
            }
        }
    }

    /// <summary>
    /// Flushes any remaining unflushed entries and closes spill file writers,
    /// making the spill files ready for reading. Idempotent.
    /// </summary>
    internal void PrepareForReading()
    {
        if (_preparedForReading)
        {
            return;
        }

        _preparedForReading = true;

        FlushChunk();

        foreach (BinaryWriter writer in _spillWriters.Values)
        {
            writer.Flush();
            writer.Dispose();
        }

        _spillWriters.Clear();
    }

    /// <summary>
    /// Streams the sorted indexes section directly to the output writer using a k-way merge
    /// of the spilled sorted runs. Unlike <see cref="SortedIndex"/>, this method
    /// never allocates the full <see cref="ValueIndexEntry"/> array — entries are merged and
    /// written one at a time, keeping memory consumption at O(number of chunks).
    /// </summary>
    /// <param name="output">The binary writer to receive the sorted indexes in
    /// <see cref="DataValueWriter"/> format.</param>
    /// <param name="excludeColumns">
    /// Columns to skip (e.g. columns assigned to B+Tree). When <c>null</c>, all columns are written.
    /// </param>
    internal void WriteSortedIndexesToStream(BufferedIndexWriter output, IReadOnlySet<string>? excludeColumns = null)
    {
        PrepareForReading();

        int columnCount = 0;

        foreach (KeyValuePair<string, int> pair in _spillRunCounts)
        {
            if (pair.Value > 0
                && (excludeColumns is null || !excludeColumns.Contains(pair.Key)))
            {
                columnCount++;
            }
        }

        output.Write(columnCount);

        foreach (KeyValuePair<string, int> pair in _spillRunCounts)
        {
            string columnName = pair.Key;
            int runCount = pair.Value;

            if (runCount == 0)
            {
                continue;
            }

            if (excludeColumns is not null && excludeColumns.Contains(columnName))
            {
                continue;
            }

            long totalEntries = _spillTotalEntries[columnName];
            output.Write(columnName);
            output.Write(checked((int)totalEntries));

            StreamMergeSortedRuns(output, columnName, runCount);
        }
    }

    /// <summary>
    /// Streams the sorted indexes section with per-column Zstd compression (version 3 format).
    /// Each column's entries are merge-streamed into a memory buffer, compressed, then written
    /// as an envelope: column name, entry count, uncompressed length, compressed length,
    /// compressed payload.
    /// </summary>
    /// <param name="output">The binary writer to receive the compressed sorted indexes.</param>
    /// <param name="excludeColumns">
    /// Columns to skip (e.g. columns assigned to B+Tree). When <c>null</c>, all columns are written.
    /// </param>
    internal void WriteCompressedSortedIndexesToStream(BufferedIndexWriter output, IReadOnlySet<string>? excludeColumns = null)
    {
        PrepareForReading();

        int columnCount = 0;

        foreach (KeyValuePair<string, int> pair in _spillRunCounts)
        {
            if (pair.Value > 0
                && (excludeColumns is null || !excludeColumns.Contains(pair.Key)))
            {
                columnCount++;
            }
        }

        output.Write(columnCount);

        foreach (KeyValuePair<string, int> pair in _spillRunCounts)
        {
            string columnName = pair.Key;
            int runCount = pair.Value;

            if (runCount == 0)
            {
                continue;
            }

            if (excludeColumns is not null && excludeColumns.Contains(columnName))
            {
                continue;
            }

            long totalEntries = _spillTotalEntries[columnName];
            output.Write(columnName);
            output.Write(checked((int)totalEntries));

            // Stream the k-way merge through Zstd compression so only the
            // compressed output (typically 5-20% of original) is held in memory.
            // The previous approach materialized the entire uncompressed buffer
            // (~500 MB+ for large columns), causing a ~1.5 GB peak allocation.
            // Pre-size to ~40% of estimated uncompressed size (13 bytes/entry for
            // typical Int32 keys) to avoid geometric MemoryStream doublings.
            // 40% accommodates columns with higher entropy (strings, dates) that
            // compress less aggressively than integer keys.
            int estimatedCompressedCapacity = Math.Max(256, (int)(totalEntries * 13 * 2 / 5));
            using MemoryStream compressedBuffer = new(estimatedCompressedCapacity);
            int uncompressedLength;

            using (CompressionStream zstdStream = new(
                compressedBuffer,
                DatumFile.DatumFileConstants.DefaultZstdCompressionLevel,
                leaveOpen: true))
            {
                ByteCountingStream counting = new(zstdStream);
                using (BufferedIndexWriter zstdWriter = new(counting))
                {
                    StreamMergeSortedRuns(zstdWriter, columnName, runCount);
                }

                uncompressedLength = checked((int)counting.BytesWritten);
            }

            output.Write(uncompressedLength);

            if (compressedBuffer.TryGetBuffer(out ArraySegment<byte> compressedSegment))
            {
                output.Write(compressedSegment.Count);
                output.Write(compressedSegment.AsSpan());
            }
            else
            {
                byte[] compressed = compressedBuffer.ToArray();
                output.Write(compressed.Length);
                output.Write(compressed);
            }
        }
    }

    /// <summary>
    /// K-way merges pre-sorted runs from a column's spill file and writes each entry
    /// directly to the output writer in sorted order, without allocating the full result array.
    /// </summary>
    private void StreamMergeSortedRuns(BufferedIndexWriter output, string columnName, int runCount)
    {
        string spillPath = GetSpillPath(columnName);
        using FileStream fileStream = File.OpenRead(spillPath);
        using BinaryReader reader = new(fileStream);

        if (runCount == 1)
        {
            StreamSingleRun(output, reader);
            return;
        }

        List<SpillRunMetadata> runMetadata = _spillRunMetadata[columnName];
        BufferedRunReader[] runs = new BufferedRunReader[runCount];

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            runs[runIndex] = new BufferedRunReader(
                reader, fileStream,
                runMetadata[runIndex].DataStartPosition,
                runMetadata[runIndex].EntryCount);
        }

        PriorityQueue<int, DataValue> queue = new(ValueKeyComparer);

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            if (runs[runIndex].TryAdvance())
            {
                queue.Enqueue(runIndex, runs[runIndex].Current.Key);
            }
        }

        while (queue.Count > 0)
        {
            int runIndex = queue.Dequeue();
            ValueIndexEntry entry = runs[runIndex].Current;

            DataValueWriter.WriteDataValue(output, entry.Key);
            output.Write(entry.ChunkIndex);
            output.Write(entry.RowOffsetInChunk);

            if (runs[runIndex].TryAdvance())
            {
                queue.Enqueue(runIndex, runs[runIndex].Current.Key);
            }
        }
    }

    /// <summary>
    /// Reads all entries from a single run and writes them directly to the output.
    /// </summary>
    private static void StreamSingleRun(BufferedIndexWriter output, BinaryReader reader)
    {
        int count = reader.ReadInt32();

        for (int index = 0; index < count; index++)
        {
            ValueIndexEntry entry = ReadEntry(reader);
            DataValueWriter.WriteDataValue(output, entry.Key);
            output.Write(entry.ChunkIndex);
            output.Write(entry.RowOffsetInChunk);
        }
    }

    /// <summary>
    /// K-way merges pre-sorted runs from a column's spill file into a single sorted array.
    /// </summary>
    private ValueIndexEntry[] MergeSortedRuns(string columnName, int runCount, long totalEntries)
    {
        string spillPath = GetSpillPath(columnName);
        using FileStream fileStream = File.OpenRead(spillPath);
        using BinaryReader reader = new(fileStream);

        // For a single run, read directly without merge overhead.
        if (runCount == 1)
        {
            return ReadSingleRun(reader);
        }

        // Use pre-recorded run metadata to avoid scanning the spill file.
        List<SpillRunMetadata> runMetadata = _spillRunMetadata[columnName];
        BufferedRunReader[] runs = new BufferedRunReader[runCount];

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            runs[runIndex] = new BufferedRunReader(
                reader, fileStream,
                runMetadata[runIndex].DataStartPosition,
                runMetadata[runIndex].EntryCount);
        }

        // K-way merge using a priority queue.
        ValueIndexEntry[] result = new ValueIndexEntry[totalEntries];
        PriorityQueue<int, DataValue> queue = new(ValueKeyComparer);

        // Seed the queue with the first entry from each run.
        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            if (runs[runIndex].TryAdvance())
            {
                queue.Enqueue(runIndex, runs[runIndex].Current.Key);
            }
        }

        long outputIndex = 0;

        while (queue.Count > 0)
        {
            int runIndex = queue.Dequeue();
            result[outputIndex++] = runs[runIndex].Current;

            if (runs[runIndex].TryAdvance())
            {
                queue.Enqueue(runIndex, runs[runIndex].Current.Key);
            }
        }

        return result;
    }

    private static ValueIndexEntry[] ReadSingleRun(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        ValueIndexEntry[] entries = new ValueIndexEntry[count];

        for (int index = 0; index < count; index++)
        {
            entries[index] = ReadEntry(reader);
        }

        return entries;
    }

    private static ValueIndexEntry ReadEntry(BinaryReader reader)
    {
        DataValue key = DataValueReader.ReadDataValue(reader);
        int chunkIndex = reader.ReadInt32();
        long rowOffset = reader.ReadInt64();
        return new ValueIndexEntry(key, chunkIndex, rowOffset);
    }

    private BinaryWriter GetOrCreateSpillWriter(string columnName)
    {
        if (_spillWriters.TryGetValue(columnName, out BinaryWriter? existing))
        {
            return existing;
        }

        Directory.CreateDirectory(_spillDirectory);
        string spillPath = GetSpillPath(columnName);
        FileStream fileStream = new(spillPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        BinaryWriter writer = new(fileStream);
        _spillWriters[columnName] = writer;
        return writer;
    }

    private string GetSpillPath(string columnName)
    {
        // Sanitize column name for use in file path by replacing invalid characters.
        string safeName = string.Join("_", columnName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_spillDirectory, $"idx_{safeName}.spill");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (BinaryWriter writer in _spillWriters.Values)
        {
            writer.Dispose();
        }

        _spillWriters.Clear();

        if (Directory.Exists(_spillDirectory))
        {
            try
            {
                Directory.Delete(_spillDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; temp directory may be locked.
            }
        }
    }

    /// <summary>
    /// Records the file offset and entry count of a sorted run, captured during
    /// <see cref="FlushChunk"/> so the merge phase can seek directly to each run
    /// without scanning through the spill file.
    /// </summary>
    private readonly record struct SpillRunMetadata(long DataStartPosition, int EntryCount);

    /// <summary>
    /// Reads entries from a sorted run in blocks to amortize seek cost. Instead of
    /// seeking and reading one entry per priority-queue pop (O(N) seeks for N entries),
    /// this reader fills an internal buffer of up to <see cref="EntriesPerBlock"/>
    /// entries per seek, reducing total seeks by that factor.
    /// </summary>
    private sealed class BufferedRunReader
    {
        /// <summary>
        /// Number of entries to read per buffer fill. Balances memory (3,200 runs ×
        /// 256 entries × ~28 bytes ≈ 22 MB per column) against seek reduction.
        /// </summary>
        private const int EntriesPerBlock = 256;

        private readonly BinaryReader _reader;
        private readonly FileStream _fileStream;
        private readonly ValueIndexEntry[] _buffer;
        private int _bufferCount;
        private int _bufferIndex;
        private long _nextReadPosition;
        private int _remainingInRun;

        /// <summary>
        /// The most recently advanced entry. Valid after a successful <see cref="TryAdvance"/>.
        /// </summary>
        internal ValueIndexEntry Current;

        internal BufferedRunReader(
            BinaryReader reader,
            FileStream fileStream,
            long dataStartPosition,
            int entryCount)
        {
            _reader = reader;
            _fileStream = fileStream;
            _buffer = new ValueIndexEntry[EntriesPerBlock];
            _nextReadPosition = dataStartPosition;
            _remainingInRun = entryCount;
            _bufferCount = 0;
            _bufferIndex = 0;
        }

        /// <summary>
        /// Advances to the next entry, refilling the buffer from disk when exhausted.
        /// </summary>
        /// <returns><c>true</c> if an entry is available in <see cref="Current"/>;
        /// <c>false</c> if the run is fully consumed.</returns>
        internal bool TryAdvance()
        {
            if (_bufferIndex < _bufferCount)
            {
                Current = _buffer[_bufferIndex++];
                return true;
            }

            if (_remainingInRun <= 0)
            {
                return false;
            }

            FillBuffer();
            Current = _buffer[_bufferIndex++];
            return true;
        }

        private void FillBuffer()
        {
            _fileStream.Position = _nextReadPosition;
            int toRead = Math.Min(EntriesPerBlock, _remainingInRun);

            for (int index = 0; index < toRead; index++)
            {
                _buffer[index] = ReadEntry(_reader);
            }

            _nextReadPosition = _fileStream.Position;
            _remainingInRun -= toRead;
            _bufferCount = toRead;
            _bufferIndex = 0;
        }
    }

    /// <summary>
    /// Write-only stream wrapper that counts bytes written while forwarding all
    /// data to an inner stream. Used to measure uncompressed size while streaming
    /// through a compression pipeline without materializing all data in memory.
    /// </summary>
    private sealed class ByteCountingStream : Stream
    {
        private readonly Stream _inner;

        /// <summary>Total number of bytes written through this stream.</summary>
        public long BytesWritten { get; private set; }

        public ByteCountingStream(Stream inner) => _inner = inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
