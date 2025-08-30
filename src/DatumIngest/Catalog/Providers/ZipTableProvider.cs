using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads ZIP archives, yielding one row per entry with <c>file_name</c> (eager)
/// and <c>file_bytes</c> (lazy — defers decompression until accessed).
/// Implements <see cref="IKeyedTableProvider"/> for efficient random access by
/// <c>file_name</c>, enabling late materialization of expensive <c>file_bytes</c>.
/// </summary>
/// <remarks>
/// <para>
/// All file access uses <see cref="FileOptions.SequentialScan"/> with a 128 KB
/// buffer to maximize OS readahead.
/// </para>
/// <para>
/// <see cref="FetchByKeysAsync"/> uses a two-phase approach for parallelism.
/// Phase 1 scans the central directory in a single serial pass, which
/// identifies matching entries and warms the OS page cache. Phase 2 opens
/// additional <see cref="ZipArchive"/> instances — each reading the now-cached
/// central directory from RAM — and decompresses contiguous partitions of
/// entries in parallel across <see cref="Environment.ProcessorCount"/> threads.
/// </para>
/// </remarks>
public sealed class ZipTableProvider : ITableProvider, IKeyedTableProvider
{
    /// <summary>
    /// Buffer size for <see cref="FileStream"/> reads. 128 KB reduces syscall
    /// overhead compared to the 4 KB default when reading entries that are
    /// typically 100 KB–1 MB (e.g. JPEG images).
    /// </summary>
    private const int StreamBufferSize = 128 * 1024;

    /// <summary>
    /// Minimum number of keys required to justify spinning up parallel workers.
    /// Below this threshold, sequential decompression is used because the
    /// overhead of opening additional archives outweighs the benefit.
    /// </summary>
    private const int ParallelThreshold = 8;

    /// <summary>
    /// Number of rows to accumulate in each <see cref="RowBatch"/> before
    /// yielding to the consumer.
    /// </summary>
    private const int DefaultBatchSize = 1024;

    private static readonly Schema ZipSchema = new(new ColumnInfo[]
    {
        new("file_name", DataKind.String, nullable: false),
        new("file_bytes", DataKind.UInt8Array, nullable: false)
    });

