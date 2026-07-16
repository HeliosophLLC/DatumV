using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.DatumFile;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.DatumFile.V2.Decoding;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.BTree.Mutable;
using Heliosoph.DatumV.Indexing.Fts;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Statistics;

namespace Heliosoph.DatumV.Catalog.Providers;

public sealed partial class DatumFileTableProviderV2
{
    /// <inheritdoc/>
    public IAppendSession BeginAppend()
    {
        // Block until any prior session/mutation has released the
        // semaphore. The session takes ownership of the permit and
        // releases it on Dispose / Commit. Sync .Wait() here mirrors
        // the other mutation methods; future async-friendly callers
        // can call BeginAppendAsync if added.
        _mutationLock.Wait();
        try
        {
            return new DatumAppendSession(this);
        }
        catch
        {
            _mutationLock.Release();
            throw;
        }
    }

    /// <inheritdoc/>
    public void DeleteRows(IReadOnlyList<long> rowIndices)
    {
        ArgumentNullException.ThrowIfNull(rowIndices);
        if (rowIndices.Count == 0) return;

        _mutationLock.Wait();
        try
        {
            // The contract's indices are live-row positions (post-tombstone
            // numbering, matching scan emission order); the on-disk tombstone
            // bitmaps are keyed by raw physical position. Translate before
            // writing — once earlier deletes exist the two spaces diverge,
            // and a live index written straight into the bitmap tombstones
            // whatever row happens to sit at that physical slot.
            Dictionary<long, (long RawRow, int Page, int RowInPage)> liveToRaw =
                MapLiveRowsToRaw(rowIndices, operation: "DeleteRows");
            long[] rawIndices = new long[liveToRaw.Count];
            int next = 0;
            foreach ((long rawRow, _, _) in liveToRaw.Values)
            {
                rawIndices[next++] = rawRow;
            }

            DatumFileWriterV2.SoftDeleteRows(_descriptor.FilePath, rawIndices);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
            // Secondary-index staleness after DELETE is a known limitation
            // shared by composite + FTS: tombstoned rows shift the live-row
            // numbering that postings reference. Fixed by REINDEX, or by a
            // future DELETE async path that can call RebuildFtsIndexesNoLockAsync /
            // RebuildCompositeIndexesNoLockAsync without sync-over-async.
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UpdateRowsAsync(IReadOnlyList<RowUpdateRequest> requests, IValueStore? sourceStore = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) return;

        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Map every request's live row index to a raw (pageIndex,
            // rowInPage) tuple via the shared live→raw walk over the
            // current snapshot — same numbering as the scan path emits.
            int[] schemaToFooter = _snapshot.SchemaToFooterIndex;

            HashSet<long> uniqueLiveIndices = new(requests.Count);
            foreach (RowUpdateRequest req in requests) uniqueLiveIndices.Add(req.LiveRowIndex);
            Dictionary<long, (long RawRow, int Page, int RowInPage)> liveToRaw =
                MapLiveRowsToRaw(uniqueLiveIndices, operation: "UpdateRows");

            // Group requests by page; translate schema-column indices to
            // footer-column indices using SchemaToFooterIndex (the schema
            // skips tombstoned columns; the footer keeps them).
            Dictionary<int, List<RowUpdate>> grouped = new();
            foreach (RowUpdateRequest req in requests)
            {
                (_, int page, int row) = liveToRaw[req.LiveRowIndex];
                Dictionary<int, DataValue> footerKeyedValues = new(req.NewValues.Count);
                foreach ((int schemaColIdx, DataValue value) in req.NewValues)
                {
                    if (schemaColIdx < 0 || schemaColIdx >= schemaToFooter.Length)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(requests), schemaColIdx,
                            $"UpdateRows: schema column index {schemaColIdx} out of range for " +
                            $"table '{QualifiedName}' (schema has {schemaToFooter.Length} column(s)).");
                    }
                    footerKeyedValues[schemaToFooter[schemaColIdx]] = value;
                }
                if (!grouped.TryGetValue(page, out List<RowUpdate>? bucket))
                {
                    bucket = new List<RowUpdate>();
                    grouped[page] = bucket;
                }
                bucket.Add(new RowUpdate(row, footerKeyedValues));
            }

