using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Ingestion;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Statistics;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads v2 <c>.datum</c> files via the <see cref="DatumFileReaderV2"/>.
/// Yields one <see cref="RowBatch"/> per page (1024 rows by default),
/// projecting only the columns the caller asks for. Sidecar-backed
/// payloads (Image, large strings, byte arrays, Struct, …) decode through
/// the catalog's <see cref="SidecarRegistry"/> using
/// <see cref="SidecarStoreId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three-tier zone-map pruning (volume → chapter → page) runs when the
/// query engine supplies a <c>filterHint</c>: volumes that conclusively
/// can't match are skipped wholesale, then chapters within surviving
/// volumes, then pages within surviving chapters. Pages that survive all
/// three tiers are read and emitted. With no filter hint the scan walks
/// every page in order.
/// </para>
/// <para>
/// First-cut limitations: no seek session, no <c>.datum-manifest</c> /
/// <c>.datum-index</c> sidecar discovery (those are v1 sidecars;
/// v2-equivalents are Phase 5+ work).
/// </para>
/// </remarks>
public sealed class DatumFileTableProviderV2 : ITableProvider, IDatumFileTableProvider, IDisposable
{
    private readonly TableDescriptor _descriptor;
    private readonly Pool _pool;
    /// <summary>
    /// Cached <c>.datum-manifest</c> contents, loaded at construction and
    /// refreshed by <see cref="RebuildManifestAsync"/>. <see cref="GetManifest"/>
    /// composes this with the live <see cref="_sourceIndex"/> on every call.
    /// </summary>
    private QueryResultsManifest? _manifest;

    /// <summary>
    /// True when the cached manifest's expensive fields (top-K, quantiles,
    /// histogram, entropy, kind-specific summaries) have drifted from the
    /// live data because a mutation has happened since the last
    /// <c>ANALYZE</c>. The live overlay (PR14h) keeps Count / NullCount /
    /// EstimatedDistinctCount fresh through every snapshot rebuild, but
    /// the cached half ages until <c>ANALYZE</c> rescans.
    /// <see cref="GetManifest"/> propagates this as
    /// <see cref="FeatureManifest.CachedStatsValid"/>=<see langword="false"/>
    /// on every column. Cleared by <see cref="RebuildManifestNoLockAsync"/>.
    /// </summary>
    private bool _cachedStatsStale;
    /// <summary>
    /// Lazily-loaded <c>.datum-index</c> sidecar mapping, captured at
    /// provider construction. Becomes stale after any mutation
    /// (mutation invalidates the source file's fingerprint); the
    /// mutation path nulls these fields out so subsequent
    /// <see cref="GetSourceIndex"/> calls return <see langword="null"/>
    /// and queries fall back to scan instead of using a stale index.
    /// <c>REINDEX</c> rebuilds the sidecar and refreshes these fields.
    /// </summary>
    private MappedSourceIndexSet? _mappedIndexSet;
    private SourceIndex? _sourceIndex;

    /// <summary>
    /// Per-snapshot bundle of read-side state. Mutations (AddColumn /
    /// DropColumn / AppendRows / DeleteRows) build a fresh
    /// <see cref="Snapshot"/> against the post-mutation file and swap
    /// it in atomically. In-flight scans hold a refcount on the
    /// snapshot they captured so the underlying reader / sidecar stays
    /// alive until they finish; the retired snapshot disposes when its
    /// refcount drops to zero.
    /// </summary>
    /// <remarks>
    /// Read paths (<see cref="ScanAsync"/>, <see cref="GetSchema"/>,
    /// <see cref="GetRowCount"/>, …) acquire the current snapshot under
    /// <see cref="_snapshotLock"/>, then release at the end. Mutations
    /// take <see cref="_mutationLock"/> first to serialize against
    /// other mutations, run the static file-level helper, build the
    /// new snapshot, then take <see cref="_snapshotLock"/> for the
    /// install.
    /// </remarks>
    private sealed class Snapshot
    {
        public required DatumFileReaderV2 Reader { get; init; }
        public required SidecarReadStore? Sidecar { get; init; }
        public required Schema Schema { get; init; }
        public required int[] SchemaToFooterIndex { get; init; }
        public required byte[]?[]? ChapterTombstoneBitmaps { get; init; }

        /// <summary>
        /// Active acquisitions outstanding against this snapshot. Set
        /// to 1 at construction (the snapshot is "live" until retired);
        /// readers increment / decrement around their use; mutations
        /// decrement once on retirement. Disposed when count hits 0.
        /// </summary>
        public int RefCount;

        /// <summary>True after a mutation has installed a successor; the snapshot is no longer the current one.</summary>
        public bool Retired;

        public void Dispose()
        {
            Sidecar?.Dispose();
            Reader.Dispose();
        }
    }

    private Snapshot _snapshot;
    private readonly object _snapshotLock = new();

    /// <summary>
    /// Serializes mutations and append sessions across async awaits.
    /// A session holds this semaphore for its entire lifetime
    /// (Begin → Commit / Dispose) so no two writers can overlap on
    /// the same provider in-process. The single-permit count mirrors
    /// the format's single-writer constraint
    /// (<see cref="DatumFileWriterV2.OpenForAppend"/> uses
    /// <c>FileShare.Read</c> to exclude other writers at the OS level).
    /// </summary>
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    /// <summary>
    /// Mutable B+Tree backing PRIMARY KEY uniqueness checks, when the table
    /// has a single-column PK and the <c>.datum-pkindex</c> sidecar exists
    /// alongside the data file. <see langword="null"/> for tables with no PK,
    /// composite PKs (PR10h scope: single-col only), or files predating PR10h.
    /// Opened once at provider construction; updated by
    /// <see cref="DatumAppendSession"/> on commit; closed at provider
    /// dispose. Single-writer through <see cref="_mutationLock"/>.
    /// </summary>
    /// <summary>
    /// On-disk PK index for tables with a PRIMARY KEY. Bytes-keyed for
    /// every PK shape (single-column or composite) — single-column keys
    /// go through <see cref="Indexing.CompositeKeyEncoder.EncodeSingle"/>,
    /// composite keys through <see cref="Indexing.CompositeKeyEncoder.Encode"/>.
    /// Opens on construction when the <c>.datum-pkindex</c> file exists;
    /// <see langword="null"/> for tables without a PK or with no sidecar.
    /// </summary>
    private Indexing.BTree.MutableBytes.MutableBPlusTreeBytes? _pkIndexBytes;

    /// <summary>
    /// Schema column indices of the PRIMARY KEY when <see cref="_pkIndexBytes"/>
    /// is non-null. Empty otherwise. Captured at provider construction;
    /// the catalog rejects ALTER DROP COLUMN of a PK column, so the
    /// indices don't move.
    /// </summary>
    private IReadOnlyList<int> _pkColumnIndices = Array.Empty<int>();

