using System.Diagnostics;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

namespace DatumIngest.Ingestion;

/// <summary>
/// Builds a <c>.datum-index</c> sidecar for a single <c>.datum</c> file. Parallel to
/// <see cref="Ingester"/>: one file in, one file out, no catalog or descriptor plumbing
/// required.
/// </summary>
/// <remarks>
/// PR13d: per-column B+Tree acceleration moved to companion
/// <c>.datum-bptree-{col}</c> page-COW files. The unified
/// <c>.datum-index</c> sidecar carries fingerprint, schema, chunk
/// directory + zone maps, bloom filters, and bitmap indexes. The
/// per-column tree files are written separately by 2b — for the
/// duration of 2a only the unified sidecar is produced and
/// non-bitmap columns lose acceleration.
/// </remarks>
public sealed class Indexer(Pool pool)
{
    /// <summary>
    /// Indexes the given <c>.datum</c> file with default options
    /// (<see cref="IndexOptions.Default"/>).
    /// </summary>
    public Task<IndexResult> IndexAsync(
        DatumFileDescriptor source,
        OutputDescriptor destination,
        CancellationToken cancellationToken = default)
        => IndexAsync(source, destination, IndexOptions.Default, cancellationToken);

    /// <summary>
    /// Indexes the given <c>.datum</c> file with caller-specified memory/throughput options.
    /// </summary>
    public async Task<IndexResult> IndexAsync(
        DatumFileDescriptor source,
        OutputDescriptor destination,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        TableDescriptor descriptor = new(
            Name: PathDetector.DeriveTableName(source.FilePath),
            FilePath: source.FilePath);

        using DatumFileTableProviderV2 provider = new(descriptor, pool);
        return await IndexAsync(provider, source.FilePath, destination, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Indexes a <c>.datum</c> file via an already-open provider.
    /// </summary>
    public async Task<IndexResult> IndexAsync(
        ITableProvider provider,
        string datumPath,
        OutputDescriptor destination,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        SourceFingerprint fingerprint = await ComputeFingerprintAsync(datumPath, cancellationToken)
            .ConfigureAwait(false);

        SourceIndexBuilder builder = ConfigureBuilder(options);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        string tableName = PathDetector.DeriveTableName(datumPath);

        SourceIndex index;
        long bytesWritten;
        long rowCount = 0;

        try
        {
            await foreach (RowBatch batch in provider
                .ScanAsync(requiredColumns: null, filterHint: null, targetArena: null, cancellationToken)
                .ConfigureAwait(false))
            {
                incremental.AddBatch(batch);

                rowCount += batch.Count;

                pool.ReturnRowBatch(batch);
            }

            index = incremental.Finalize();

            SourceIndexSet indexSet = SourceIndexSet.Create(tableName, index);

            await using Stream outputStream = await destination.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            UnifiedIndexWriter.Write(indexSet, outputStream);
            bytesWritten = outputStream.Length;
        }
        finally
        {
            incremental.Dispose();
        }

        sw.Stop();

        return new IndexResult(
            OutputPath: destination.FilePath,
            RowCount: rowCount,
            ChunkCount: index.Chunks.Count,
            BytesWritten: bytesWritten,
            Schema: index.Schema.Schema,
            Fingerprint: fingerprint,
            IndexedColumns: CollectIndexedColumnNames(index),
            BloomColumns: CollectBloomColumnNames(index),
            SortedColumns: Array.Empty<string>(),
            BitmapColumns: CollectBitmapColumnNames(index),
            DeferredReindexColumns: Array.Empty<string>(),
            Elapsed: sw.Elapsed);
    }

    /// <summary>
    /// PR13b chunk-splice extend path. Scans only rows past
    /// <c>existing.Schema.TotalRowCount</c>, builds an index over the new
    /// rows, merges it with <paramref name="existing"/> via
    /// <see cref="SourceIndex.Merge"/>, and writes the merged result.
    /// </summary>
    public async Task<IndexResult> ExtendAsync(
        ITableProvider provider,
        string datumPath,
        OutputDescriptor destination,
        SourceIndex existing,
        IndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);

        Stopwatch sw = Stopwatch.StartNew();

        SourceFingerprint fingerprint = await ComputeFingerprintAsync(datumPath, cancellationToken)
            .ConfigureAwait(false);

        SourceIndexBuilder builder = ConfigureBuilder(options);
        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);

        string tableName = PathDetector.DeriveTableName(datumPath);

        long existingRowCount = existing.Schema.TotalRowCount;
        long rowsScanned = 0;
        long deltaRowCount = 0;

        SourceIndex deltaIndex;
        SourceIndex merged;
        long bytesWritten;

        try
        {
            await foreach (RowBatch batch in provider
                .ScanAsync(requiredColumns: null, filterHint: null, targetArena: null, cancellationToken)
                .ConfigureAwait(false))
            {
                if (rowsScanned + batch.Count <= existingRowCount)
                {
                    rowsScanned += batch.Count;
                    pool.ReturnRowBatch(batch);
                    continue;
                }

                int skip = (int)Math.Max(0, existingRowCount - rowsScanned);
                for (int r = skip; r < batch.Count; r++)
                {
                    incremental.AddRow(batch[r], batch.Arena);
                    deltaRowCount++;
                }
                rowsScanned += batch.Count;
                pool.ReturnRowBatch(batch);
            }

            deltaIndex = incremental.Finalize();
            merged = SourceIndex.Merge(existing, deltaIndex);

            SourceIndexSet indexSet = SourceIndexSet.Create(tableName, merged);

            await using Stream outputStream = await destination.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            UnifiedIndexWriter.Write(indexSet, outputStream);
            bytesWritten = outputStream.Length;
        }
        finally
        {
            incremental.Dispose();
        }

        sw.Stop();

        return new IndexResult(
            OutputPath: destination.FilePath,
            RowCount: rowsScanned,
            ChunkCount: merged.Chunks.Count,
            BytesWritten: bytesWritten,
            Schema: merged.Schema.Schema,
            Fingerprint: fingerprint,
            IndexedColumns: CollectIndexedColumnNames(merged),
            BloomColumns: CollectBloomColumnNames(merged),
            SortedColumns: Array.Empty<string>(),
            BitmapColumns: CollectBitmapColumnNames(merged),
            DeferredReindexColumns: Array.Empty<string>(),
            Elapsed: sw.Elapsed);
    }