            // RewritePages handles the page-COW commit (including
            // generation bump and torn-tail recovery). Pass the
            // conventional sidecar path; the rewriter only opens it if
            // a VariableSlot column is touched.
            Dictionary<int, IReadOnlyList<RowUpdate>> dispatch = new(grouped.Count);
            foreach ((int p, List<RowUpdate> rows) in grouped)
            {
                dispatch[p] = rows;
            }

            string sidecarPath = Path.ChangeExtension(_descriptor.FilePath, SidecarConstants.FileExtension);
            DatumFileWriterV2.RewritePages(
                _descriptor.FilePath,
                sidecarPath,
                dispatch,
                sourceStore);

            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: true);

            // PR13c: auto-refresh .datum-index after UPDATE. Mirrors
            // PR13a-2's INSERT unlock — the user no longer needs a
            // manual REINDEX to restore acceleration after a row
            // rewrite. Full rebuild rather than chunk-splice extend:
            // UPDATE may rewrite an arbitrary subset of existing
            // chunks, so the append-only merge from PR13b doesn't
            // apply. Per-chunk replacement (decompress affected
            // chunks' bitmaps, OR new value into bloom, recompute zone
            // map, splice back) is the future PR13c-perf optimisation;
            // v1 trades correctness + simplicity for the per-INSERT
            // cost we already pay. Best-effort: a failure here leaves
            // the data commit in place and the index Stale (visible
            // via system.indexes.is_valid = false).
            try
            {
                await RebuildIndexNoLockAsync(existingForExtend: null).ConfigureAwait(false);
            }
            catch
            {
                // Swallowed — data commit stands. The catch above
                // already invalidated the cache so GetSourceIndex
                // returns null and queries fall back to scan.
            }

            // Composite indexes need a full rebuild after UPDATE: a row
            // whose indexed-column value changed leaves a stale entry
            // pointing at a key that no longer matches the underlying
            // row's data. Cheapest correct path until an incremental
            // delete-old/insert-new path lands. Best-effort to keep the
            // failure-mode story consistent with the source-index rebuild
            // above — the data commit stands even if composite-index
            // rebuild fails (queries may return stale matches until the
            // user re-issues DROP INDEX / CREATE INDEX).
            try
            {
                await RebuildCompositeIndexesNoLockAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallowed — see above.
            }

            // FTS indexes follow the same correctness story as composite
            // indexes: an UPDATE rewrites the indexed text, so any posting
            // for the old terms points at a row whose value no longer
            // contains them. Full rebuild is the cheapest correct path until
            // an incremental delete-old/insert-new posting path lands. Same
            // best-effort failure contract — data commit stands even if the
            // FTS rebuild fails; queries fall back to the FilterOperator
            // path (which scans + re-tokenizes per row, so still correct).
            try
            {
                await RebuildFtsIndexesNoLockAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallowed — see above.
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Maps live row indices (post-tombstone numbering — the space the
    /// <see cref="ITableProvider"/> mutation contract uses, matching scan
    /// emission order) to raw physical positions: the linear row index
    /// the tombstone bitmaps are keyed by, plus the (page, rowInPage)
    /// pair the page rewriter addresses. Walks the current snapshot's
    /// page directory once in ascending index order, skipping rows the
    /// snapshot already marks tombstoned. Caller must hold
    /// <see cref="_mutationLock"/> so the snapshot can't swap mid-walk.
    /// </summary>
    /// <param name="liveIndices">Live row indices to resolve; duplicates are collapsed.</param>
    /// <param name="operation">Operation name used in the out-of-range message.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A requested index is negative or at/past the table's live row count.
    /// </exception>
    private Dictionary<long, (long RawRow, int Page, int RowInPage)> MapLiveRowsToRaw(
        IReadOnlyCollection<long> liveIndices, string operation)
    {
        Snapshot snap = _snapshot;
        FooterV2 footer = snap.Reader.Footer;
        byte[]?[]? tombstones = snap.ChapterTombstoneBitmaps;
        int chapterRowSpan = ChapterTombstoneBlock.MaxRowsPerChapter;

        // Sort the unique indices ascending so a single forward walk over
        // the page directory resolves all of them.
        HashSet<long> unique = new(liveIndices.Count);
        foreach (long idx in liveIndices) unique.Add(idx);
        long[] sortedLive = unique.ToArray();
        Array.Sort(sortedLive);

        Dictionary<long, (long RawRow, int Page, int RowInPage)> liveToRaw = new(sortedLive.Length);

        // Probe the first live column for the page directory — footer
        // slot 0 may be a dropped (tombstoned) column; all live columns
        // share page counts and per-page row counts by construction.
        int[] schemaToFooter = snap.SchemaToFooterIndex;
        int probeFooterIdx = schemaToFooter.Length > 0 ? schemaToFooter[0] : -1;
        int pageCount = probeFooterIdx >= 0 ? footer.Columns[probeFooterIdx].Pages.Count : 0;

        int sortedIndex = 0;
        long liveCounter = 0;
        long rawRow = 0;
        for (int p = 0; p < pageCount && sortedIndex < sortedLive.Length; p++)
        {
            int rowsInPage = footer.Columns[probeFooterIdx].Pages[p].RowCount;
            for (int r = 0; r < rowsInPage; r++, rawRow++)
            {
                int chapterIndex = (int)(rawRow / chapterRowSpan);
                int rowInChapter = (int)(rawRow - (long)chapterIndex * chapterRowSpan);
                bool tombstoned = tombstones is not null
                    && chapterIndex < tombstones.Length
                    && tombstones[chapterIndex] is byte[] bits
                    && (bits[rowInChapter >> 3] & (1 << (rowInChapter & 7))) != 0;
                if (tombstoned) continue;

                if (sortedLive[sortedIndex] == liveCounter)
                {
                    liveToRaw[liveCounter] = (rawRow, p, r);
                    sortedIndex++;
                }
                liveCounter++;
                if (sortedIndex >= sortedLive.Length) break;
            }
        }

        if (sortedIndex < sortedLive.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(liveIndices), sortedLive[sortedIndex],
                $"{operation}: live row index {sortedLive[sortedIndex]} is out of range " +
                $"for table '{QualifiedName}' (table has {liveCounter} live row(s)).");
        }

        return liveToRaw;
    }

    private DatumFileWriterV2 OpenAppendWriter()
    {
        string sidecarPath = Path.ChangeExtension(_descriptor.FilePath, SidecarConstants.FileExtension);
        DatumFileWriterV2 writer = DatumFileWriterV2.OpenForAppend(_descriptor.FilePath, sidecarPath);

        // Append batches can carry sidecar-backed values scanned from OTHER
        // tables (CTAS, INSERT … SELECT). Hand the writer the catalog-wide
        // registry plus this table's own blob source so it copies foreign
        // payload bytes into this table's sidecar instead of persisting
        // dangling foreign (offset, length) pairs.
        if (SidecarRegistry is not null)
        {
            writer.ConfigureSidecarImport(SidecarRegistry, Sidecar);
        }
        return writer;
    }

    /// <inheritdoc/>
    public bool CanRebuildIndex => true;

    /// <inheritdoc/>
    public async Task RebuildIndexAsync()
    {
        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Public REINDEX entry — always full rebuild (no carry-forward
            // from the existing in-memory snapshot). Called by users who
            // want a from-scratch sweep regardless of how the on-disk
            // index drifted.
            await RebuildIndexNoLockAsync(existingForExtend: null).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public bool CanRebuildManifest => true;

    /// <inheritdoc/>
    public async Task RebuildManifestAsync()
    {
        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await RebuildManifestNoLockAsync().ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Refreshes the <c>.datum-manifest</c> sidecar by streaming the
    /// table's rows through a fresh <see cref="StatisticsCollector"/>,
    /// rebuilding the per-column <see cref="QueryResultsManifest"/>, and
    /// atomically replacing the on-disk file. Caller must already hold
    /// <see cref="_mutationLock"/>. After this returns,
    /// <see cref="GetManifest"/> reflects the freshly-built cached half.
    /// </summary>
    private async Task RebuildManifestNoLockAsync()
    {
        Schema schema = _snapshot.Schema;

        // Empty schema (table created but never populated): write an empty
        // manifest so subsequent reads don't see stale cached fields.
        StatisticsCollector collector = new();
        long rowCount = 0;

        await foreach (RowBatch batch in ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                collector.Collect(batch);
                rowCount += batch.Count;
            }
            finally
            {
                batch.Dispose();
            }
        }

        Dictionary<string, ColumnInfo> columnInfos = new(schema.Columns.Count);
        foreach (ColumnInfo column in schema.Columns)
        {
            columnInfos[column.Name] = column;
        }

        QueryResultsManifest manifest = ManifestBuilder.Build(
            collector.GetStatistics(), columnInfos, rowCount);

        // Atomically replace the .datum-manifest file. Write to .tmp then
        // rename so a concurrent reader either sees the old file or the
        // fully-written new one, never a torn write.
        string finalPath = PathDetector.GetSidecarBasePath(_descriptor.FilePath) + ".datum-manifest";
        string tempPath = finalPath + ".tmp";
        try
        {
            await ManifestSerializer.WriteToFileAsync(_descriptor.Name, manifest, tempPath)
                .ConfigureAwait(false);
            if (File.Exists(finalPath))
            {
                File.Replace(tempPath, finalPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        _manifest = manifest;
        _cachedStatsStale = false;
    }

    /// <summary>
    /// Refreshes the <c>.datum-index</c> without taking the mutation
    /// lock. Caller must already hold <see cref="_mutationLock"/>.
    /// </summary>
    /// <param name="existingForExtend">
    /// When non-null and <see cref="Indexer.CanExtend"/> returns true,
    /// the rebuild uses PR13b's chunk-splice path: the prefix's
    /// bloom + bitmap + zone-map data is carried forward verbatim and
    /// only the appended rows are scanned. The caller (typically
    /// <see cref="DatumAppendSession.CommitAsync"/>) supplies the
    /// pre-mutation in-memory index. Pass <see langword="null"/> for a
    /// full rebuild from scratch.
    /// </param>
    /// <remarks>
    /// <para>
    /// Two-step swap pattern:
    /// <list type="number">
    ///   <item>Write the new index to a <c>.tmp</c> sibling so the
    ///     existing <see cref="_mappedIndexSet"/> can stay open during
    ///     the merge (the extend path's bloom filters read through the
    ///     accessor up until <c>UnifiedIndexWriter.Write</c> finishes).</item>
    ///   <item>Once the temp file is fully written, dispose the old
    ///     mapping, rename temp → final, reopen.</item>
    /// </list>
    /// </para>
    /// <para>
    /// On failure the temp file may be left orphaned; the on-disk
    /// final file remains stale (pre-mutation fingerprint) and the
    /// in-memory cache is invalidated so the index surfaces as Stale
    /// per the <see cref="IndexValidity"/> contract.
    /// </para>
    /// </remarks>
    private async Task RebuildIndexNoLockAsync(SourceIndex? existingForExtend)
    {
        // Detach the cached index immediately. After mutation, the
        // post-mutation snapshot is already live but the in-memory
        // index still describes the pre-mutation file. A concurrent
        // reader that observes that pair can issue indexed queries
        // whose pruning is wrong — for INSERT, chunks past the
        // existing tail are invisible; for UPDATE, the new value's
        // bloom/bitmap entries don't exist anywhere in the old index.
        _sourceIndex = null;

        // PR13d: per-column tree files are also rewritten by Indexer.
        // Close the in-memory MutableBPlusTree handles up front so the
        // file rename below isn't blocked by FileShare.None on the open
        // tree files. Concurrent readers that already captured an
        // IColumnIndex reference keep working through it (the underlying
        // FileStream stays alive via that reference until they release).
        CloseColumnIndexes();

        string finalPath = PathDetector.GetSidecarBasePath(_descriptor.FilePath) + ".datum-index";
        string tempPath = finalPath + ".tmp";

        Indexer indexer = new(_pool);
        bool useExtend = existingForExtend is not null && Indexer.CanExtend(existingForExtend);

        try
        {
            OutputDescriptor destination = new(tempPath);
            if (useExtend)
            {
                await indexer.ExtendAsync(this, _descriptor.FilePath, destination, existingForExtend!, IndexOptions.Default)
                    .ConfigureAwait(false);
            }
            else
            {
                await indexer.IndexAsync(this, _descriptor.FilePath, destination, IndexOptions.Default)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            InvalidateSourceIndexCache();
            // Reopen any per-column trees that the failed rebuild left
            // behind on disk so reads can still accelerate against
            // whatever made it through.
            OpenColumnIndexes();
            throw;
        }

        InvalidateSourceIndexCache();
        File.Move(tempPath, finalPath, overwrite: true);

        (_mappedIndexSet, _sourceIndex) = TryLoadSourceIndex(_descriptor);
        OpenColumnIndexes();
    }

    /// <summary>
    /// Phase 3a-wide append-session fast path: writes the supplied (already
    /// merged) <paramref name="merged"/> index to the unified sidecar via the
    /// same temp-file + atomic-rename dance as <see cref="RebuildIndexNoLockAsync"/>,
    /// but skips the Indexer scan entirely (the append session built the delta
    /// in lockstep with the data writes). Caller must hold the mutation lock.
    /// Per-column trees stay open across this call — the append session
    /// already inserted entries into them in-place.
    /// </summary>
    /// <param name="merged">Pre-merged source index (existing prefix + delta from the append session).</param>
    /// <param name="tableName">Logical table name for the SourceIndexSet wrapper.</param>
    private void WriteUnifiedSidecarNoLock(SourceIndex merged, string tableName)
    {
        _sourceIndex = null;

        string finalPath = PathDetector.GetSidecarBasePath(_descriptor.FilePath) + ".datum-index";
        string tempPath = finalPath + ".tmp";

        try
        {
            SourceIndexSet indexSet = SourceIndexSet.Create(tableName, merged);
            using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                UnifiedIndexWriter.Write(indexSet, stream);
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            InvalidateSourceIndexCache();
            throw;
        }

        InvalidateSourceIndexCache();
        File.Move(tempPath, finalPath, overwrite: true);

        (_mappedIndexSet, _sourceIndex) = TryLoadSourceIndex(_descriptor);
    }

    /// <summary>
    /// Ensures every indexable column in the live schema has an open per-column
    /// tree available for in-session inserts. Trees that don't yet exist on
    /// disk (first append on a fresh table, or a column added after the last
    /// index build) are <c>Create</c>d and registered. Caller must hold the
    /// mutation lock.
    /// </summary>
    private void EnsureColumnTreesForIndexableColumnsNoLock()
    {
        Schema schema = _snapshot.Schema;
        foreach (ColumnInfo column in schema.Columns)
        {
            if (!Indexing.SourceIndexBuilder.IsAutoIndexableKind(column.Kind)) continue;
            if (column.IsArray) continue;
            if (_columnTrees.ContainsKey(column.Name)) continue;

            string treePath = GetColumnIndexPath(_descriptor.FilePath, column.Name);
            MutableBPlusTree tree = File.Exists(treePath)
                ? MutableBPlusTree.Open(treePath)
                : MutableBPlusTree.Create(treePath, column.Kind, allowDuplicates: true);

            _columnTrees[column.Name] = tree;
            _columnIndexes[column.Name] = new MutableBPlusTreeColumnIndex(tree);
        }
    }

    /// <summary>
    /// Returns the open per-column tree for <paramref name="columnName"/>, or
    /// <see langword="null"/> if no tree was opened (column not indexable, or
    /// initial open failed). Used by the append session's per-row insert path
    /// — the same lock that gates the session ensures no concurrent read
    /// observes torn tree state during the queued-insert flush.
    /// </summary>
    internal MutableBPlusTree? GetColumnTreeForAppendSession(string columnName)
    {
        return _columnTrees.TryGetValue(columnName, out MutableBPlusTree? tree) ? tree : null;
    }

    /// <summary>
    /// Builds a new <see cref="Snapshot"/> from the on-disk file post-
    /// mutation and atomically swaps it in. Caller must hold
    /// <see cref="_mutationLock"/>. When
    /// <paramref name="sidecarMayHaveGrown"/> is <see langword="true"/>
    /// (i.e. an append just ran), the catalog's
    /// <see cref="SidecarRegistry"/> is reconciled with the new
    /// <see cref="IBlobSource"/> so existing storeId-stamped DataValues
    /// in flight resolve through bytes the new mmap can see. When the
    /// sidecar appears for the first time on this commit (the table was
    /// created without one and an append just spilled a wide value),
    /// the registry has no slot for it yet — the provider allocates a
    /// fresh <c>storeId</c> via <see cref="SidecarRegistry.Register"/>
    /// instead of <see cref="SidecarRegistry.UpdateAt"/>.
    /// </summary>
    /// <remarks>
    /// Does NOT touch <see cref="_sourceIndex"/> / <see cref="_mappedIndexSet"/>.
    /// Mutations that only invalidate the index (AddColumn, DropColumn,
    /// DeleteRows, UPDATE) call <see cref="InvalidateSourceIndexCache"/>
    /// after this. Mutations that refresh the index (AppendRows commit,
    /// PR13b extend path) keep the existing in-memory index alive long
    /// enough to be merged with the delta — see
    /// <c>RebuildIndexNoLock(SourceIndex?)</c>.
    /// </remarks>
    private void RebuildSnapshotAfterMutation(bool sidecarMayHaveGrown)
    {
        Snapshot next = OpenSnapshot(_descriptor.FilePath);

        // Always re-point the registry at the new sidecar handle when
        // the next snapshot has one. `SwapSnapshot` disposes the old
        // sidecar unconditionally, so leaving the registry pointing at
        // it would dangle into a disposed object for any subsequent
        // sidecar-backed read. The `sidecarMayHaveGrown` flag is just
        // a hint for invalidating cached statistics — it does NOT
        // control whether the sidecar handle was replaced (every
        // rebuild reopens the sidecar fresh).
        //
        // First-appearance vs update: the previous snapshot had no
        // sidecar exactly when the catalog never registered one for
        // this provider (RegisterProviderSidecar early-returns when
        // Sidecar is null at provider-add time). In that case the
        // current `SidecarStoreId` is the default zero, which would
        // collide with whatever else lives at slot 0 — Register
        // allocates a fresh slot instead.
        if (next.Sidecar is not null && SidecarRegistry is not null)
        {
            bool firstAppearance = _snapshot.Sidecar is null;
            if (firstAppearance)
            {
                SidecarStoreId = SidecarRegistry.Register(next.Sidecar);
            }
            else
            {
                SidecarRegistry.UpdateAt(SidecarStoreId, next.Sidecar);
            }
        }
        SwapSnapshot(next);

        // PR14j: every mutation drifts the cached half of the manifest
        // (top-K, quantiles, histogram, entropy, kind-specific summaries)
        // away from the live data. Surface that to consumers via
        // CachedStatsValid=false until ANALYZE rescans.
        _cachedStatsStale = true;

        // sidecarMayHaveGrown was used historically to gate the
        // registry update; it now only documents intent at the call
        // site. Keep the parameter so callers don't need a sweep.
        _ = sidecarMayHaveGrown;
    }

    /// <summary>
    /// Drops the cached <see cref="SourceIndex"/> and disposes the
    /// memory-mapped sidecar handle. Used by mutations that don't
    /// refresh the index (AddColumn / DropColumn / DeleteRows / UPDATE
    /// in PR13b — UPDATE moves to the extend path in PR13c).
    /// </summary>
    private void InvalidateSourceIndexCache()
    {
        MappedSourceIndexSet? staleMapped = _mappedIndexSet;
        _mappedIndexSet = null;
        _sourceIndex = null;
        staleMapped?.Dispose();
    }
}
