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
/// <para>
/// The indexer reads the source <c>.datum</c> file through <see cref="DatumFileTableProviderV2"/>
/// streaming one <see cref="RowBatch"/> at a time, accumulates per-chunk statistics and
/// optional acceleration structures (bloom filters, bitmap indexes, sorted or B+Tree column
/// indexes), and serialises the result via <see cref="UnifiedIndexWriter"/>. The output
/// overwrites any existing file at the destination path, matching the semantics of
/// <see cref="Ingester"/>.
/// </para>
/// <para>
/// Column selection policy is controlled by <see cref="IndexOptions.Columns"/>. The default
/// (<see cref="IndexColumnSelection.Auto"/>) builds bloom filters for every column and
/// sorted/B+Tree indexes for the columns the builder considers cheap and useful
/// (numerics, dates, booleans, UUIDs, short strings). Wide reference types are skipped.
/// </para>
/// <para>
/// Memory during build is bounded by the <see cref="IncrementalIndexBuilder"/>'s on-disk
/// spill writer for sorted/B+Tree data, and by the per-chunk accumulator working set for
/// everything else. Lower the peak working set by using <see cref="IndexOptions.MultiTenantServer"/>
/// or by reducing <see cref="IndexOptions.ChunkSize"/>.
/// </para>
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

        // v2 reader validates magic + version inside its constructor.
        // v2 files emit sidecar-bound DataValues for long strings / byte
        // arrays / images; the index builder skips those at the bloom
        // layer (see IncrementalIndexBuilder.AddRow).
        using DatumFileTableProviderV2 provider = new(descriptor, pool);
        return await IndexAsync(provider, source.FilePath, destination, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Indexes a <c>.datum</c> file via an already-open provider. Used by
    /// <c>REINDEX</c>, which holds the live provider's mutation lock and
    /// already has a reader open against the file — opening a second
    /// provider here would conflict with the live <c>.datum-pkindex</c>
    /// (whose backing tree opens with <see cref="FileShare.None"/>).
    /// </summary>
    /// <param name="provider">An open provider for the source file. The caller retains ownership.</param>
    /// <param name="datumPath">Path of the source <c>.datum</c> file (used for the fingerprint).</param>
    /// <param name="destination">Output target for the rebuilt <c>.datum-index</c> sidecar.</param>
    /// <param name="options">Memory / throughput options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
            UnifiedIndexWriter.Write(indexSet, outputStream, incremental.SpillWriter);
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
            SortedColumns: CollectSortedColumnNames(index),
            BitmapColumns: CollectBitmapColumnNames(index),
            DeferredReindexColumns: incremental.DeferredReindexColumns,
            Elapsed: sw.Elapsed);
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
                indexAllColumns: false,
                chunkSize: options.ChunkSize,
                autoIndexColumns: true,
                computeCardinality: options.ComputeCardinality),

            IndexColumnSelection.All => new SourceIndexBuilder(
                bloomAllColumns: true,
                indexAllColumns: true,
                chunkSize: options.ChunkSize,
                computeCardinality: options.ComputeCardinality),

            IndexColumnSelection.Explicit explicitSelection => new SourceIndexBuilder(
                chunkSize: options.ChunkSize,
                bloomColumns: ToColumnSet(explicitSelection.Columns),
                indexColumns: ToColumnSet(explicitSelection.Columns),
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
        foreach (string name in CollectSortedColumnNames(index)) names.Add(name);
        foreach (string name in CollectBitmapColumnNames(index)) names.Add(name);
        return names.ToArray();
    }

    private static IReadOnlyList<string> CollectBloomColumnNames(SourceIndex index)
        => index.BloomFilters is { } filters ? filters.ColumnNames.ToArray() : Array.Empty<string>();

    private static IReadOnlyList<string> CollectSortedColumnNames(SourceIndex index)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (index.MappedSortedIndexes is { } mapped)
        {
            foreach (string name in mapped.Keys) names.Add(name);
        }
        if (index.BPlusTreeIndexes is { } btree)
        {
            foreach (string name in btree.ColumnNames) names.Add(name);
        }
        return names.ToArray();
    }

    private static IReadOnlyList<string> CollectBitmapColumnNames(SourceIndex index)
        => index.BitmapIndexes is { } bitmap ? bitmap.ColumnNames.ToArray() : Array.Empty<string>();
}