    /// <summary>
    /// Per-column acceleration B+Tree indexes (PR13d), backed by companion
    /// <c>.datum-bptree-{column}</c> page-COW files alongside the data file.
    /// Opened on construction and refreshed by <see cref="RebuildIndexNoLockAsync"/>;
    /// closed on dispose. Read paths route through <see cref="TryGetColumnIndex"/>;
    /// mutations (insert / delete / update) hold the mutation lock and rewrite
    /// the relevant trees in place. Empty when no acceleration is built (e.g. a
    /// fresh table with no rows yet, or before the first index pass).
    /// </summary>
    private Dictionary<string, MutableBPlusTree> _columnTrees = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, MutableBPlusTreeColumnIndex> _columnIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// User-defined secondary indexes (one bytes-keyed tree per
    /// <c>CREATE INDEX</c>). Backed by <c>.datum-cindex-{Name}</c> sidecars
    /// alongside the data file. Multi-value (<c>allowDuplicates: true</c>)
    /// because composite secondary indexes are not uniqueness constraints.
    /// Opened from <see cref="TableDescriptor.Indexes"/> at construction;
    /// extended by <see cref="AddCompositeIndexAsync"/>; closed in
    /// <see cref="Dispose"/>.
    /// </summary>
    private readonly Dictionary<string, Indexing.BTree.MutableBytes.MutableBPlusTreeBytes> _compositeIndexTrees =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ordered list of schema-column indices for each composite index. Used
    /// by the append session to extract tuples per row. Keyed by index name
    /// (matches <see cref="_compositeIndexTrees"/>).
    /// </summary>
    private readonly Dictionary<string, int[]> _compositeIndexColumnIndices =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The declaration of each composite index (the descriptor handed in at
    /// <c>CREATE INDEX</c> time). Retained so that we can answer "which
    /// columns?" without re-resolving names against the schema on every
    /// INSERT.
    /// </summary>
    private readonly Dictionary<string, IndexDescriptor> _compositeIndexDescriptors =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes the provider with the given descriptor and pool. Opens
    /// the v2 <c>.datum</c> file, parses its footer, and (when the file
    /// declares <see cref="DatumFileFlagsV2.HasSidecarReferences"/>)
    /// memory-maps the companion <c>.datum-blob</c> for sidecar reads.
    /// Auto-discovers <c>.datum-manifest</c> and <c>.datum-index</c>
    /// sidecars alongside the source so <see cref="GetManifest"/> /
    /// <see cref="GetSourceIndex"/> return live data. Also opens the
    /// <c>.datum-pkindex</c> sidecar when the schema has a single-column
    /// PK and the file exists (created by <c>CREATE TABLE</c> in PR10h+).
    /// </summary>
    public DatumFileTableProviderV2(TableDescriptor descriptor, Pool pool)
    {
        _descriptor = descriptor;
        _pool = pool;
        _snapshot = OpenSnapshot(descriptor.FilePath);
        _manifest = TryLoadManifest(descriptor);
        (_mappedIndexSet, _sourceIndex) = TryLoadSourceIndex(descriptor);
        TryOpenPrimaryKeyIndex();
        OpenColumnIndexes();
        OpenCompositeIndexes();
    }

    /// <summary>
    /// Discovers and opens every <c>.datum-bptree-{column}</c> file that lives
    /// alongside this provider's data file and matches a column in the live
    /// schema. Files that don't match a current column (DROPped column with
    /// stale tree, renamed column, etc.) are left on disk but not opened —
    /// REINDEX cleans them up.
    /// </summary>
    private void OpenColumnIndexes()
    {
        Schema schema = _snapshot.Schema;

        foreach (ColumnInfo column in schema.Columns)
        {
            string treePath = GetColumnIndexPath(_descriptor.FilePath, column.Name);

            if (!File.Exists(treePath))
            {
                continue;
            }

            try
            {
                MutableBPlusTree tree = MutableBPlusTree.Open(treePath);
                _columnTrees[column.Name] = tree;
                _columnIndexes[column.Name] = new MutableBPlusTreeColumnIndex(tree);
            }
            catch
            {
                // Silently skip a tree that won't open (torn write, version
                // mismatch); the column degrades to scan-based access until
                // REINDEX rebuilds it. Don't crash provider construction.
            }
        }
    }

    /// <summary>
    /// Returns the per-column acceleration B+Tree path companion for a given
    /// data file + column. Column name is sanitized so non-alphanumeric chars
    /// don't collide with the path separator.
    /// </summary>
    internal static string GetColumnIndexPath(string datumPath, string columnName)
    {
        string sanitized = SanitizeColumnNameForPath(columnName);
        return Path.ChangeExtension(datumPath, $".datum-bptree-{sanitized}");
    }

    private static string SanitizeColumnNameForPath(string columnName)
    {
        Span<char> buffer = stackalloc char[columnName.Length];
        for (int i = 0; i < columnName.Length; i++)
        {
            char c = columnName[i];
            buffer[i] = char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_';
        }
        return new string(buffer);
    }

    /// <inheritdoc />
    public bool TryGetColumnIndex(string columnName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Indexing.IColumnIndex? index)
    {
        if (_columnIndexes.TryGetValue(columnName, out MutableBPlusTreeColumnIndex? tree))
        {
            index = tree;
            return true;
        }
        index = null;
        return false;
    }

    /// <summary>
    /// Closes every per-column tree and clears the dictionary. Called from
    /// REINDEX (before a rebuild rewrites the files) and from
    /// <see cref="Dispose"/>. The caller must hold the mutation lock when
    /// invoking this during a rebuild — concurrent readers that captured a
    /// <see cref="MutableBPlusTreeColumnIndex"/> reference before the close
    /// keep working through their stale reference for the duration of the
    /// rebuild window; any new TryGetColumnIndex call after close returns
    /// <c>false</c> until the next <see cref="OpenColumnIndexes"/>.
    /// </summary>
    private void CloseColumnIndexes()
    {
        foreach (MutableBPlusTree tree in _columnTrees.Values)
        {
            tree.Dispose();
        }
        _columnTrees.Clear();
        _columnIndexes.Clear();
    }

    /// <summary>
    /// Opens the <c>.datum-pkindex</c> sidecar when the schema has a PK
    /// and the file exists. Single-column PKs open the typed tree
    /// (<see cref="MutableBPlusTree"/>); composite PKs open the
    /// bytes-keyed tree (<see cref="Indexing.BTree.MutableBytes.MutableBPlusTreeBytes"/>).
    /// No-op for files without a PK index — those tables fall back to
    /// <c>InsertExecutor</c>'s scan-based PK check.
    /// </summary>
    private void TryOpenPrimaryKeyIndex()
    {
        IReadOnlyList<int> pkIndices = _snapshot.Schema.PrimaryKeyColumnIndices;
        if (pkIndices.Count == 0) return;

        string pkIndexPath = GetPrimaryKeyIndexPath(_descriptor.FilePath);
        if (!File.Exists(pkIndexPath)) return;

        _pkIndexBytes = Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Open(pkIndexPath);
        _pkColumnIndices = pkIndices.ToArray();
    }

    /// <summary>
    /// Returns the <c>.datum-pkindex</c> path companion for the given
    /// <c>.datum</c> file path.
    /// </summary>
    internal static string GetPrimaryKeyIndexPath(string datumPath) =>
        Path.ChangeExtension(datumPath, ".datum-pkindex");

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
    /// registers it for INSERT maintenance, and (if the table is non-empty)
    /// rejects — backfill of existing rows is not yet supported in v1.
    /// </summary>
    /// <remarks>
    /// v1 limitation: composite indexes must be created before any data is
    /// inserted. The error message points the user at the workaround
    /// (<c>DROP TABLE</c> → <c>CREATE TABLE</c> → <c>CREATE INDEX</c> →
    /// <c>INSERT</c>). Backfill via <c>ScanAsync</c> + per-row tuple
    /// encoding lands in a follow-up.
    /// </remarks>
    internal Task AddCompositeIndexAsync(IndexDescriptor descriptor)
    {
        if (_compositeIndexTrees.ContainsKey(descriptor.Name))
        {
            throw new InvalidOperationException(
                $"Composite index '{descriptor.Name}' is already open on table '{_descriptor.Name}'.");
        }

        if (GetRowCount() > 0)
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{descriptor.Name}' on a populated table is not yet supported. " +
                "v1 limitation: composite indexes must be created before any rows are inserted. " +
                "Workaround: DROP TABLE, recreate, CREATE INDEX, then INSERT.");
        }

        int[]? ordinals = TryResolveCompositeIndexOrdinals(_snapshot.Schema, descriptor);
        if (ordinals is null)
        {
            // Caller (TableCatalog.ApplyCreateIndexAsync) already validated
            // column existence; this is a defense-in-depth check.
            throw new InvalidOperationException(
                $"CREATE INDEX '{descriptor.Name}': one or more columns no longer exist on the schema.");
        }

        string treePath = GetCompositeIndexPath(_descriptor.FilePath, descriptor.Name);
        Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree =
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Create(treePath, allowDuplicates: true);