    /// <summary>
    /// Always true after PR13d: the unified sidecar carries only chunk-aligned
    /// data (bloom + bitmap + zone maps), all of which are extensible by
    /// chunk splice. Per-column B+Tree acceleration lives outside the
    /// sidecar in companion files and is mutated in-place by the provider.
    /// </summary>
    public static bool CanExtend(SourceIndex existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return true;
    }

    private static async Task<SourceFingerprint> ComputeFingerprintAsync(
        string filePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
        return await SourceFingerprint.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static SourceIndexBuilder ConfigureBuilder(IndexOptions options)
    {
        return options.Columns switch
        {
            IndexColumnSelection.Auto => new SourceIndexBuilder(
                bloomAllColumns: true,
                chunkSize: options.ChunkSize,
                computeCardinality: options.ComputeCardinality),

            IndexColumnSelection.All => new SourceIndexBuilder(
                bloomAllColumns: true,
                chunkSize: options.ChunkSize,
                computeCardinality: options.ComputeCardinality),

            IndexColumnSelection.Explicit explicitSelection => new SourceIndexBuilder(
                chunkSize: options.ChunkSize,
                bloomColumns: ToColumnSet(explicitSelection.Columns),
                computeCardinality: options.ComputeCardinality),

            IndexColumnSelection.None => new SourceIndexBuilder(
                chunkSize: options.ChunkSize,
                computeCardinality: options.ComputeCardinality),

            _ => throw new InvalidOperationException(
                $"Unknown {nameof(IndexColumnSelection)}: {options.Columns.GetType().Name}"),
        };
    }

    private static HashSet<string> ToColumnSet(IReadOnlyList<string> columns)
        => new(columns, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> CollectIndexedColumnNames(SourceIndex index)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in CollectBloomColumnNames(index)) names.Add(name);
        foreach (string name in CollectBitmapColumnNames(index)) names.Add(name);
        return names.ToArray();
    }

    private static IReadOnlyList<string> CollectBloomColumnNames(SourceIndex index)
        => index.BloomFilters is { } filters ? filters.ColumnNames.ToArray() : Array.Empty<string>();

    private static IReadOnlyList<string> CollectBitmapColumnNames(SourceIndex index)
        => index.BitmapIndexes is { } bitmap ? bitmap.ColumnNames.ToArray() : Array.Empty<string>();
}
