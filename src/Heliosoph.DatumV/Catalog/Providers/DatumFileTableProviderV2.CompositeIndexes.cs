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
    // ──────────────────── Composite index lifecycle ────────────────────

    /// <summary>
    /// Returns the <c>.datum-cindex-{indexName}</c> path companion for the
    /// given <c>.datum</c> file. Index name characters are sanitized so
    /// non-alphanumeric chars don't collide with the path separator.
    /// </summary>
    internal static string GetCompositeIndexPath(string datumPath, string indexName)
    {
        string sanitized = SanitizeColumnNameForPath(indexName);
        return Path.ChangeExtension(datumPath, $".datum-cindex-{sanitized}");
    }

    /// <summary>
    /// Opens every composite-index sidecar declared in
    /// <see cref="TableDescriptor.Indexes"/> at provider construction. Files
    /// that don't open cleanly (torn write, version mismatch) are skipped
    /// silently — the index degrades to "not loaded" until <c>DROP INDEX</c>
    /// and <c>CREATE INDEX</c> rebuild it. Schema mismatches (a referenced
    /// column no longer exists) likewise skip the load and leave the
    /// descriptor entry intact for a future REPAIR pass.
    /// </summary>
    private void OpenCompositeIndexes()
    {
        if (_descriptor.Indexes is not { Count: > 0 } declared) return;
        Schema schema = _snapshot.Schema;

        foreach (IndexDescriptor descriptor in declared)
        {
            string treePath = GetCompositeIndexPath(_descriptor.FilePath, descriptor.Name);
            if (!File.Exists(treePath)) continue;

            // Resolve column ordinals; skip the index entirely if any column
            // is missing (e.g. ALTER DROP COLUMN ran in a prior session
            // without cascading the index drop).
            int[]? ordinals = TryResolveCompositeIndexOrdinals(schema, descriptor);
            if (ordinals is null) continue;

            try
            {
                Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree =
                    Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Open(treePath);
                _compositeIndexTrees[descriptor.Name] = tree;
                _compositeIndexColumnIndices[descriptor.Name] = ordinals;
                _compositeIndexDescriptors[descriptor.Name] = descriptor;
            }
            catch
            {
                // Skip — caller has no other recovery surface; REINDEX once
                // it learns about composite indexes will rebuild.
            }
        }
    }

    /// <summary>
    /// Closes every composite-index tree and clears the dictionaries.
    /// Called from <see cref="Dispose"/>. Callers holding stale references
    /// after Dispose is undefined behavior (matches the rest of the
    /// provider's disposal contract).
    /// </summary>
    private void CloseCompositeIndexes()
    {
        foreach (Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree in _compositeIndexTrees.Values)
        {
            try { tree.Dispose(); } catch { /* best-effort */ }
        }
        _compositeIndexTrees.Clear();
        _compositeIndexColumnIndices.Clear();
        _compositeIndexDescriptors.Clear();
    }

    /// <summary>
    /// Resolves each column name in <paramref name="descriptor"/> to its
    /// schema ordinal. Returns <see langword="null"/> if any column is
    /// missing — the caller treats that as "skip this index for now".
    /// </summary>
    private static int[]? TryResolveCompositeIndexOrdinals(Schema schema, IndexDescriptor descriptor)
    {
        int[] result = new int[descriptor.Columns.Count];
        for (int i = 0; i < descriptor.Columns.Count; i++)
        {
            int ordinal = -1;
            for (int j = 0; j < schema.Columns.Count; j++)
            {
                if (string.Equals(schema.Columns[j].Name, descriptor.Columns[i], StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = j;
                    break;
                }
            }
            if (ordinal < 0) return null;
            result[i] = ordinal;
        }
        return result;
    }

    /// <summary>
    /// Creates a new composite-index sidecar (<c>.datum-cindex-{Name}</c>),
    /// registers it for INSERT maintenance, and — when the table already
    /// has rows — backfills the tree from a full table scan so existing
    /// keys are immediately findable. INSERTs after this point queue
    /// new entries through the append session as usual.
    /// </summary>
    /// <remarks>
    /// Holds <see cref="_mutationLock"/> for the duration so concurrent
    /// INSERT / UPDATE / DELETE / DROP TABLE are serialised against the
    /// build. The new tree is registered in the visible-state dictionaries
    /// only after the scan completes, so a query running concurrently
    /// with the build either misses the index (and falls through to scan)
    /// or sees the fully-populated tree — never a half-built one. This is
    /// the non-<c>CONCURRENTLY</c> Postgres model: writers block until
    /// the build returns.
    /// </remarks>
    internal async Task AddCompositeIndexAsync(IndexDescriptor descriptor)
    {
        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_compositeIndexTrees.ContainsKey(descriptor.Name))
            {
                throw new InvalidOperationException(
                    $"Composite index '{descriptor.Name}' is already open on table '{_descriptor.Name}'.");
            }

            int[]? ordinals = TryResolveCompositeIndexOrdinals(_snapshot.Schema, descriptor);
            if (ordinals is null)
            {
                // Caller (TableCatalog.ApplyCreateIndexAsync) already
                // validated column existence; this is a defense-in-depth
                // check.
                throw new InvalidOperationException(
                    $"CREATE INDEX '{descriptor.Name}': one or more columns no longer exist on the schema.");
            }

            string treePath = GetCompositeIndexPath(_descriptor.FilePath, descriptor.Name);
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree =
                Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Create(treePath, allowDuplicates: !descriptor.IsUnique);

            try
            {
                // Backfill into the local tree handle BEFORE it's visible
                // to readers / future append sessions. Empty tables yield
                // no batches and the loop is a cheap no-op. For UNIQUE
                // indexes a duplicate encountered during backfill bubbles
                // up via DuplicateKeyException (translated by the
                // helper) and the outer catch rolls back the partially
                // built tree before any visible-state mutation.
                if (GetRowCount() > 0)
                {
                    Dictionary<string, CompositeIndexBuildTarget> targets = new(StringComparer.OrdinalIgnoreCase)
                    {
                        [descriptor.Name] = new CompositeIndexBuildTarget(tree, ordinals, descriptor.IsUnique, descriptor.Name),
                    };
                    await PopulateCompositeIndexesFromScanAsync(targets, CancellationToken.None).ConfigureAwait(false);
                }

                // Build succeeded — publish to visible state. After this
                // point the planner picks up the index and any pending
                // AppendSession will see it on its next start. The dict
                // lock makes the three-dict update atomic from any
                // concurrent GetCompositeIndexes() snapshot.
                lock (_compositeIndexSync)
                {
                    _compositeIndexTrees[descriptor.Name] = tree;
                    _compositeIndexColumnIndices[descriptor.Name] = ordinals;
                    _compositeIndexDescriptors[descriptor.Name] = descriptor;
                }
            }
            catch
            {
                // Build failed — tear down the local tree and remove the
                // sidecar so a retry starts clean. Nothing was published
                // to the visible-state dictionaries.
                try { tree.Dispose(); } catch { /* best-effort */ }
                try { if (File.Exists(treePath)) File.Delete(treePath); } catch { /* best-effort */ }
                throw;
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Drops every open composite-index tree, deletes its sidecar, and
    /// rebuilds it from a full table scan. Used after UPDATE (which can
    /// shift an indexed-column value into a new key) — the cheapest
    /// correct path until incremental update-paths land. The (chunkIndex,
    /// rowOffsetInChunk) positions stored in fresh entries match the live
    /// <see cref="SourceIndex"/>'s chunk directory, so the planner's
    /// seek path resolves them via the same arithmetic as INSERT-time
    /// entries. Caller must hold <c>_mutationLock</c>.
    /// </summary>
    private async Task RebuildCompositeIndexesNoLockAsync(CancellationToken ct)
    {
        if (_compositeIndexTrees.Count == 0 && _compositeIndexDescriptors.Count == 0) return;

        // Snapshot descriptors before tearing down — _compositeIndexDescriptors
        // is the source of truth for "what indexes existed pre-rebuild."
        List<IndexDescriptor> descriptors = new(_compositeIndexDescriptors.Values);

        // Snapshot the existing tree handles under the dict lock, clear the
        // dicts atomically (so concurrent GetCompositeIndexes sees either
        // pre- or post-clear state), then dispose handles + delete files
        // outside the lock — disposal is slow and shouldn't block readers.
        List<Indexing.BTree.MutableBytes.MutableBPlusTreeBytes> oldHandles = new();
        lock (_compositeIndexSync)
        {
            foreach (Indexing.BTree.MutableBytes.MutableBPlusTreeBytes h in _compositeIndexTrees.Values)
            {
                oldHandles.Add(h);
            }
            _compositeIndexTrees.Clear();
            _compositeIndexColumnIndices.Clear();
            _compositeIndexDescriptors.Clear();
        }
        foreach (Indexing.BTree.MutableBytes.MutableBPlusTreeBytes h in oldHandles)
        {
            try { h.Dispose(); } catch { /* best-effort */ }
        }
        foreach (IndexDescriptor d in descriptors)
        {
            string path = GetCompositeIndexPath(_descriptor.FilePath, d.Name);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }

        // Recreate empty trees. Schema-drift (column missing) drops the
        // index — the catalog descriptor entry will be cleaned up by the
        // next save (or by an explicit DROP INDEX); we just don't open
        // the tree. Republish under the dict lock.
        Schema schema = _snapshot.Schema;
        Dictionary<string, (Indexing.BTree.MutableBytes.MutableBPlusTreeBytes Tree, int[] Ordinals, IndexDescriptor Descriptor)> rebuilt = new();
        foreach (IndexDescriptor d in descriptors)
        {
            int[]? ordinals = TryResolveCompositeIndexOrdinals(schema, d);
            if (ordinals is null) continue;

            string path = GetCompositeIndexPath(_descriptor.FilePath, d.Name);
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree =
                Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Create(path, allowDuplicates: !d.IsUnique);
            rebuilt[d.Name] = (tree, ordinals, d);
        }
        lock (_compositeIndexSync)
        {
            foreach ((string name, (var tree, int[] ordinals, IndexDescriptor descriptor)) in rebuilt)
            {
                _compositeIndexTrees[name] = tree;
                _compositeIndexColumnIndices[name] = ordinals;
                _compositeIndexDescriptors[name] = descriptor;
            }
        }

        if (_compositeIndexTrees.Count == 0) return;

        Dictionary<string, CompositeIndexBuildTarget> targets =
            new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int[] ordinals) in _compositeIndexColumnIndices)
        {
            IndexDescriptor d = _compositeIndexDescriptors[name];
            targets[name] = new CompositeIndexBuildTarget(_compositeIndexTrees[name], ordinals, d.IsUnique, d.Name);
        }
        await PopulateCompositeIndexesFromScanAsync(targets, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-target state for <see cref="PopulateCompositeIndexesFromScanAsync"/>.
    /// Carries the open tree handle plus the metadata the scan loop needs to
    /// extract row tuples, encode them, and translate duplicate violations
    /// for UNIQUE indexes into a user-facing error.
    /// </summary>
    private readonly record struct CompositeIndexBuildTarget(
        Indexing.BTree.MutableBytes.MutableBPlusTreeBytes Tree,
        int[] Ordinals,
        bool IsUnique,
        string IndexName);

    /// <summary>
    /// Streams every live row through a scan and inserts entries into each
    /// target in <paramref name="targets"/>. Absolute row positions map to
    /// <c>(chunkIndex, rowOffsetInChunk)</c> via the live
    /// <see cref="SourceIndex"/>'s chunk directory; the planner's seek path
    /// resolves entries via the same arithmetic. Without a
    /// <see cref="SourceIndex"/> the loop falls back to default-chunk-size
    /// partitioning. Caller holds <c>_mutationLock</c>. Trees are passed
    /// explicitly (not looked up from <c>_compositeIndexTrees</c>) so
    /// callers can populate a tree before it's published to the visible
    /// state dictionaries — e.g. CREATE INDEX backfill on a populated
    /// table works against the local handle until the scan completes.
    /// UNIQUE-index duplicate violations during the scan throw
    /// <see cref="UniqueIndexViolationException"/>; the caller is expected
    /// to dispose the partial trees + delete sidecars in a catch.
    /// </summary>
    private async Task PopulateCompositeIndexesFromScanAsync(
        IReadOnlyDictionary<string, CompositeIndexBuildTarget> targets,
        CancellationToken ct)
    {
        if (targets.Count == 0) return;

        IReadOnlyList<Indexing.IndexChunk>? chunks = _sourceIndex?.Chunks;
        int defaultChunkSize = Indexing.IndexConstants.DefaultChunkSize;
        long absoluteRow = 0;
        int currentChunk = 0;

        await foreach (RowBatch batch in ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: ct).ConfigureAwait(false))
        {
            try
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    int chunkIndex;
                    long rowOffsetInChunk;
                    if (chunks is not null && chunks.Count > 0)
                    {
                        while (currentChunk + 1 < chunks.Count &&
                               absoluteRow >= chunks[currentChunk].RowOffset + chunks[currentChunk].RowCount)
                        {
                            currentChunk++;
                        }
                        chunkIndex = currentChunk;
                        rowOffsetInChunk = absoluteRow - chunks[chunkIndex].RowOffset;
                    }
                    else
                    {
                        chunkIndex = (int)(absoluteRow / defaultChunkSize);
                        rowOffsetInChunk = absoluteRow % defaultChunkSize;
                    }

                    Row row = batch[r];
                    foreach ((_, CompositeIndexBuildTarget target) in targets)
                    {
                        // Skip rows with a NULL in any covered column —
                        // matches the AppendSession INSERT path. Indexes
                        // can't be probed by NULL equality anyway, and
                        // `IS NULL` falls through to scan. NULLS DISTINCT
                        // (PG default) for UNIQUE indexes: NULL rows are
                        // exempt from the uniqueness check.
                        int[] ordinals = target.Ordinals;
                        DataValue[] tuple = new DataValue[ordinals.Length];
                        bool hasNull = false;
                        for (int p = 0; p < ordinals.Length; p++)
                        {
                            DataValue v = row[ordinals[p]];
                            if (v.IsNull) { hasNull = true; break; }
                            tuple[p] = v;
                        }
                        if (hasNull) continue;

                        byte[] encoded = Indexing.CompositeKeyEncoder.Encode(tuple, batch.Arena);
                        try
                        {
                            target.Tree.Insert(
                                new Indexing.BTree.MutableBytes.BytesIndexEntry(
                                    encoded, chunkIndex, rowOffsetInChunk));
                        }
                        catch (Indexing.BTree.MutableBytes.DuplicateKeyException)
                        {
                            // Backfill of a UNIQUE index across rows that
                            // already contain duplicate tuples. Surface
                            // a user-facing violation so CREATE INDEX
                            // fails cleanly and the outer catch rolls
                            // back the half-built tree before the
                            // visible-state mutation.
                            throw new UniqueIndexViolationException(
                                $"CREATE UNIQUE INDEX '{target.IndexName}': duplicate key across rows " +
                                $"on columns ({string.Join(", ", target.Ordinals.Select(o => _snapshot.Schema.Columns[o].Name))}).");
                        }
                    }
                    absoluteRow++;
                }
            }
            finally
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>
    /// Disposes the named composite-index tree and removes its sidecar
    /// file. Idempotent — DROP INDEX IF EXISTS may reach here with the
    /// tree already disposed if an earlier session crashed mid-cleanup.
    /// </summary>
    /// <remarks>
    /// Holds <see cref="_mutationLock"/> for the whole operation so
    /// concurrent INSERT / UPDATE / CREATE-INDEX paths are serialised
    /// against the drop. The dict-mutation step takes the short
    /// <see cref="_compositeIndexSync"/> lock so a concurrent
    /// <see cref="GetCompositeIndexes"/> snapshots a consistent view —
    /// either with the index present, or without it, never half-removed.
    /// <para>
    /// Disposal of the tree handle happens AFTER both locks are released:
    /// a reader that captured the <c>ICompositeIndex</c> reference before
    /// the drop may still hold it and observe the disposal as an
    /// <c>ObjectDisposedException</c> on the next call. This is the
    /// captured-reference-after-drop race; a true fix needs refcounting
    /// on the tree handle and is filed as a follow-up.
    /// </para>
    /// </remarks>
    internal void DropCompositeIndex(string indexName)
    {
        _mutationLock.Wait();
        try
        {
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes? toDispose;
            lock (_compositeIndexSync)
            {
                if (_compositeIndexTrees.TryGetValue(indexName, out toDispose))
                {
                    _compositeIndexTrees.Remove(indexName);
                    _compositeIndexColumnIndices.Remove(indexName);
                    _compositeIndexDescriptors.Remove(indexName);
                }
            }

            // Dispose outside the dict lock — closing a FileStream can be
            // slow under fsync, no reason to block concurrent readers
            // (they no longer see this entry anyway).
            if (toDispose is not null)
            {
                try { toDispose.Dispose(); } catch { /* best-effort */ }
            }

            string treePath = GetCompositeIndexPath(_descriptor.FilePath, indexName);
            try { if (File.Exists(treePath)) File.Delete(treePath); } catch { /* best-effort */ }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

}