        _compositeIndexTrees[descriptor.Name] = tree;
        _compositeIndexColumnIndices[descriptor.Name] = ordinals;
        _compositeIndexDescriptors[descriptor.Name] = descriptor;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes the named composite-index tree and removes its sidecar
    /// file. Idempotent — DROP INDEX IF EXISTS may reach here with the
    /// tree already disposed if an earlier session crashed mid-cleanup.
    /// </summary>
    internal void DropCompositeIndex(string indexName)
    {
        if (_compositeIndexTrees.TryGetValue(indexName, out Indexing.BTree.MutableBytes.MutableBPlusTreeBytes? tree))
        {
            try { tree.Dispose(); } catch { /* best-effort */ }
            _compositeIndexTrees.Remove(indexName);
            _compositeIndexColumnIndices.Remove(indexName);
            _compositeIndexDescriptors.Remove(indexName);
        }

        string treePath = GetCompositeIndexPath(_descriptor.FilePath, indexName);
        try { if (File.Exists(treePath)) File.Delete(treePath); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Opens a fresh <see cref="DatumFileReaderV2"/> + sidecar for
    /// <paramref name="path"/> and bundles the derived schema /
    /// tombstone state into a <see cref="Snapshot"/> with refcount 1.
    /// </summary>
    private static Snapshot OpenSnapshot(string path)
    {
        DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);
        SidecarReadStore? sidecar = null;
        try
        {
            sidecar = TryOpenSidecar(path, reader);
            (Schema schema, int[] schemaToFooterIndex) = BuildSchema(reader.Footer);
            byte[]?[]? bitmaps = reader.LoadChapterTombstoneBitmaps();
            return new Snapshot
            {
                Reader = reader,
                Sidecar = sidecar,
                Schema = schema,
                SchemaToFooterIndex = schemaToFooterIndex,
                ChapterTombstoneBitmaps = bitmaps,
                RefCount = 1,
            };
        }
        catch
        {
            sidecar?.Dispose();
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Captures the current snapshot and increments its refcount.
    /// Callers must pass the returned reference back to
    /// <see cref="ReleaseSnapshot"/> when done.
    /// </summary>
    private Snapshot AcquireSnapshot()
    {
        lock (_snapshotLock)
        {
            _snapshot.RefCount++;
            return _snapshot;
        }
    }

    /// <summary>
    /// Decrements <paramref name="snapshot"/>'s refcount and disposes it
    /// if it has been retired and is now unreferenced.
    /// </summary>
    private void ReleaseSnapshot(Snapshot snapshot)
    {
        lock (_snapshotLock)
        {
            if (--snapshot.RefCount == 0 && snapshot.Retired)
            {
                snapshot.Dispose();
            }
        }
    }

    /// <summary>
    /// Installs <paramref name="next"/> as the current snapshot,
    /// retiring the previous one. The previous snapshot disposes
    /// immediately if no scans hold a refcount; otherwise the last
    /// scan releases will dispose it. Caller must hold
    /// <see cref="_mutationLock"/>.
    /// </summary>
    private void SwapSnapshot(Snapshot next)
    {
        lock (_snapshotLock)
        {
            Snapshot old = _snapshot;
            old.Retired = true;
            _snapshot = next;

            // The retired snapshot's initial RefCount=1 represents
            // "this is the current snapshot". Drop that ref now;
            // remaining refs (if any) belong to in-flight scans.
            if (--old.RefCount == 0)
            {
                old.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public IBlobSource? Sidecar
    {
        get
        {
            // Reads the current snapshot's sidecar without bumping the
            // refcount. Catalog wiring calls this once at provider-add
            // time to register with SidecarRegistry; the registered
            // source is later swapped via SidecarRegistry.UpdateAt
            // when AppendRows grows the .datum-blob.
            lock (_snapshotLock) { return _snapshot.Sidecar; }
        }
    }

    /// <inheritdoc/>
    public byte SidecarStoreId { get; set; }

    /// <inheritdoc/>
    public SidecarRegistry? SidecarRegistry { get; set; }

    /// <inheritdoc/>
    public void EnsureTypeTableLoaded(DatumIngest.Execution.ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Snapshot s = AcquireSnapshot();
        try
        {
            IReadOnlyList<TypeTableEntryV5> entries = s.Reader.Footer.TypeTable;
            if (entries.Count == 0) return;

            IBlobSource? sidecar = s.Sidecar;
            if (sidecar is null)
            {
                throw new InvalidDataException(
                    $"Table '{_descriptor.Name}' declares a type table with {entries.Count} entries " +
                    "but has no sidecar to read descriptor blobs from. The file is corrupt or " +
                    "missing its companion .datum-blob.");
            }

            // Build the on-disk → runtime map and register it on the per-query
            // TypeIdTranslations. No per-provider caching of runtime ids — concurrent
            // queries with different TypeRegistry instances would race over a shared
            // cache. Per-column translation happens at decoder-open time using the
            // typeIdTranslations argument threaded through ScanAsync.
            Dictionary<ushort, ushort> onDiskToRuntime = new(entries.Count);
            foreach (TypeTableEntryV5 entry in entries)
            {
                ReadOnlySpan<byte> blob = sidecar.Read(entry.SidecarOffset, entry.DescriptorLength);
                int runtimeId = TypeDescriptorSerializer.DeserializeAndIntern(blob, context.Types);
                onDiskToRuntime[entry.OnDiskTypeId] = checked((ushort)runtimeId);
            }

            context.TypeIdTranslations.Register(SidecarStoreId, onDiskToRuntime);
        }
        finally { ReleaseSnapshot(s); }
    }

    /// <inheritdoc/>
    public string Name => _descriptor.Name;

    /// <inheritdoc/>
    public bool Seekable => true;

    /// <inheritdoc/>
    public long GetRowCount()
    {
        Snapshot s = AcquireSnapshot();
        try { return s.Reader.TotalRowCount; }
        finally { ReleaseSnapshot(s); }
    }

    /// <inheritdoc/>
    public Schema GetSchema()
    {
        Snapshot s = AcquireSnapshot();
        try { return s.Schema; }
        finally { ReleaseSnapshot(s); }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// PR14h: when both a cached <c>.datum-manifest</c> and a live
    /// <c>.datum-index</c> are available, returns a composed manifest where
    /// per-column live fields (Count / NullCount / NullRatio / EstimatedDistinctCount)
    /// are recomputed from the index's per-chunk statistics on every call.
    /// Without an index the cached manifest is returned verbatim.
    /// </remarks>
    public QueryResultsManifest? GetManifest()
    {
        QueryResultsManifest? cached = _manifest;
        SourceIndex? index = _sourceIndex;
        if (cached is null) return null;

        QueryResultsManifest result = index is null
            ? cached
            : LiveManifestOverlay.Compose(cached, index);

        if (_cachedStatsStale)
        {
            result = LiveManifestOverlay.WithCachedStatsValid(result, cachedStatsValid: false);
        }

        return result;
    }

    /// <inheritdoc/>
    public SourceIndex? GetSourceIndex() => _sourceIndex;


    /// <inheritdoc/>
    public IndexValidity GetIndexValidity()
    {
        // Live-loaded index → Valid. The constructor + RebuildIndex
        // (PR12) only set _sourceIndex when TryLoadSourceIndex confirmed
        // the fingerprint matches and the IDXT tail validated.
        if (_sourceIndex is not null) return IndexValidity.Valid;

        // No live index in memory. Check whether a (now-invalid) file
        // still sits on disk so users can tell "needs REINDEX" apart
        // from "never had one."
        string indexPath = PathDetector.GetSidecarBasePath(_descriptor.FilePath) + ".datum-index";
        return File.Exists(indexPath) ? IndexValidity.Stale : IndexValidity.Missing;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        TypeIdTranslationTable? typeIdTranslations = null)
    {
        Snapshot s = AcquireSnapshot();
        try
        {
            if (s.Reader.Footer.Columns.Count == 0)
            {
                yield break;
            }

            // Resolve the column projection. Maps lookup-index → schema-index
            // so the scan only opens decoders for projected columns.
            ColumnLookup columnLookup = ResolveProjection(s.Schema, requiredColumns);
            int projectedCount = columnLookup.Count;
            if (projectedCount == 0)
            {
                yield break;
            }

            // All columns share the same page count and per-page row count
            // because the writer flushes every encoder at the same row cadence.
            // Use SchemaToFooterIndex[0] (the first live column) as the probe —
            // tombstoned columns at index 0 are skipped from the live schema.
            int pageCount = s.Reader.Footer.Columns[s.SchemaToFooterIndex[0]].Pages.Count;

            // schemaIndices[i] = footer column index for the i-th projected
            // column. The columnLookup gives us the index into the live
            // (filtered) schema; we translate via SchemaToFooterIndex so all
            // downstream Footer.Columns[..] accesses land on the right block.
            int[] schemaIndices = new int[projectedCount];
            for (int i = 0; i < projectedCount; i++)
            {
                int filteredIndex = columnLookup.GetSchemaColumnIndex(i);
                schemaIndices[i] = s.SchemaToFooterIndex[filteredIndex];
            }

            // Build a filter-column → schema-index lookup once. Skip the
            // pruning path entirely when the filter references no columns we
            // have stats for.
            Dictionary<string, int>? filterSchemaIndex = filterHint is null
                ? null
                : BuildFilterColumnIndex(s.Schema, filterHint);

            // Stats arena for boxed min/max values during partition checks.
            // Reused across all skip evaluations; values are tiny (numerics,
            // short strings) so growth is bounded.
            Arena statsArena = new();

            IPageDecoderV2[] decoders = new IPageDecoderV2[projectedCount];

            foreach (int pageIndex in EnumerateScanablePages(s, pageCount, filterHint, filterSchemaIndex, statsArena))
            {
                cancellationToken.ThrowIfCancellationRequested();

                int rowCount = s.Reader.Footer.Columns[schemaIndices[0]].Pages[pageIndex].RowCount;
                if (rowCount == 0)
                {
                    continue;
                }

                // One batch per page. batch.Arena is the eager store the
                // decoders use to materialize Struct field arrays — values
                // stored against the batch's own arena resolve cleanly through
                // standard accessors downstream.
                RowBatch batch = _pool.RentRowBatch(columnLookup, rowCount, targetArena);

                for (int i = 0; i < projectedCount; i++)
                {
                    int footerIndex = schemaIndices[i];

                    // Per-column on-disk StructTypeId → runtime id translation,
                    // done once per page-decoder open against the *caller's*
                    // translator. No shared mutable state on the provider, so
                    // concurrent queries reading the same file each get their
                    // own registry's runtime ids without interference.
                    ushort columnRuntimeStructTypeId = 0;
                    if (typeIdTranslations is not null
                        && s.Reader.Footer.Columns[footerIndex].StructTypeId is { } onDiskId)
                    {
                        columnRuntimeStructTypeId =
                            typeIdTranslations.Translate(SidecarStoreId, onDiskId);
                    }

                    decoders[i] = s.Reader.OpenPageDecoder(
                        columnIndex: footerIndex,
                        pageIndex: pageIndex,
                        sidecarStoreId: SidecarStoreId,
                        sidecarSource: s.Sidecar,
                        eagerStore: batch.Arena,
                        columnRuntimeStructTypeId: columnRuntimeStructTypeId);
                }

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Skip soft-deleted rows. Fast-paths when the file has
                    // no tombstones (IsRowDeleted returns false without
                    // touching the bitmap array).
                    if (IsRowDeleted(s, pageIndex, rowIndex)) continue;

                    DataValue[] values = _pool.RentDataValues(projectedCount);
                    for (int i = 0; i < projectedCount; i++)
                    {
                        values[i] = decoders[i].ReadValue(rowIndex);
                    }
                    batch.Add(values);
                }

                // Skip empty batches — a page where every row was tombstoned
                // produces a zero-length batch that consumers might interpret
                // as end-of-stream. Yield only batches with at least one row.
                if (batch.Count > 0)
                {
                    yield return batch;
                }
                else
                {
                    _pool.ReturnRowBatch(batch);
                }
            }
        }
        finally
        {
            ReleaseSnapshot(s);
        }
    }

    /// <summary>
    /// Yields the page indices that survive zone-map pruning, in scan
    /// order. With no filter hint, this is just <c>0..pageCount-1</c>.
    /// With a filter, the three-tier hierarchy (volume → chapter → page)
    /// is walked top-down: a volume that the predicate provably can't
    /// match short-circuits all its chapters; same for chapters and
    /// their pages.
    /// </summary>
    private static IEnumerable<int> EnumerateScanablePages(
        Snapshot s,
        int pageCount,
        Expression? filterHint,
        Dictionary<string, int>? filterSchemaIndex,
        Arena statsArena)
    {
        if (filterHint is null || filterSchemaIndex is null || filterSchemaIndex.Count == 0)
        {
            for (int p = 0; p < pageCount; p++) yield return p;
            yield break;
        }

        int pagesPerChapter = DatumFormatV2.PagesPerChapter;
        int chaptersPerVolume = DatumFormatV2.ChaptersPerVolume;
        // Probe the first live column (skipping tombstoned slots) for
        // page-count / chapter-count / volume-count metadata. All live
        // columns share these counts.
        int probeFooterIdx = s.SchemaToFooterIndex[0];
        int chapterCount = s.Reader.Footer.Columns[probeFooterIdx].ChapterZoneMaps.Count;

        bool hasVolumes = (s.Reader.Header.Flags & DatumFileFlagsV2.HasVolumeZoneMaps) != 0
            && s.Reader.Footer.Columns[probeFooterIdx].VolumeZoneMaps is { Count: > 0 };

        // When the file emits volume zone maps walk volumes; otherwise
        // the volume tier is collapsed and we walk chapters directly.
        int volumeIterCount = hasVolumes ? s.Reader.Footer.Columns[probeFooterIdx].VolumeZoneMaps!.Count : 1;

        for (int v = 0; v < volumeIterCount; v++)
        {
            if (hasVolumes && CanSkipVolume(s, v, filterHint, filterSchemaIndex, statsArena))
            {
                continue;
            }

            int chapterStart = hasVolumes ? v * chaptersPerVolume : 0;
            int chapterEnd = hasVolumes
                ? Math.Min(chapterStart + chaptersPerVolume, chapterCount)
                : chapterCount;

            for (int c = chapterStart; c < chapterEnd; c++)
            {
                if (CanSkipChapter(s, c, filterHint, filterSchemaIndex, statsArena))
                {
                    continue;
                }

                int pageStart = c * pagesPerChapter;
                int pageEnd = Math.Min(pageStart + pagesPerChapter, pageCount);

                for (int p = pageStart; p < pageEnd; p++)
                {
                    if (CanSkipPage(s, p, filterHint, filterSchemaIndex, statsArena))
                    {
                        continue;
                    }
                    yield return p;
                }
            }
        }
    }

    private static bool CanSkipVolume(Snapshot s, int volumeIndex, Expression filter, Dictionary<string, int> filterSchemaIndex, Arena arena)
    {
        // Volume row count = sum of chapter row counts in this volume.
        // Chapter row count = sum of page row counts in that chapter.
        // We only need a bound; passing pageCount * pageSize is fine as a
        // ceiling for the predicate evaluator's null-vs-row arithmetic.
        long rowCount = ComputeVolumeRowCount(s, volumeIndex);
        Dictionary<string, ColumnStatisticsRange> stats = new(filterSchemaIndex.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int schemaIdx) in filterSchemaIndex)
        {
            int footerIdx = s.SchemaToFooterIndex[schemaIdx];
            DatumZoneMap zoneMap = s.Reader.Footer.Columns[footerIdx].VolumeZoneMaps![volumeIndex];
            stats[name] = MakeRange(zoneMap, rowCount, arena);
        }
        using ColumnStatisticsRangeLookup lookup = new(stats);
        return StatisticsPredicateEvaluator.CanSkipPartition(filter, lookup, arena);
    }

    private static bool CanSkipChapter(Snapshot s, int chapterIndex, Expression filter, Dictionary<string, int> filterSchemaIndex, Arena arena)
    {
        long rowCount = ComputeChapterRowCount(s, chapterIndex);
        Dictionary<string, ColumnStatisticsRange> stats = new(filterSchemaIndex.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int schemaIdx) in filterSchemaIndex)
        {
            int footerIdx = s.SchemaToFooterIndex[schemaIdx];
            DatumZoneMap zoneMap = s.Reader.Footer.Columns[footerIdx].ChapterZoneMaps[chapterIndex];
            stats[name] = MakeRange(zoneMap, rowCount, arena);
        }
        using ColumnStatisticsRangeLookup lookup = new(stats);
        return StatisticsPredicateEvaluator.CanSkipPartition(filter, lookup, arena);
    }

    private static bool CanSkipPage(Snapshot s, int pageIndex, Expression filter, Dictionary<string, int> filterSchemaIndex, Arena arena)
    {
        int rowCount = s.Reader.Footer.Columns[s.SchemaToFooterIndex[0]].Pages[pageIndex].RowCount;
        Dictionary<string, ColumnStatisticsRange> stats = new(filterSchemaIndex.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int schemaIdx) in filterSchemaIndex)
        {
            int footerIdx = s.SchemaToFooterIndex[schemaIdx];
            DatumZoneMap? zoneMap = s.Reader.Footer.Columns[footerIdx].Pages[pageIndex].ZoneMap;
            // Page-level zone maps are null for non-comparable kinds —
            // skip those columns rather than synthesizing fake stats.
            if (zoneMap is null) continue;
            stats[name] = MakeRange(zoneMap, rowCount, arena);
        }
        if (stats.Count == 0) return false;
        using ColumnStatisticsRangeLookup lookup = new(stats);
        return StatisticsPredicateEvaluator.CanSkipPartition(filter, lookup, arena);
    }

    /// <summary>
    /// Materializes a <see cref="DatumZoneMap"/> as a
    /// <see cref="ColumnStatisticsRange"/>, lifting the boxed min/max
    /// into <see cref="DataValue"/>s landed in <paramref name="arena"/>.
    /// </summary>
    private static ColumnStatisticsRange MakeRange(DatumZoneMap zoneMap, long rowCount, Arena arena) =>
        new(
            DataValueComparer.MakeFromBoxed(zoneMap.Kind, zoneMap.Minimum, arena),
            DataValueComparer.MakeFromBoxed(zoneMap.Kind, zoneMap.Maximum, arena),
            zoneMap.NullCount,
            rowCount);

    private static long ComputeChapterRowCount(Snapshot s, int chapterIndex)
    {
        // Use the first live (non-tombstoned) column as the probe — its
        // page count and per-page row counts mirror every other live
        // column's by construction (the writer flushes all encoders at
        // the same row cadence). Tombstoned columns are skipped from
        // SchemaToFooterIndex so we never address one here.
        int pagesPerChapter = DatumFormatV2.PagesPerChapter;
        int pageStart = chapterIndex * pagesPerChapter;
        var pages = s.Reader.Footer.Columns[s.SchemaToFooterIndex[0]].Pages;
        int pageEnd = Math.Min(pageStart + pagesPerChapter, pages.Count);
        long total = 0;
        for (int p = pageStart; p < pageEnd; p++) total += pages[p].RowCount;
        return total;
    }

    private static long ComputeVolumeRowCount(Snapshot s, int volumeIndex)
    {
        int chaptersPerVolume = DatumFormatV2.ChaptersPerVolume;
        int chapterStart = volumeIndex * chaptersPerVolume;
        int chapterCount = s.Reader.Footer.Columns[s.SchemaToFooterIndex[0]].ChapterZoneMaps.Count;
        int chapterEnd = Math.Min(chapterStart + chaptersPerVolume, chapterCount);
        long total = 0;
        for (int c = chapterStart; c < chapterEnd; c++) total += ComputeChapterRowCount(s, c);
        return total;
    }

    /// <summary>
    /// Builds a case-insensitive dictionary mapping every column name
    /// referenced in <paramref name="filter"/> to its schema column
    /// index. Columns not present in the schema are silently dropped
    /// (the predicate evaluator falls back to "do not skip" for those).
    /// </summary>
    private static Dictionary<string, int>? BuildFilterColumnIndex(Schema schema, Expression filter)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(filter))
        {
            if (result.ContainsKey(columnName)) continue;
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                if (string.Equals(schema.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    result[columnName] = i;
                    break;
                }
            }
        }
        return result.Count > 0 ? result : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Each session opens a fresh <see cref="DatumFileReaderV2"/> against
    /// the same path so multiple concurrent sessions don't contend for
    /// <see cref="FileStream.Position"/> on a shared reader. The sidecar
    /// is similarly re-opened per session — mmap views are read-only and
    /// shareable across processes, so two views of the same file don't
    /// cost much. Resolved projection metadata is captured once and kept
    /// for the session's lifetime.
    /// </remarks>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns, Arena? targetArena = null)
    {
        // Open the session reader first, then derive schema /
        // schemaToFooterIndex from THAT reader's footer (rather than from
        // the provider's snapshot). This sidesteps any race window
        // between a concurrent mutation finishing the file write and the
        // provider swapping its snapshot — the session always sees a
        // self-consistent view of whatever the file currently is.
        DatumFileReaderV2 sessionReader = DatumFileReaderV2.Open(_descriptor.FilePath);
        SidecarReadStore? sessionSidecar = null;
        try
        {
            sessionSidecar = TryOpenSidecar(_descriptor.FilePath, sessionReader);
            (Schema sessionSchema, int[] sessionSchemaToFooterIndex) = BuildSchema(sessionReader.Footer);

            ColumnLookup columnLookup = ResolveProjection(sessionSchema, requiredColumns);
            int projectedCount = columnLookup.Count;
            int[] schemaIndices = new int[projectedCount];
            for (int i = 0; i < projectedCount; i++)
            {
                int filteredIndex = columnLookup.GetSchemaColumnIndex(i);
                schemaIndices[i] = sessionSchemaToFooterIndex[filteredIndex];
            }

            return new DatumFileSeekSessionV2(
                _pool, sessionReader, sessionSidecar, columnLookup, schemaIndices, SidecarStoreId, targetArena);
        }
        catch
        {
            sessionSidecar?.Dispose();
            sessionReader.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _mappedIndexSet?.Dispose();
        _pkIndexBytes?.Dispose();
        _pkIndexBytes = null;
        CloseColumnIndexes();
        CloseCompositeIndexes();

        // Dispose whichever snapshot is current; in-flight scans hold
        // their own refs so they remain safe until they finish.
        // (Disposing the provider while scans are mid-iteration is a
        // caller-side bug; this is best-effort cleanup.)
        Snapshot toDispose;
        lock (_snapshotLock)
        {
            toDispose = _snapshot;
            _snapshot.Retired = true;
            if (--_snapshot.RefCount > 0)
            {
                // Live readers still hold the snapshot; let the last
                // release dispose it.
                return;
            }
        }
        toDispose.Dispose();
    }

    /// <inheritdoc/>
    public IPrimaryKeyLookup? GetPrimaryKeyLookup()
        => _pkIndexBytes is null ? null : new MutableBPlusTreeBytesPrimaryKeyLookup(_pkIndexBytes);

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the row at
    /// <c>(pageIndex, rowInPage)</c> has been soft-deleted — its bit
    /// is set in the chapter tombstone bitmap. Fast-paths when the
    /// file has no tombstones at all (<c>s.ChapterTombstoneBitmaps</c>
    /// is <see langword="null"/>) or no tombstones in the row's
    /// chapter (<c>s.ChapterTombstoneBitmaps[c]</c> is
    /// <see langword="null"/>).
    /// </summary>
    private static bool IsRowDeleted(Snapshot s, int pageIndex, int rowInPage)
    {
        if (s.ChapterTombstoneBitmaps is null) return false;

        int pageSize = s.Reader.Header.PageSize;
        int chapterIndex = pageIndex / DatumFormatV2.PagesPerChapter;
        if (chapterIndex >= s.ChapterTombstoneBitmaps.Length) return false;

        byte[]? bitmap = s.ChapterTombstoneBitmaps[chapterIndex];
        if (bitmap is null) return false;

        int pageOffsetInChapter = pageIndex % DatumFormatV2.PagesPerChapter;
        int rowInChapter = pageOffsetInChapter * pageSize + rowInPage;
        if ((uint)rowInChapter >= (uint)(bitmap.Length * 8)) return false;

        int byteIndex = rowInChapter >> 3;
        int bitMask = 1 << (rowInChapter & 7);
        return (bitmap[byteIndex] & bitMask) != 0;
    }

    /// <summary>
    /// Builds an engine-facing <see cref="Schema"/> from the v2 footer's
    /// column descriptors, filtering out soft-dropped (tombstoned)
    /// columns. The descriptor's <c>IsArray</c> flag rides through to
    /// <see cref="ColumnInfo.IsArray"/>; per-element kind is the
    /// descriptor's <c>Kind</c> directly (typed-array convention — no
    /// separate <c>ArrayElementKind</c> wrapper).
    /// </summary>
    /// <returns>
    /// The filtered schema and a parallel array mapping each filtered
    /// schema index to its position in <see cref="FooterV2.Columns"/>.
    /// Callers use the mapping when consuming page directories / zone
    /// maps directly from the footer.
    /// </returns>
    private static (Schema Schema, int[] SchemaToFooterIndex) BuildSchema(FooterV2 footer)
    {
        // Index DEFAULT entries by footer column index for O(1) lookup
        // during schema construction. Most columns have no default, so a
        // dictionary stays cheap.
        Dictionary<ushort, string>? defaultsByIndex = null;
        if (footer.Prologue.ColumnDefaults.Count > 0)
        {
            defaultsByIndex = new Dictionary<ushort, string>(footer.Prologue.ColumnDefaults.Count);
            foreach (ColumnDefaultV4 entry in footer.Prologue.ColumnDefaults)
            {
                defaultsByIndex[entry.ColumnIndex] = entry.SqlFragment;
            }
        }

        // Same shape for computed columns (v6+). The footer carries a
        // sibling block, parsed identically to defaults.
        Dictionary<ushort, string>? computedsByIndex = null;
        if (footer.ColumnComputeds.Count > 0)
        {
            computedsByIndex = new Dictionary<ushort, string>(footer.ColumnComputeds.Count);
            foreach (ColumnComputedV4 entry in footer.ColumnComputeds)
            {
                computedsByIndex[entry.ColumnIndex] = entry.SqlFragment;
            }
        }

        // IDENTITY spec — at most one column carries it, identified by
        // footer-column index. Resolve to the schema-column index after
        // tombstone-skipping below.
        IdentitySpec? identitySpec = null;
        int identityFooterIndex = footer.Prologue.IdentityColumnIndex;
        if (identityFooterIndex >= 0)
        {
            identitySpec = new IdentitySpec(
                footer.Prologue.IdentitySeed,
                footer.Prologue.IdentityStep,
                footer.Prologue.IdentityAcceptUserValues);
        }

        // PRIMARY KEY — set of footer column indices for the per-column
        // IsPrimaryKey flag. Schema-level ordered list is derived after
        // we know each column's schema-position.
        HashSet<ushort>? pkFooterIndexSet = null;
        if (footer.Prologue.PrimaryKeyColumnIndices.Count > 0)
        {
            pkFooterIndexSet = new HashSet<ushort>(footer.Prologue.PrimaryKeyColumnIndices);
        }

        List<ColumnInfo> columns = new(footer.Columns.Count);
        List<int> indices = new(footer.Columns.Count);
        // footerIndexToSchemaIndex[i] = schema-position of footer column i
        // (or -1 when tombstoned). Used to translate the prologue's
        // footer-indexed PK list into schema-indexed form.
        int[] footerIndexToSchemaIndex = new int[footer.Columns.Count];
        for (int i = 0; i < footer.Columns.Count; i++)
        {
            ColumnDescriptorV2 d = footer.Columns[i].Descriptor;
            if (d.IsTombstoned)
            {
                footerIndexToSchemaIndex[i] = -1;
                continue;
            }

            Expression? defaultExpression = null;
            if (defaultsByIndex is not null && defaultsByIndex.TryGetValue((ushort)i, out string? fragment))
            {
                defaultExpression = ParseDefaultFragment(fragment, d.Name);
            }

            Expression? computedExpression = null;
            if (computedsByIndex is not null
                && computedsByIndex.TryGetValue((ushort)i, out string? computedFragment))
            {
                // Same re-parse path as DEFAULT — the catalog persisted the
                // expression via QueryExplainer.FormatExpression; we wrap it
                // in SELECT and pull out the projected expression.
                computedExpression = ParseDefaultFragment(computedFragment, d.Name);
            }

            footerIndexToSchemaIndex[i] = columns.Count;
            columns.Add(new ColumnInfo(d.Name, d.Kind, d.IsNullable)
            {
                IsArray = d.IsArray,
                DefaultExpression = defaultExpression,
                Identity = i == identityFooterIndex ? identitySpec : null,
                IsPrimaryKey = pkFooterIndexSet is not null && pkFooterIndexSet.Contains((ushort)i),
                ComputedExpression = computedExpression,
            });
            indices.Add(i);
        }

        // Translate the prologue's footer-indexed PK list to schema-indexed.
        IReadOnlyList<int>? schemaPkIndices = null;
        if (footer.Prologue.PrimaryKeyColumnIndices.Count > 0)
        {
            int[] translated = new int[footer.Prologue.PrimaryKeyColumnIndices.Count];
            for (int p = 0; p < translated.Length; p++)
            {
                ushort footerIdx = footer.Prologue.PrimaryKeyColumnIndices[p];
                if (footerIdx >= footerIndexToSchemaIndex.Length || footerIndexToSchemaIndex[footerIdx] < 0)
                {
                    // PK column was tombstoned — shouldn't happen because
                    // ALTER DROP COLUMN of a PK column is rejected; surface
                    // as a corruption-style error.
                    throw new InvalidDataException(
                        $"PRIMARY KEY references footer column {footerIdx} but it is missing or tombstoned.");
                }
                translated[p] = footerIndexToSchemaIndex[footerIdx];
            }
            schemaPkIndices = translated;
        }

        return (new Schema(columns.ToArray(), schemaPkIndices), indices.ToArray());
    }

    /// <summary>
    /// Re-parses a persisted DEFAULT SQL fragment back into an
    /// <see cref="Expression"/>. Wraps the fragment in a synthetic
    /// <c>SELECT</c> and pulls the first column expression — same trick
    /// the catalog uses for UDF default-parameter persistence.
    /// </summary>
    private static Expression ParseDefaultFragment(string fragment, string columnName)
    {
        try
        {
            QueryExpression q = SqlParser.Parse($"SELECT {fragment}");
            return ((SelectQueryExpression)q).Statement.Columns[0].Expression;
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException)
        {
            throw new InvalidDataException(
                $"Failed to re-parse DEFAULT for column '{columnName}': '{fragment}' — {ex.Message}");
        }
    }

    private static ColumnLookup ResolveProjection(Schema schema, IReadOnlySet<string>? requiredColumns)
    {
        if (requiredColumns is null)
        {
            return new ColumnLookup(schema.Columns);
        }

        // Contract: requiredColumns is a subset of the table's schema.
        // Walk the schema once, building the projection in schema order and
        // tracking which required names we've matched. Any remaining unmatched
        // name is a planner contract violation — surface it explicitly rather
        // than silently dropping the column or letting ColumnLookup crash on
        // a null dictionary key.
        (int index, int schemaIndex, string name)[] projected =
            new (int, int, string)[requiredColumns.Count];
        HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            string columnName = schema.Columns[i].Name;
            if (requiredColumns.Contains(columnName))
            {
                projected[index] = (index, i, columnName);
                resolved.Add(columnName);
                index++;
            }
        }

        if (resolved.Count != requiredColumns.Count)
        {
            IEnumerable<string> missing = requiredColumns.Where(n => !resolved.Contains(n));
            throw new InvalidOperationException(
                $"requiredColumns contains names not present in the table schema: " +
                $"[{string.Join(", ", missing)}]. The projection-pushdown contract " +
                $"requires every name in requiredColumns to resolve to a schema column; " +
                $"this indicates a planner bug (e.g. a synthetic name leaked through " +
                $"CollectAllReferencedColumns).");
        }

        return new ColumnLookup(projected);
    }

    /// <summary>
    /// Opens the companion <c>.datum-blob</c> sidecar when the v2 file
    /// declares <see cref="DatumFileFlagsV2.HasSidecarReferences"/>. The
    /// fingerprint is read from the sidecar's own header (v2 doesn't
    /// store the fingerprint in the .datum footer yet — see follow-up
    /// in <c>project_sidecar_integrity_hash.md</c>); the
    /// <see cref="SidecarReadStore"/>'s fingerprint check therefore only
    /// validates that the sidecar header itself is consistent.
    /// </summary>
    private static SidecarReadStore? TryOpenSidecar(string datumPath, DatumFileReaderV2 reader)
    {
        if ((reader.Header.Flags & DatumFileFlagsV2.HasSidecarReferences) == 0)
        {
            return null;
        }

        string sidecarPath = Path.ChangeExtension(datumPath, SidecarConstants.FileExtension);
        if (!File.Exists(sidecarPath))
        {
            throw new FileNotFoundException(
                $".datum file '{datumPath}' declares HasSidecarReferences but the companion sidecar " +
                $"'{sidecarPath}' is missing.", sidecarPath);
        }

        ulong fingerprint = ReadSidecarFingerprint(sidecarPath);
        return new SidecarReadStore(sidecarPath, fingerprint);
    }

    /// <summary>
    /// Reads the 8-byte fingerprint at offset 16 of the sidecar header
    /// (after the 8-byte magic + 4-byte version + 4-byte reserved fields,
    /// per <see cref="SidecarConstants"/>'s documented layout).
    /// </summary>
    private static ulong ReadSidecarFingerprint(string sidecarPath)
    {
        using FileStream fs = File.OpenRead(sidecarPath);
        Span<byte> hdr = stackalloc byte[SidecarConstants.HeaderSize];
        fs.ReadExactly(hdr);
        return BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(16, 8));
    }

    /// <summary>
    /// Loads a <c>.datum-manifest</c> sidecar alongside the source file.
    /// Returns the per-table <see cref="QueryResultsManifest"/> matching
    /// this provider's table name, or <see langword="null"/> when the
    /// sidecar is absent or has no entry for this table. Mirrors the v1
    /// loader; the manifest format is shared between v1 and v2.
    /// </summary>
    private static QueryResultsManifest? TryLoadManifest(TableDescriptor descriptor)
    {
        string path = PathDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-manifest";
        if (!File.Exists(path))
        {
            return null;
        }

        string json = File.ReadAllText(path);
        SourceManifest? sourceManifest = ManifestSerializer.Deserialize(json);
        if (sourceManifest is null)
        {
            return null;
        }

        return ResolveSidecarEntry(sourceManifest.Tables, descriptor.Name, descriptor.FilePath);
    }

    /// <summary>
    /// Memory-maps a <c>.datum-index</c> sidecar alongside the source
    /// file. Returns the owning <see cref="MappedSourceIndexSet"/> and
    /// the resolved <see cref="SourceIndex"/> for this table, or
    /// <c>(null, null)</c> when absent <strong>or stale</strong>.
    /// Multiple scan operators share the single mapped view via the
    /// kept <see cref="MappedSourceIndexSet"/>.
    /// </summary>
    /// <remarks>
    /// Compares the index sidecar's stored
    /// <see cref="SourceFingerprint"/> against a freshly-computed
    /// fingerprint of the current <c>.datum</c> file. Any prior
    /// mutation (AppendRows / AddColumn / DropColumn / DeleteRows) bumps
    /// the file's size and stripe-hash, so the stored fingerprint no
    /// longer matches and the index is treated as missing — readers
    /// fall back to scan rather than silently using a stale index that
    /// could miss newly-appended rows or address dropped columns. The
    /// stale sidecar file isn't deleted; a future REINDEX command
    /// rebuilds it on demand.
    /// </remarks>
    private static (MappedSourceIndexSet? Mapped, SourceIndex? Index) TryLoadSourceIndex(TableDescriptor descriptor)
    {
        string path = PathDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-index";
        if (!File.Exists(path))
        {
            return (null, null);
        }

        MappedSourceIndexSet mapped;
        try
        {
            mapped = UnifiedIndexReader.Open(path);
        }
        catch (InvalidDataException)
        {
            // Torn IDXT tail or otherwise unreadable. Treat as missing —
            // queries fall back to scan; the next REINDEX rebuilds. Same
            // failure-mode contract as a fingerprint mismatch (PR9.5).
            return (null, null);
        }
        try
        {
            if (!IsFingerprintCurrent(descriptor.FilePath, mapped.IndexSet.Fingerprint))
            {
                // Stale index — source file has changed since the index
                // was built. Treat as no index; the next reader either
                // works without it or callers can run REINDEX.
                mapped.Dispose();
                return (null, null);
            }

            SourceIndex? index = ResolveSidecarEntry(mapped.IndexSet.Tables, descriptor.Name, descriptor.FilePath);
            if (index is null)
            {
                mapped.Dispose();
                return (null, null);
            }
            return (mapped, index);
        }
        catch
        {
            mapped.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Compares <paramref name="storedFingerprint"/> against a freshly-
    /// computed fingerprint of the file at <paramref name="datumPath"/>.
    /// Returns <see langword="false"/> when the file has changed since
    /// the fingerprint was captured (different size, or differing
    /// stripe content).
    /// </summary>
    /// <remarks>
    /// Synchronous wrapper over the async <see cref="SourceFingerprint.MatchesAsync"/>
    /// because the provider constructor is synchronous. The compute
    /// is striped-sample-based (not full-file), so the cost is bounded
    /// regardless of file size.
    /// </remarks>
    private static bool IsFingerprintCurrent(string datumPath, SourceFingerprint storedFingerprint)
    {
        using FileStream fs = File.OpenRead(datumPath);
        return storedFingerprint.Matches(fs);
    }

    /// <summary>
    /// Resolves a sidecar entry by the catalog's registered table name,
    /// falling back to the file-convention-derived name. Mirrors the v1
    /// loader to handle name-mismatch scenarios consistently between
    /// formats.
    /// </summary>
    private static T? ResolveSidecarEntry<T>(
        IReadOnlyDictionary<string, T> entries, string tableName, string sourceFilePath)
        where T : class
    {
        if (entries.TryGetValue(tableName, out T? value))
        {
            return value;
        }

        string derivedName = PathDetector.DeriveTableName(sourceFilePath);
        if (!string.Equals(derivedName, tableName, StringComparison.Ordinal)
            && entries.TryGetValue(derivedName, out value))
        {
            return value;
        }

        return null;
    }

    // ──────────────────── Mutation (catalog-level ALTER TABLE) ────────────────────

    /// <inheritdoc/>
    public bool CanAlterColumns => true;

    /// <inheritdoc/>
    public bool CanAppendRows => true;

    /// <inheritdoc/>
    public bool CanDeleteRows => true;

    /// <inheritdoc/>
    public bool CanUpdateRows => true;

    /// <inheritdoc/>
    public void AddColumn(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!column.Nullable)
        {
            throw new ArgumentException(
                $"AddColumn requires column '{column.Name}' to be nullable: existing rows are " +
                "back-filled with nulls and a non-nullable column would violate the schema.",
                nameof(column));
        }

        ColumnDescriptorV2 descriptor = new(
            Name: column.Name,
            Kind: column.Kind,
            Encoder: ColumnDescriptorV2.EncoderFor(column.Kind, column.IsArray),
            IsNullable: column.Nullable,
            IsArray: column.IsArray);

        // Translate the column's optional DEFAULT / GENERATED ALWAYS AS
        // expressions into SQL fragments the writer persists in the
        // prologue's defaults table / footer's computed-columns block.
        // Mutual exclusion is enforced upstream by the catalog.
        string? defaultFragment = column.DefaultExpression is null
            ? null
            : Execution.QueryExplainer.FormatExpression(column.DefaultExpression);
        string? computedFragment = column.ComputedExpression is null
            ? null
            : Execution.QueryExplainer.FormatExpression(column.ComputedExpression);

        // IDENTITY: the writer needs the new column's footer index, which
        // it computes itself (newColumnCount - 1 after the resize). We
        // pass a placeholder ColumnIndex; the writer overwrites it from
        // its own state at pump time.
        IdentityWriterSpec? identitySpec = column.Identity is null
            ? null
            : new IdentityWriterSpec(
                ColumnIndex: -1,  // writer assigns the real footer index
                Seed: column.Identity.Seed,
                Step: column.Identity.Step,
                AcceptUserValues: column.Identity.AcceptUserValues);

        _mutationLock.Wait();
        try
        {
            DatumFileWriterV2.AddColumn(_descriptor.FilePath, descriptor, defaultFragment, computedFragment, identitySpec);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public void DropColumn(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        _mutationLock.Wait();
        try
        {
            DatumFileWriterV2.DropColumn(_descriptor.FilePath, columnName);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

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
            DatumFileWriterV2.SoftDeleteRows(_descriptor.FilePath, rowIndices);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
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
            // rowInPage) tuple by walking the page directory once,
            // skipping tombstoned rows. Using the current snapshot's
            // footer + tombstone bitmaps — same numbering as the scan
            // path emits.
            Snapshot snap = _snapshot;
            int[] schemaToFooter = snap.SchemaToFooterIndex;
            FooterV2 footer = snap.Reader.Footer;
            byte[]?[]? tombstones = snap.ChapterTombstoneBitmaps;
            int chapterRowSpan = ChapterTombstoneBlock.MaxRowsPerChapter;

            // Build live-row → (pageIndex, rowInPage) for the unique set
            // of live indices in `requests`. Sort indices ascending so a
            // single forward walk over the page directory resolves all
            // of them.
            HashSet<long> uniqueLiveIndices = new(requests.Count);
            foreach (RowUpdateRequest req in requests) uniqueLiveIndices.Add(req.LiveRowIndex);
            long[] sortedLive = uniqueLiveIndices.ToArray();
            Array.Sort(sortedLive);

            Dictionary<long, (int Page, int Row)> liveToRaw = new(sortedLive.Length);
            int pageCount = footer.Columns.Count > 0 ? footer.Columns[0].Pages.Count : 0;
            int sortedIndex = 0;
            long liveCounter = 0;
            long rawRow = 0;
            for (int p = 0; p < pageCount && sortedIndex < sortedLive.Length; p++)
            {
                int rowsInPage = footer.Columns[0].Pages[p].RowCount;
                for (int r = 0; r < rowsInPage; r++, rawRow++)
                {
                    int chapterIndex = (int)(rawRow / chapterRowSpan);
                    int rowInChapter = (int)(rawRow - (long)chapterIndex * chapterRowSpan);
                    bool tombstoned = tombstones is not null
                        && chapterIndex < tombstones.Length
                        && tombstones[chapterIndex] is byte[] bits
                        && (bits[rowInChapter >> 3] & (1 << (rowInChapter & 7))) != 0;
                    if (tombstoned) continue;

                    while (sortedIndex < sortedLive.Length && sortedLive[sortedIndex] == liveCounter)
                    {
                        liveToRaw[liveCounter] = (p, r);
                        sortedIndex++;
                    }
                    liveCounter++;
                    if (sortedIndex >= sortedLive.Length) break;
                }
            }

            if (sortedIndex < sortedLive.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requests), sortedLive[sortedIndex],
                    $"UpdateRows: live row index {sortedLive[sortedIndex]} is out of range " +
                    $"for table '{Name}' (table has {liveCounter} live row(s)).");
            }

            // Group requests by page; translate schema-column indices to
            // footer-column indices using SchemaToFooterIndex (the schema
            // skips tombstoned columns; the footer keeps them).
            Dictionary<int, List<RowUpdate>> grouped = new();
            foreach (RowUpdateRequest req in requests)
            {
                (int page, int row) = liveToRaw[req.LiveRowIndex];
                Dictionary<int, DataValue> footerKeyedValues = new(req.NewValues.Count);
                foreach ((int schemaColIdx, DataValue value) in req.NewValues)
                {
                    if (schemaColIdx < 0 || schemaColIdx >= schemaToFooter.Length)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(requests), schemaColIdx,
                            $"UpdateRows: schema column index {schemaColIdx} out of range for " +
                            $"table '{Name}' (schema has {schemaToFooter.Length} column(s)).");
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
            // via datum_catalog.indexes.is_valid = false).
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
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private DatumFileWriterV2 OpenAppendWriter()
    {
        string sidecarPath = Path.ChangeExtension(_descriptor.FilePath, SidecarConstants.FileExtension);
        return DatumFileWriterV2.OpenForAppend(_descriptor.FilePath, sidecarPath);
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

    /// <summary>
    /// Append session backed by a held-open <see cref="DatumFileWriterV2"/>.
    /// Constructed under the parent provider's <c>_mutationLock</c>;
    /// owns the permit until disposed. Writes stream straight to the
    /// data file (visible past the existing tail but unreferenced
    /// until commit); commit calls <c>FinalizeWriter</c> to write the
    /// new tail and rebuild the parent's snapshot. Disposing without
    /// committing closes the writer without finalizing — partial bytes
    /// are reclaimed by the next writer's torn-tail recovery.
    /// </summary>
    private sealed class DatumAppendSession : IAppendSession
    {
        private readonly DatumFileTableProviderV2 _provider;
        private DatumFileWriterV2? _writer;
        private bool _committed;
        private bool _disposed;
        private bool _anyWrites;

        // Captured at construction; unchanged across the session's
        // lifetime (column index + spec are CREATE-time properties).
        // null when the table has no IDENTITY column.
        private readonly IdentityState? _initialIdentityState;
        private long _identityNextValue;
        private bool _identityReserved;

        // Composite-encoded PK key bytes queued per WriteAsync row when
        // the parent provider has an open PK index. Flushed into the
        // bytes tree on CommitAsync after the data commit succeeds.
        // <see langword="null"/> when there's no on-disk PK index —
        // InsertExecutor's scan-based fallback handles uniqueness for
        // those tables (TEMP / InMemoryProvider).
        private readonly List<byte[]>? _pendingPkBytes;
        private readonly IReadOnlyList<int> _pkColumnIndices;

        // User-defined composite indexes (CREATE INDEX). One queue per
        // index, keyed by index name. Each entry carries the encoded key
        // plus the (chunkIndex, rowOffsetInChunk) where the row lands —
        // captured pre-AddRow so future point/range probes can seek back.
        // Empty (null) when the table has no composite indexes; otherwise
        // mirrors the snapshot of <c>_compositeIndexTrees</c> at session
        // start. Re-resolution against the live provider state happens
        // at flush time in CommitAsync.
        private readonly Dictionary<string, CompositeIndexState>? _compositeIndexStates;

        // Phase 3a-wide: per-row in-line index build. Eliminates the
        // post-commit scan that ExtendAsync used to do — the append session
        // already has the rows in hand, so we mirror them into an
        // IncrementalIndexBuilder (bloom + bitmap + zone maps) and queue
        // per-column tree entries during WriteAsync. CommitAsync flushes
        // the queues into the provider's open trees and writes the merged
        // sidecar atomically.
        //
        // Lazily initialized on first WriteAsync — schema is captured then.
        private Indexing.IncrementalIndexBuilder? _indexBuilder;
        private SourceIndex? _existingForMerge;
        private Dictionary<string, List<ValueIndexEntry>>? _pendingColumnEntries;
        private string[]? _indexableColumnNames;
        private const int _indexBuildChunkSize = Indexing.IndexConstants.DefaultChunkSize;

        public DatumAppendSession(DatumFileTableProviderV2 provider)
        {
            _provider = provider;
            // Writer opened lazily on first WriteAsync — empty sessions
            // (Begin → Commit with no writes) are no-ops with no file
            // mutation, matching the in-memory provider's semantics.

            // Snapshot IDENTITY state once. Reservations advance the
            // session-local counter; commit pushes it back through the
            // writer's prologue.
            Snapshot snapshot = provider.AcquireSnapshot();
            try
            {
                IdentityState? state = ResolveIdentityStateLocked(snapshot);
                _initialIdentityState = state;
                _identityNextValue = state?.NextValue ?? 0;
            }
            finally
            {
                provider.ReleaseSnapshot(snapshot);
            }

            // PK index state. Captured once; the column indices can't
            // move because ALTER DROP COLUMN of a PK column is rejected.
            if (provider._pkIndexBytes is not null)
            {
                _pendingPkBytes = new List<byte[]>();
                _pkColumnIndices = provider._pkColumnIndices;
            }
            else
            {
                _pkColumnIndices = Array.Empty<int>();
            }

            // Composite index state. Snapshot the provider's open indexes
            // so the session has stable per-index queues + column ordinals.
            // ALTER DROP COLUMN that affects a covered column drops the
            // dependent index, so the ordinals are guaranteed stable for the
            // session's lifetime.
            if (provider._compositeIndexTrees.Count > 0)
            {
                _compositeIndexStates = new Dictionary<string, CompositeIndexState>(
                    StringComparer.OrdinalIgnoreCase);
                foreach ((string indexName, _) in provider._compositeIndexTrees)
                {
                    int[] ordinals = provider._compositeIndexColumnIndices[indexName];
                    _compositeIndexStates[indexName] = new CompositeIndexState(
                        ColumnIndices: ordinals,
                        PendingEntries: new List<Indexing.BTree.MutableBytes.BytesIndexEntry>());
                }
            }
        }

        /// <summary>
        /// Per-index state captured at the start of an append session: the
        /// schema column indices to extract per row plus the queue of
        /// encoded entries waiting for CommitAsync to flush into the tree.
        /// </summary>
        private sealed record CompositeIndexState(
            int[] ColumnIndices,
            List<Indexing.BTree.MutableBytes.BytesIndexEntry> PendingEntries);

        public IdentityState? IdentityState => _initialIdentityState is null
            ? null
            : _initialIdentityState with { NextValue = _identityNextValue };

        public long ReserveNextIdentityValue()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("Cannot reserve IDENTITY values after CommitAsync.");
            if (_initialIdentityState is null)
            {
                throw new InvalidOperationException(
                    $"Table '{_provider.Name}' has no IDENTITY column.");
            }

            long reserved = _identityNextValue;
            _identityNextValue = checked(_identityNextValue + _initialIdentityState.Spec.Step);
            _identityReserved = true;
            return reserved;
        }

        private static IdentityState? ResolveIdentityStateLocked(Snapshot snapshot)
        {
            // Snapshot.Schema's tombstone-skipping is symmetric with the
            // schema-to-footer index map; walk live schema columns and
            // pick the one whose ColumnInfo.Identity is non-null.
            Schema schema = snapshot.Schema;
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                ColumnInfo c = schema.Columns[i];
                if (c.Identity is { } spec)
                {
                    return new IdentityState(
                        ColumnIndex: i,
                        ColumnKind: c.Kind,
                        Spec: spec,
                        NextValue: snapshot.Reader.Footer.Prologue.IdentityNextValue);
                }
            }
            return null;
        }

        public Task WriteAsync(RowBatch batch, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("Cannot write after CommitAsync.");
            ArgumentNullException.ThrowIfNull(batch);
            cancellationToken.ThrowIfCancellationRequested();

            if (batch.Count == 0) return Task.CompletedTask;

            ValidateBatchSchema(batch);

            // Capture PK keys per row, encoding them to bytes before the
            // batch potentially returns to the pool. The encoded bytes
            // are fresh byte[] arrays so they survive the batch's
            // arena lifecycle.
            if (_pendingPkBytes is not null)
            {
                DataValue[] tuple = new DataValue[_pkColumnIndices.Count];
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    for (int p = 0; p < _pkColumnIndices.Count; p++)
                    {
                        tuple[p] = row[_pkColumnIndices[p]];
                    }
                    byte[] encoded = Indexing.CompositeKeyEncoder.Encode(tuple, batch.Arena);
                    _pendingPkBytes.Add(encoded);
                }
            }

            // Phase 3a-wide: build the unified-sidecar delta + per-column
            // tree entry queues in lockstep with the data write. Lazy init
            // on first batch — _indexBuilder needs a fingerprint at
            // construction (will be swapped at Finalize), and the indexable
            // column set is captured from the live schema once.
            if (_indexBuilder is null)
            {
                EnsureIndexBuildersInitialized();
            }

            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];

                // Capture (chunkIndex, rowOffsetInChunk) BEFORE AddRow so the
                // values match where IncrementalIndexBuilder will place this
                // row internally. AddRow may roll the chunk forward inside
                // its own bookkeeping; we want the pre-add slot.
                int chunkIndex = _indexBuilder!.CurrentChunkIndex;
                int rowOffsetInChunk = _indexBuilder.RowsInCurrentChunk;

                _indexBuilder.AddRow(row, batch.Arena);

                if (_indexableColumnNames is { } cols)
                {
                    for (int c = 0; c < cols.Length; c++)
                    {
                        string columnName = cols[c];
                        DataValue value = row[columnName];
                        if (value.IsNull) continue;
                        // Sidecar-bound values can't survive the source
                        // batch's pool return (the DataValue copy points at
                        // an offset that's about to be reused). Skip — the
                        // column degrades to scan for those rows.
                        if (value.IsInSidecar) continue;
                        _pendingColumnEntries![columnName].Add(
                            new ValueIndexEntry(value, chunkIndex, rowOffsetInChunk));
                    }
                }

                // Composite indexes: encode this row's tuple once per index.
                // Encoded bytes outlive the source batch's arena (Encode
                // returns a fresh byte[]), so the queue is safe to hold.
                if (_compositeIndexStates is { } compositeStates)
                {
                    foreach (CompositeIndexState state in compositeStates.Values)
                    {
                        DataValue[] tuple = new DataValue[state.ColumnIndices.Length];
                        for (int p = 0; p < state.ColumnIndices.Length; p++)
                        {
                            tuple[p] = row[state.ColumnIndices[p]];
                        }
                        byte[] encoded = Indexing.CompositeKeyEncoder.Encode(tuple, batch.Arena);
                        state.PendingEntries.Add(
                            new Indexing.BTree.MutableBytes.BytesIndexEntry(
                                encoded, chunkIndex, rowOffsetInChunk));
                    }
                }
            }

            _writer ??= _provider.OpenAppendWriter();
            _writer.WriteRowBatch(batch);
            _anyWrites = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Lazy initializer for the in-line index build. Captures the live
        /// schema's indexable-column set, asks the provider to open/create
        /// trees for them, and creates an <see cref="Indexing.IncrementalIndexBuilder"/>
        /// rooted at a placeholder fingerprint (overridden at finalize with
        /// the real post-commit fingerprint). Called from the first WriteAsync.
        /// </summary>
        private void EnsureIndexBuildersInitialized()
        {
            // Capture the existing in-memory index for the merge step.
            // Same lifetime story as the original RebuildIndexNoLock(existingForExtend)
            // path: RebuildSnapshotAfterMutation no longer disposes the
            // cached mmap, so this reference's accessor stays alive
            // through CommitAsync's merge.
            _existingForMerge = _provider._sourceIndex;

            Schema schema = _provider._snapshot.Schema;

            // Indexable columns: kind-based eligibility, no array columns
            // (parallel to the rules SourceIndexBuilder.CreateBitmapAccumulators
            // and Indexer.IndexAsync use).
            List<string> indexable = new();
            foreach (ColumnInfo column in schema.Columns)
            {
                if (Indexing.SourceIndexBuilder.IsAutoIndexableKind(column.Kind) && !column.IsArray)
                {
                    indexable.Add(column.Name);
                }
            }
            _indexableColumnNames = indexable.ToArray();

            // Have the provider open/create trees for any indexable columns
            // that don't yet have one open. After this call, every column
            // in _indexableColumnNames has an open MutableBPlusTree we can
            // GetColumnTreeForAppendSession() on at commit time.
            _provider.EnsureColumnTreesForIndexableColumnsNoLock();

            _pendingColumnEntries = new Dictionary<string, List<ValueIndexEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (string columnName in _indexableColumnNames)
            {
                _pendingColumnEntries[columnName] = new List<ValueIndexEntry>();
            }

            // Builder needs a fingerprint at construction; it's overridden
            // at Finalize() with the real post-commit one. Bloom + bitmap
            // for every column ("Auto" mode parity with Indexer.IndexAsync's
            // default IndexOptions).
            SourceIndexBuilder builder = new(
                bloomAllColumns: true,
                chunkSize: _indexBuildChunkSize,
                computeCardinality: true);
            SourceFingerprint placeholder = new(0, new byte[32]);
            _indexBuilder = builder.CreateIncrementalBuilder(placeholder);
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("CommitAsync was already called.");
            cancellationToken.ThrowIfCancellationRequested();

            // Capture the pre-mutation in-memory index BEFORE we touch
            // the file. PR13b's chunk-splice extend path needs this to
            // carry forward the prefix's bloom + bitmap + zone-map
            // bytes without rescanning. RebuildSnapshotAfterMutation no
            // longer disposes the cached mmap, so the captured
            // reference's accessor stays alive until
            // RebuildIndexNoLock's final InvalidateSourceIndexCache.
            SourceIndex? existingForExtend = _provider._sourceIndex;

            try
            {
                // If the session reserved IDENTITY values but never
                // wrote a batch, we still need to bump the prologue's
                // counter so the wasted reservations aren't handed out
                // again on a future session. Open the writer in append
                // mode — it carries forward existing state, including
                // the IDENTITY counter, and rewrites the prologue.
                if (_writer is null && _identityReserved)
                {
                    _writer = _provider.OpenAppendWriter();
                }

                if (_writer is not null)
                {
                    if (_initialIdentityState is not null && _identityReserved)
                    {
                        _writer.UpdateIdentityNextValue(_identityNextValue);
                    }
                    _writer.FinalizeWriter();
                    _writer.Dispose();
                    _writer = null;

                    // Rebuild the parent's snapshot under the lock we
                    // already hold (the session has the semaphore
                    // permit). Sidecar may have grown if any of the
                    // appended rows spilled non-inline payloads.
                    // Snapshot rebuild leaves _sourceIndex untouched —
                    // RebuildIndexNoLock below handles the swap.
                    _provider.RebuildSnapshotAfterMutation(sidecarMayHaveGrown: _anyWrites);
                }

                // Flush queued PK keys into the on-disk B+Tree. Done after
                // the .datum file commit succeeds so a writer crash between
                // the two leaves the index either consistent (no data, no
                // index update) or slightly stale (rows present, missing
                // from index). The latter only widens the window for a
                // duplicate to slip past on the next INSERT — caught at
                // REINDEX (PR12) until we add a 2-phase commit.
                if (_pendingPkBytes is not null && _provider._pkIndexBytes is not null)
                {
                    foreach (byte[] encoded in _pendingPkBytes)
                    {
                        // ChunkIndex / RowOffset are placeholder zeros —
                        // the lookup is uniqueness-only; the actual
                        // (chunk, row) addressing for "find me the row"
                        // is a follow-up PR. Storing 0/0 keeps the entry
                        // shape compatible.
                        _provider._pkIndexBytes.Insert(
                            new Indexing.BTree.MutableBytes.BytesIndexEntry(
                                encoded, ChunkIndex: 0, RowOffsetInChunk: 0L));
                    }
                    _pendingPkBytes.Clear();
                }

                // Flush queued composite-index entries. Same crash-window
                // contract as the PK queue: if the process dies between
                // this loop and the next commit, the affected indexes go
                // slightly stale — rebuild via DROP / CREATE INDEX, or
                // (future) REINDEX once it learns about composite trees.
                if (_compositeIndexStates is { } compositeStates)
                {
                    foreach ((string indexName, CompositeIndexState state) in compositeStates)
                    {
                        if (!_provider._compositeIndexTrees.TryGetValue(
                                indexName, out Indexing.BTree.MutableBytes.MutableBPlusTreeBytes? tree))
                        {
                            // Index was dropped mid-session — nothing to flush.
                            state.PendingEntries.Clear();
                            continue;
                        }
                        foreach (Indexing.BTree.MutableBytes.BytesIndexEntry entry in state.PendingEntries)
                        {
                            tree.Insert(entry);
                        }
                        state.PendingEntries.Clear();
                    }
                }

                _committed = true;
            }
            catch
            {
                // Commit failed — drop the writer without finalizing.
                // Subsequent torn-tail recovery cleans up.
                _writer?.Dispose();
                _writer = null;
                throw;
            }

            // Phase 3a-wide: refresh the unified .datum-index sidecar from
            // the in-line build that ran alongside WriteAsync. The append
            // session already has the per-row bloom / bitmap / zone-map
            // accumulators populated and per-column tree entries queued —
            // we don't need Indexer to re-scan the file the way the old
            // PR13b extend path did.
            //
            // Failure-mode contract (locked in PR13's design): if any of
            // the steps below fails, the data commit stands. The cache is
            // invalidated and the on-disk .datum-index becomes Stale (per
            // IndexValidity) — queries fall back to scan until a manual
            // REINDEX recovers. We swallow the exception so the user's
            // INSERT statement doesn't appear to fail when the data is
            // actually committed.
            //
            // IDENTITY-only commits (no _anyWrites) bump the prologue and
            // therefore the fingerprint, but no rows changed; the existing
            // index is functionally stale on fingerprint mismatch alone.
            // Match PR13a-2 behaviour and just invalidate.
            if (_anyWrites && _indexBuilder is not null)
            {
                try
                {
                    FlushIndexBuildersToProvider(existingForExtend);
                }
                catch
                {
                    _provider.InvalidateSourceIndexCache();
                }
            }
            else
            {
                _provider.InvalidateSourceIndexCache();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Phase 3a-wide commit-time flush: drains the per-column tree entry
        /// queues into the provider's open <see cref="MutableBPlusTree"/>s,
        /// finalizes the in-line index build using the post-commit data-file
        /// fingerprint, merges with <paramref name="existing"/>, and writes
        /// the result via <see cref="DatumFileTableProviderV2.WriteUnifiedSidecarNoLock"/>.
        /// </summary>
        private void FlushIndexBuildersToProvider(SourceIndex? existing)
        {
            // Per-column tree inserts. Trees stay open across the call —
            // the append session's mutation lock keeps concurrent readers
            // from observing torn state through the open dictionary.
            if (_pendingColumnEntries is not null)
            {
                foreach (KeyValuePair<string, List<ValueIndexEntry>> entry in _pendingColumnEntries)
                {
                    MutableBPlusTree? tree = _provider.GetColumnTreeForAppendSession(entry.Key);
                    if (tree is null) continue;
                    foreach (ValueIndexEntry e in entry.Value)
                    {
                        tree.Insert(e);
                    }
                }
            }

            // Compute the post-commit fingerprint of the .datum file.
            // The data writer just rewrote the prologue, so the fingerprint
            // necessarily changed; the in-line builder needs the live one
            // so the on-disk sidecar matches.
            SourceFingerprint freshFingerprint;
            using (FileStream stream = new(
                _provider._descriptor.FilePath,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, useAsync: false))
            {
                freshFingerprint = SourceFingerprint.Compute(stream);
            }

            SourceIndex delta = _indexBuilder!.Finalize(freshFingerprint);

            SourceIndex merged = existing is null
                ? delta
                : SourceIndex.Merge(existing, delta);

            string tableName = PathDetector.DeriveTableName(_provider._descriptor.FilePath);
            _provider.WriteUnifiedSidecarNoLock(merged, tableName);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;

            try
            {
                if (!_committed && _writer is not null)
                {
                    // Abort: close the writer without FinalizeWriter so
                    // no new tail is emitted. Trailing partial bytes
                    // become unreachable garbage cleaned up by the next
                    // writer's RecoverIfTorn pass.
                    _writer.Dispose();
                    _writer = null;
                }
                _indexBuilder?.Dispose();
                _indexBuilder = null;
            }
            finally
            {
                _provider._mutationLock.Release();
            }

            return ValueTask.CompletedTask;
        }

        private void ValidateBatchSchema(RowBatch batch)
        {
            // Capture current schema under snapshot lock — light touch,
            // doesn't bump the refcount because we don't need to retain
            // anything across the call.
            Schema schema;
            lock (_provider._snapshotLock)
            {
                schema = _provider._snapshot.Schema;
            }

            if (batch.ColumnLookup.Count != schema.Columns.Count)
            {
                throw new InvalidOperationException(
                    $"Append batch has {batch.ColumnLookup.Count} columns but table " +
                    $"'{_provider.Name}' has {schema.Columns.Count}.");
            }
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                string expected = schema.Columns[i].Name;
                string actual = batch.ColumnLookup.GetColumnName(i);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Append batch column {i} is named '{actual}' but table " +
                        $"'{_provider.Name}' expects '{expected}'.");
                }
            }
        }
    }
}