    /// <inheritdoc />
    public Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ZipSchema);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool includeFileName = requiredColumns is null ||
            requiredColumns.Contains("file_name");
        bool includeFileBytes = requiredColumns is null ||
            requiredColumns.Contains("file_bytes");

        // Build projected column arrays
        List<string> columnNames = new();
        if (includeFileName)
        {
            columnNames.Add("file_name");
        }
        if (includeFileBytes)
        {
            columnNames.Add("file_bytes");
        }

        string[] names = columnNames.ToArray();

        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < names.Length; index++)
        {
            nameIndex[names[index]] = index;
        }

        // We must read the ZIP synchronously because ZipArchive is not thread-safe
        // and entries are only valid while the archive is open. We read all entries
        // eagerly but defer the byte content via lazy evaluation.
        using FileStream fileStream = new(
            descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Read);

        RowBatch? batch = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip directory entries
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                continue;
            }

            Row resultRow = GlobalBufferPool.RentRow(names.Length);
            resultRow.UpdateSchema(names, nameIndex);
            DataValue[] values = resultRow.RawValues;
            int valueIndex = 0;

            if (includeFileName)
            {
                values[valueIndex++] = DataValue.FromString(entry.FullName);
            }

            if (includeFileBytes)
            {
                // Read bytes eagerly since the ZipArchive must remain open
                // (entries are invalidated when the archive is disposed).
                // The query planner uses ColumnCost.Expensive to avoid reading
                // this column unnecessarily during join build phases.
                byte[] bytes = ReadEntryBytes(entry);
                values[valueIndex++] = DataValue.FromUInt8Array(bytes);
            }

            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(resultRow);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    /// <inheritdoc />
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(descriptor.FilePath))
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: null,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>
                {
                    ["file_bytes"] = ColumnCost.Expensive
                },
                KeyColumn: "file_name"));
        }

        long? estimatedRowCount = null;

        using (ZipArchive archive = ZipFile.OpenRead(descriptor.FilePath))
        {
            int entryCount = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!string.IsNullOrEmpty(entry.Name))
                {
                    entryCount++;
                }
            }

            estimatedRowCount = entryCount;
        }

        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: estimatedRowCount,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: new Dictionary<string, ColumnCost>
            {
                ["file_bytes"] = ColumnCost.Expensive
            },
            KeyColumn: "file_name"));
    }

    /// <summary>
    /// Reads all bytes from a ZIP entry into a byte array. Pre-sizes the
    /// destination buffer using <see cref="ZipArchiveEntry.Length"/> to
    /// avoid repeated array doubling during the copy.
    /// </summary>
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        long length = entry.Length;
        using Stream stream = entry.Open();

        if (length > 0)
        {
            byte[] buffer = new byte[length];
            int offset = 0;

            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            return buffer;
        }

        // Fallback for entries that don't report length.
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Phase 1 — Scan (serial):</b> Opens a single archive and scans
    /// <see cref="ZipArchive.Entries"/> in central-directory order to identify
    /// which entries match the requested keys. This read warms the OS page
    /// cache with the central directory bytes.
    /// </para>
    /// <para>
    /// <b>Phase 2 — Decompress (parallel):</b> When the number of matches
    /// exceeds <see cref="ParallelThreshold"/>, the matched entry names are
    /// split into contiguous partitions. Each worker opens its own archive
    /// instance — the central directory is loaded from the now-hot page cache
    /// in milliseconds — and decompresses its partition independently.
    /// Workers feed results into a <see cref="Channel{T}"/> for streaming
    /// consumption.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<RowBatch> FetchByKeysAsync(
        TableDescriptor descriptor,
        string keyColumn,
        IReadOnlySet<DataValue> keyValues,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!string.Equals(keyColumn, "file_name", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"ZIP provider only supports keyed access by 'file_name', not '{keyColumn}'.",
                nameof(keyColumn));
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool includeFileName = requiredColumns is null ||
            requiredColumns.Contains("file_name");
        bool includeFileBytes = requiredColumns is null ||
            requiredColumns.Contains("file_bytes");

        List<string> columnNames = new();
        if (includeFileName)
        {
            columnNames.Add("file_name");
        }
        if (includeFileBytes)
        {
            columnNames.Add("file_bytes");
        }

        string[] names = columnNames.ToArray();
        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < names.Length; index++)
        {
            nameIndex[names[index]] = index;
        }

        // Collect the string keys into a set for O(1) matching.
        HashSet<string> entryNameSet = new(StringComparer.Ordinal);
        foreach (DataValue keyValue in keyValues)
        {
            if (!keyValue.IsNull && keyValue.Kind == DataKind.String)
            {
                entryNameSet.Add(keyValue.AsString());
            }
        }

        if (entryNameSet.Count == 0)
        {
            yield break;
        }

        // ── Phase 1: Scan central directory (serial) ──────────────────────
        // This read warms the OS page cache so subsequent archive opens are
        // served from RAM rather than disk.
        List<string> matchedEntries;
        using (FileStream scanStream = new(
            descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan))
        using (ZipArchive scanArchive = new(scanStream, ZipArchiveMode.Read))
        {
            matchedEntries = new List<string>(entryNameSet.Count);
            foreach (ZipArchiveEntry entry in scanArchive.Entries)
            {
                if (entryNameSet.Contains(entry.FullName))
                {
                    matchedEntries.Add(entry.FullName);

                    if (matchedEntries.Count >= entryNameSet.Count)
                    {
                        break;
                    }
                }
            }
        }

        if (matchedEntries.Count == 0)
        {
            yield break;
        }

        // ── Phase 2: Decompress ───────────────────────────────────────────
        // For small result sets, decompress sequentially to avoid worker overhead.
        // For larger sets, partition contiguously and decompress in parallel.
        int workerCount = matchedEntries.Count < ParallelThreshold
            ? 1
            : Math.Min(Environment.ProcessorCount, matchedEntries.Count / 4);

        if (workerCount <= 1)
        {
            // Sequential: single archive pass.
            await foreach (RowBatch decompressedBatch in DecompressEntriesAsync(
                descriptor, names, nameIndex, matchedEntries,
                includeFileName, includeFileBytes, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return decompressedBatch;
            }

            yield break;
        }

        // Parallel: contiguous partitions, one archive per worker.
        List<List<string>> partitions = PartitionContiguous(matchedEntries, workerCount);
        Channel<Row> channel = Channel.CreateBounded<Row>(new BoundedChannelOptions(workerCount * 16)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        Task[] workers = new Task[partitions.Count];
        for (int workerIndex = 0; workerIndex < partitions.Count; workerIndex++)
        {
            List<string> partition = partitions[workerIndex];
            workers[workerIndex] = Task.Run(
                () => DecompressWorkerAsync(
                    descriptor, names, nameIndex, partition,
                    includeFileName, includeFileBytes,
                    channel.Writer, cancellationToken),
                cancellationToken);
        }

        // Complete the channel when all workers finish (or fault).
        _ = CompleteChannelWhenDoneAsync(workers, channel.Writer);

        RowBatch? batch = null;
        await foreach (Row row in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(row);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Sequential decompression path: opens a single archive and iterates
    /// the pre-ordered entry names, decompressing each match in turn.
    /// </summary>
    private static async IAsyncEnumerable<RowBatch> DecompressEntriesAsync(
        TableDescriptor descriptor,
        string[] names,
        Dictionary<string, int> nameIndex,
        List<string> entryNames,
        bool includeFileName,
        bool includeFileBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        using FileStream fileStream = new(
            descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Read);

        RowBatch? batch = null;

        foreach (string entryName in entryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ZipArchiveEntry? entry = archive.GetEntry(entryName);
            if (entry is null)
            {
                continue;
            }

            Row row = BuildRow(entry, names, nameIndex, includeFileName, includeFileBytes);
            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(row);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Parallel decompression worker: opens its own archive instance (central
    /// directory is expected to be hot in the OS page cache from Phase 1) and
    /// decompresses its contiguous partition, writing results to the shared channel.
    /// </summary>
    private static async Task DecompressWorkerAsync(
        TableDescriptor descriptor,
        string[] names,
        Dictionary<string, int> nameIndex,
        List<string> partition,
        bool includeFileName,
        bool includeFileBytes,
        ChannelWriter<Row> writer,
        CancellationToken cancellationToken)
    {
        using FileStream fileStream = new(
            descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Read);

        foreach (string entryName in partition)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ZipArchiveEntry? entry = archive.GetEntry(entryName);
            if (entry is null)
            {
                continue;
            }

            Row row = BuildRow(entry, names, nameIndex, includeFileName, includeFileBytes);
            await writer.WriteAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes the channel writer after all workers finish. If any worker
    /// faults, the exception propagates through the channel reader.
    /// </summary>
    private static async Task CompleteChannelWhenDoneAsync(
        Task[] workers,
        ChannelWriter<Row> writer)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception exception)
        {
            writer.Complete(exception);
        }
    }

    /// <summary>
    /// Builds a single <see cref="Row"/> from a ZIP entry, applying projection.
    /// </summary>
    private static Row BuildRow(
        ZipArchiveEntry entry,
        string[] names,
        Dictionary<string, int> nameIndex,
        bool includeFileName,
        bool includeFileBytes)
    {
        Row resultRow = GlobalBufferPool.RentRow(names.Length);
        resultRow.UpdateSchema(names, nameIndex);
        DataValue[] values = resultRow.RawValues;
        int valueIndex = 0;

        if (includeFileName)
        {
            values[valueIndex++] = DataValue.FromString(entry.FullName);
        }

        if (includeFileBytes)
        {
            byte[] bytes = ReadEntryBytes(entry);
            values[valueIndex++] = DataValue.FromUInt8Array(bytes);
        }

        return resultRow;
    }

    /// <summary>
    /// Splits an ordered list into <paramref name="partitionCount"/> contiguous
    /// sublists. Each sublist spans a contiguous range so workers read from
    /// non-overlapping regions of the archive file.
    /// </summary>
    private static List<List<string>> PartitionContiguous(
        List<string> orderedEntries,
        int partitionCount)
    {
        List<List<string>> partitions = new(partitionCount);
        int chunkSize = (orderedEntries.Count + partitionCount - 1) / partitionCount;

        for (int start = 0; start < orderedEntries.Count; start += chunkSize)
        {
            int count = Math.Min(chunkSize, orderedEntries.Count - start);
            partitions.Add(orderedEntries.GetRange(start, count));
        }

        return partitions;
    }
}
