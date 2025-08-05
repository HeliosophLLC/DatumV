using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Accumulates sorted index entries chunk-by-chunk, spilling each sorted run to a
/// temporary file on disk. At finalization, performs a k-way merge of all sorted runs
/// into a single <see cref="SortedValueIndexSet"/> with constant memory overhead
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
/// At finalization (<see cref="BuildSortedValueIndexSet"/>), each column's temp file is
/// read back as a sequence of pre-sorted runs and merged using a priority queue into a
/// single sorted array of <see cref="ValueIndexEntry"/>, producing the final
/// <see cref="SortedValueIndex"/>.
/// </para>
/// </remarks>
internal sealed class SortedIndexSpillWriter : IDisposable
{
    /// <summary>
    /// Maximum string length for auto-indexed string columns. Strings observed to exceed
    /// this length cause the column to be dropped from indexing.
    /// </summary>
    internal const int AutoIndexMaxStringLength = 16;

    private readonly string _spillDirectory;
    private readonly Dictionary<string, List<ValueIndexEntry>> _currentChunkEntries;
    private readonly Dictionary<string, BinaryWriter> _spillWriters;
    private readonly Dictionary<string, int> _spillRunCounts;
    private readonly Dictionary<string, long> _spillTotalEntries;
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
        _droppedColumns = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Initializes per-column tracking for the specified index columns.
    /// </summary>
    /// <param name="indexColumns">Column names that should have sorted indexes built.</param>
    internal void Initialize(IReadOnlySet<string> indexColumns)
    {
        foreach (string column in indexColumns)
        {
            _currentChunkEntries.TryAdd(column, new List<ValueIndexEntry>());
            _spillRunCounts.TryAdd(column, 0);
            _spillTotalEntries.TryAdd(column, 0);
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
    private void DropColumn(string columnName)
    {
        _droppedColumns.Add(columnName);
        _currentChunkEntries.Remove(columnName);
        _spillRunCounts.Remove(columnName);
        _spillTotalEntries.Remove(columnName);

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
            entries.Sort((a, b) => StatisticsPredicateEvaluator.CompareValues(a.Key, b.Key));

            // Append to the spill file.
            BinaryWriter writer = GetOrCreateSpillWriter(columnName);
            writer.Write(entries.Count);

            foreach (ValueIndexEntry entry in entries)
            {
                IndexWriter.WriteDataValue(writer, entry.Key);
                writer.Write(entry.ChunkIndex);
                writer.Write(entry.RowOffsetInChunk);
            }

            _spillRunCounts[columnName]++;
            _spillTotalEntries[columnName] += entries.Count;

            entries.Clear();
        }
    }

    /// <summary>
    /// Merges all spilled sorted runs into a <see cref="SortedValueIndexSet"/>.
    /// Also includes any remaining unflushed entries from the last partial chunk.
    /// </summary>
    /// <remarks>
    /// This method materializes the entire sorted index into memory. For large datasets,
    /// prefer <see cref="WriteSortedIndexesToStream"/> which streams the k-way merge
    /// directly to a <see cref="BinaryWriter"/> without allocating the full array.
    /// </remarks>
    /// <returns>The merged sorted value index set, or <c>null</c> if no columns were indexed.</returns>
    internal SortedValueIndexSet? BuildSortedValueIndexSet()
    {
        PrepareForReading();

        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, int> pair in _spillRunCounts)
        {
            string columnName = pair.Key;
            int runCount = pair.Value;

            if (runCount == 0)
            {
                continue;
            }

            long totalEntries = _spillTotalEntries[columnName];
            ValueIndexEntry[] merged = MergeSortedRuns(columnName, runCount, totalEntries);
            indexes[columnName] = new SortedValueIndex(merged);
        }

        if (indexes.Count == 0)
        {
            return null;
        }

        return new SortedValueIndexSet(indexes);
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
    /// of the spilled sorted runs. Unlike <see cref="BuildSortedValueIndexSet"/>, this method
    /// never allocates the full <see cref="ValueIndexEntry"/> array — entries are merged and
    /// written one at a time, keeping memory consumption at O(number of chunks).
    /// </summary>
    /// <param name="output">The binary writer to receive the sorted indexes in
    /// <see cref="IndexWriter"/> format.</param>
    internal void WriteSortedIndexesToStream(BinaryWriter output)
    {
        PrepareForReading();

        int columnCount = 0;

        foreach (int runCount in _spillRunCounts.Values)
        {
            if (runCount > 0)
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
    internal void WriteCompressedSortedIndexesToStream(BinaryWriter output)
    {
        PrepareForReading();

        int columnCount = 0;

        foreach (int runCount in _spillRunCounts.Values)
        {
            if (runCount > 0)
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

            long totalEntries = _spillTotalEntries[columnName];
            output.Write(columnName);
            output.Write(checked((int)totalEntries));

            // Merge-stream into a memory buffer, then compress.
            using MemoryStream buffer = new();
            using (BinaryWriter bufferWriter = new(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                StreamMergeSortedRuns(bufferWriter, columnName, runCount);
            }

            byte[] uncompressed = buffer.ToArray();
            byte[] compressed = DatumFile.Compression.DatumCompressor.Compress(
                uncompressed, DatumFile.DatumCompression.Zstd);

            output.Write(uncompressed.Length);
            output.Write(compressed.Length);
            output.Write(compressed);
        }
    }

    /// <summary>
    /// K-way merges pre-sorted runs from a column's spill file and writes each entry
    /// directly to the output writer in sorted order, without allocating the full result array.
    /// </summary>
    private void StreamMergeSortedRuns(BinaryWriter output, string columnName, int runCount)
    {
        string spillPath = GetSpillPath(columnName);
        using FileStream fileStream = File.OpenRead(spillPath);
        using BinaryReader reader = new(fileStream);

        if (runCount == 1)
        {
            StreamSingleRun(output, reader);
            return;
        }

        RunState[] runs = new RunState[runCount];

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            long runStart = fileStream.Position;
            int entryCount = reader.ReadInt32();
            long dataStart = fileStream.Position;

            for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                SkipEntry(reader);
            }

            runs[runIndex] = new RunState(runStart, dataStart, entryCount);
        }

        PriorityQueue<int, DataValue> queue = new(
            Comparer<DataValue>.Create((a, b) => StatisticsPredicateEvaluator.CompareValues(a, b)));

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            if (runs[runIndex].RemainingCount > 0)
            {
                fileStream.Position = runs[runIndex].DataPosition;
                ValueIndexEntry entry = ReadEntry(reader);
                runs[runIndex].Current = entry;
                runs[runIndex].DataPosition = fileStream.Position;
                runs[runIndex].RemainingCount--;
                queue.Enqueue(runIndex, entry.Key);
            }
        }

        while (queue.Count > 0)
        {
            int runIndex = queue.Dequeue();
            ValueIndexEntry entry = runs[runIndex].Current;

            IndexWriter.WriteDataValue(output, entry.Key);
            output.Write(entry.ChunkIndex);
            output.Write(entry.RowOffsetInChunk);

            if (runs[runIndex].RemainingCount > 0)
            {
                fileStream.Position = runs[runIndex].DataPosition;
                ValueIndexEntry nextEntry = ReadEntry(reader);
                runs[runIndex].Current = nextEntry;
                runs[runIndex].DataPosition = fileStream.Position;
                runs[runIndex].RemainingCount--;
                queue.Enqueue(runIndex, nextEntry.Key);
            }
        }
    }

    /// <summary>
    /// Reads all entries from a single run and writes them directly to the output.
    /// </summary>
    private static void StreamSingleRun(BinaryWriter output, BinaryReader reader)
    {
        int count = reader.ReadInt32();

        for (int index = 0; index < count; index++)
        {
            ValueIndexEntry entry = ReadEntry(reader);
            IndexWriter.WriteDataValue(output, entry.Key);
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

        // Load run metadata: each run's remaining count and current position.
        RunState[] runs = new RunState[runCount];

        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            long runStart = fileStream.Position;
            int entryCount = reader.ReadInt32();
            long dataStart = fileStream.Position;

            // Skip past this run's data to find the next run.
            for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                SkipEntry(reader);
            }

            runs[runIndex] = new RunState(runStart, dataStart, entryCount);
        }

        // K-way merge using a priority queue.
        ValueIndexEntry[] result = new ValueIndexEntry[totalEntries];
        PriorityQueue<int, DataValue> queue = new(
            Comparer<DataValue>.Create((a, b) => StatisticsPredicateEvaluator.CompareValues(a, b)));

        // Seed the queue with the first entry from each run.
        for (int runIndex = 0; runIndex < runCount; runIndex++)
        {
            if (runs[runIndex].RemainingCount > 0)
            {
                fileStream.Position = runs[runIndex].DataPosition;
                ValueIndexEntry entry = ReadEntry(reader);
                runs[runIndex].Current = entry;
                runs[runIndex].DataPosition = fileStream.Position;
                runs[runIndex].RemainingCount--;
                queue.Enqueue(runIndex, entry.Key);
            }
        }

        long outputIndex = 0;

        while (queue.Count > 0)
        {
            int runIndex = queue.Dequeue();
            result[outputIndex++] = runs[runIndex].Current;

            if (runs[runIndex].RemainingCount > 0)
            {
                fileStream.Position = runs[runIndex].DataPosition;
                ValueIndexEntry nextEntry = ReadEntry(reader);
                runs[runIndex].Current = nextEntry;
                runs[runIndex].DataPosition = fileStream.Position;
                runs[runIndex].RemainingCount--;
                queue.Enqueue(runIndex, nextEntry.Key);
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
        DataValue key = IndexReader.ReadDataValue(reader);
        int chunkIndex = reader.ReadInt32();
        long rowOffset = reader.ReadInt64();
        return new ValueIndexEntry(key, chunkIndex, rowOffset);
    }

    private static void SkipEntry(BinaryReader reader)
    {
        // Read and discard the DataValue + chunkIndex + rowOffset.
        IndexReader.ReadDataValue(reader);
        reader.ReadInt32();
        reader.ReadInt64();
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
    /// Tracks the state of a single sorted run during k-way merge.
    /// </summary>
    private sealed class RunState
    {
        internal long DataPosition;
        internal int RemainingCount;
        internal ValueIndexEntry Current;

        internal RunState(long runStart, long dataPosition, int entryCount)
        {
            _ = runStart;
            DataPosition = dataPosition;
            RemainingCount = entryCount;
        }
    }
}
