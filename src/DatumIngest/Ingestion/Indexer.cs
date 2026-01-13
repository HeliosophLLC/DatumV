using System.Diagnostics;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
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

        // PR13d: per-column B+Tree trees live in companion files. Wipe the
        // previous build's trees (if any) up front so a partial-failure mid-
        // build doesn't leave stale entries from a different column kind.
        DeleteExistingColumnTreeFiles(datumPath);

        Schema providerSchema = provider.GetSchema();
        IReadOnlySet<string> indexableColumns = SourceIndexBuilder.ResolveAutoIndexColumns(providerSchema);

        // Open one MutableBPlusTree per indexable column, keyed by column name.
        // The dictionary maps columnName → (tree, schemaOrdinal). schemaOrdinal
        // lets the per-row indexing path skip a name lookup once per row per
        // column.
        Dictionary<string, ColumnTreeBuild> columnTrees = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (ColumnInfo column in providerSchema.Columns)
            {
                if (!indexableColumns.Contains(column.Name))
                {
                    continue;
                }
                string treePath = DatumFileTableProviderV2.GetColumnIndexPath(datumPath, column.Name);
                MutableBPlusTree tree = MutableBPlusTree.Create(
                    treePath, column.Kind, allowDuplicates: true);
                columnTrees[column.Name] = new ColumnTreeBuild(tree, column.Kind);
            }

            SourceIndex index;
            long bytesWritten;
            long rowCount = 0;
            int currentChunkIndex = 0;
            int rowsInCurrentChunk = 0;
            int chunkSize = options.ChunkSize;

            try
            {
                await foreach (RowBatch batch in provider
                    .ScanAsync(requiredColumns: null, filterHint: null, targetArena: null, cancellationToken)
                    .ConfigureAwait(false))
                {
                    for (int r = 0; r < batch.Count; r++)
                    {
                        Row row = batch[r];
                        incremental.AddRow(row, batch.Arena);

                        // Mirror the per-column tree inserts. ChunkIndex /
                        // RowOffsetInChunk track the same chunk boundaries the
                        // incremental builder uses (chunkSize from options).
                        foreach (KeyValuePair<string, ColumnTreeBuild> kvp in columnTrees)
                        {
                            DataValue value = row[kvp.Key];
                            if (value.IsNull) continue;
                            // Sidecar-backed values can't currently be keyed
                            // through MutableBPlusTree (ValueIndexEntry holds
                            // a DataValue copy that goes stale once the
                            // owning RowBatch returns to the pool). Skip them
                            // — those columns degrade to scan.
                            if (value.IsInSidecar) continue;
                            kvp.Value.Tree.Insert(new ValueIndexEntry(value, currentChunkIndex, rowsInCurrentChunk));
                        }

                        rowsInCurrentChunk++;
                        rowCount++;

                        if (rowsInCurrentChunk >= chunkSize)
                        {
                            currentChunkIndex++;
                            rowsInCurrentChunk = 0;
                        }
                    }

                    pool.ReturnRowBatch(batch);
                }

                index = incremental.Finalize();

                // PR13e-A: drop per-column trees that are redundant against the
                // unified sidecar's bitmap, and empty trees that ended up with
                // zero entries (typically all-sidecar-stored String columns
                // where the per-row insert path skips). Bitmap covers
                // chunk-level pruning + per-row bitmask for low-cardinality
                // columns; an additional B+Tree would just duplicate the same
                // information at higher storage cost.
                DropRedundantColumnTrees(columnTrees, index, datumPath);

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
                IndexedColumns: CollectIndexedColumnNames(index, columnTrees.Keys),
                BloomColumns: CollectBloomColumnNames(index),
                SortedColumns: columnTrees.Keys.ToArray(),
                BitmapColumns: CollectBitmapColumnNames(index),
                DeferredReindexColumns: Array.Empty<string>(),
                Elapsed: sw.Elapsed);
        }
        finally
        {
            foreach (ColumnTreeBuild build in columnTrees.Values)
            {
                build.Tree.Dispose();
            }
        }
    }

    /// <summary>
    /// Drops per-column tree files for columns whose unified-sidecar bitmap
    /// already covers them, and for trees that ended up with zero entries
    /// (e.g. all values were sidecar-stored). Mutates <paramref name="columnTrees"/>
    /// in place so the caller's `finally` block doesn't double-dispose. Best-
    /// effort on file delete (any failure leaves the tree on disk; next REINDEX
    /// rebuilds it).
    /// </summary>
    private static void DropRedundantColumnTrees(
        Dictionary<string, ColumnTreeBuild> columnTrees,
        SourceIndex index,
        string datumPath)
    {
        if (columnTrees.Count == 0) return;

        List<string> toRemove = new();

        foreach (KeyValuePair<string, ColumnTreeBuild> kvp in columnTrees)
        {
            bool bitmapCovers = index.BitmapIndexes is not null
                && index.BitmapIndexes.TryGetIndex(kvp.Key, out _);
            bool emptyTree = kvp.Value.Tree.EntryCount == 0;
            if (bitmapCovers || emptyTree)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (string col in toRemove)
        {
            columnTrees[col].Tree.Dispose();
            columnTrees.Remove(col);
            string treePath = DatumFileTableProviderV2.GetColumnIndexPath(datumPath, col);
            try { File.Delete(treePath); } catch { }
        }
    }

    private readonly record struct ColumnTreeBuild(MutableBPlusTree Tree, DataKind KeyKind);

    private static void DeleteExistingColumnTreeFiles(string datumPath)
    {
        string directory = Path.GetDirectoryName(datumPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(datumPath);
        string prefix = baseName + ".datum-bptree-";

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Directory doesn't exist yet — nothing to clean.
        }
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

        // PR13d: open existing per-column trees and append delta-row entries.
        // The chunk index for delta rows starts at existing.Chunks.Count
        // (matches SourceIndex.Merge's shifted chunk numbering).
        Schema providerSchema = provider.GetSchema();
        IReadOnlySet<string> indexableColumns = SourceIndexBuilder.ResolveAutoIndexColumns(providerSchema);
        Dictionary<string, ColumnTreeBuild> columnTrees = new(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnInfo column in providerSchema.Columns)
        {
            if (!indexableColumns.Contains(column.Name)) continue;
            string treePath = DatumFileTableProviderV2.GetColumnIndexPath(datumPath, column.Name);
            // If no tree exists yet (extending an index that pre-dates per-column
            // trees), create one — the existing rows it would have covered are
            // unrepresented but acceleration for new rows still works.
            MutableBPlusTree tree = File.Exists(treePath)
                ? MutableBPlusTree.Open(treePath)
                : MutableBPlusTree.Create(treePath, column.Kind, allowDuplicates: true);
            columnTrees[column.Name] = new ColumnTreeBuild(tree, column.Kind);
        }

        int existingChunkCount = existing.Chunks.Count;
        int deltaChunkIndex = 0;
        int rowsInCurrentDeltaChunk = 0;
        int chunkSize = options.ChunkSize;

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
                    Row row = batch[r];
                    incremental.AddRow(row, batch.Arena);

                    int absoluteChunkIndex = existingChunkCount + deltaChunkIndex;
                    foreach (KeyValuePair<string, ColumnTreeBuild> kvp in columnTrees)
                    {
                        DataValue value = row[kvp.Key];
                        if (value.IsNull) continue;
                        if (value.IsInSidecar) continue;
                        kvp.Value.Tree.Insert(new ValueIndexEntry(value, absoluteChunkIndex, rowsInCurrentDeltaChunk));
                    }

                    rowsInCurrentDeltaChunk++;
                    deltaRowCount++;

                    if (rowsInCurrentDeltaChunk >= chunkSize)
                    {
                        deltaChunkIndex++;
                        rowsInCurrentDeltaChunk = 0;
                    }
                }
                rowsScanned += batch.Count;
                pool.ReturnRowBatch(batch);
            }

            deltaIndex = incremental.Finalize();
            merged = SourceIndex.Merge(existing, deltaIndex);

            // PR13e-A: same redundant-tree cleanup as IndexAsync. After the
            // merge, columns whose merged-bitmap covers them no longer need
            // their per-column tree on disk. Note: this can delete a tree
            // that prior INSERTs populated — that's intentional, the bitmap
            // takes over.
            DropRedundantColumnTrees(columnTrees, merged, datumPath);

            SourceIndexSet indexSet = SourceIndexSet.Create(tableName, merged);

            await using Stream outputStream = await destination.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            UnifiedIndexWriter.Write(indexSet, outputStream);
            bytesWritten = outputStream.Length;
        }
        finally
        {
            incremental.Dispose();
            foreach (ColumnTreeBuild build in columnTrees.Values)
            {
                build.Tree.Dispose();
            }
        }

        sw.Stop();

        return new IndexResult(
            OutputPath: destination.FilePath,
            RowCount: rowsScanned,
            ChunkCount: merged.Chunks.Count,
            BytesWritten: bytesWritten,
            Schema: merged.Schema.Schema,
            Fingerprint: fingerprint,
            IndexedColumns: CollectIndexedColumnNames(merged, columnTrees.Keys),
            BloomColumns: CollectBloomColumnNames(merged),
            SortedColumns: columnTrees.Keys.ToArray(),
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

    private static IReadOnlyList<string> CollectIndexedColumnNames(SourceIndex index, IEnumerable<string> columnTreeColumns)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in CollectBloomColumnNames(index)) names.Add(name);
        foreach (string name in CollectBitmapColumnNames(index)) names.Add(name);
        foreach (string name in columnTreeColumns) names.Add(name);
        return names.ToArray();
    }

    private static IReadOnlyList<string> CollectBloomColumnNames(SourceIndex index)
        => index.BloomFilters is { } filters ? filters.ColumnNames.ToArray() : Array.Empty<string>();

    private static IReadOnlyList<string> CollectBitmapColumnNames(SourceIndex index)
        => index.BitmapIndexes is { } bitmap ? bitmap.ColumnNames.ToArray() : Array.Empty<string>();
}
