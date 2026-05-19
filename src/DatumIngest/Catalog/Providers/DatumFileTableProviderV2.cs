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
public sealed partial class DatumFileTableProviderV2 : ITableProvider, IDatumFileTableProvider, IDisposable
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
    /// Brief-duration lock protecting the three composite-index dictionaries
    /// (<see cref="_compositeIndexTrees"/>, <see cref="_compositeIndexColumnIndices"/>,
    /// <see cref="_compositeIndexDescriptors"/>) against concurrent reader
    /// enumeration during a mutation. Separate from <see cref="_mutationLock"/>:
    /// writers acquire <c>_mutationLock</c> for the whole operation (which can be
    /// long, e.g. a CREATE INDEX backfill) and only need this fast lock around the
    /// final dict swap; readers (<see cref="GetCompositeIndexes"/>) take only this
    /// fast lock to snapshot a consistent view without waiting on a slow writer.
    /// </summary>
    private readonly object _compositeIndexSync = new();

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
        OpenFtsIndexes();
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
    public void EnsureTypeTableLoaded(Heliosoph.DatumV.Execution.ExecutionContext context)
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
    public QualifiedName QualifiedName => Catalog.QualifiedName.Parse(_descriptor.Name);

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
    public void Dispose()
    {
        _mappedIndexSet?.Dispose();
        _pkIndexBytes?.Dispose();
        _pkIndexBytes = null;
        CloseColumnIndexes();
        CloseCompositeIndexes();
        CloseFtsIndexes();

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
            bool isPrimaryKey = pkFooterIndexSet is not null && pkFooterIndexSet.Contains((ushort)i);
            // PK columns are implicitly NOT NULL at the schema level even
            // if the underlying descriptor still records IsNullable=true.
            // The latter happens for ALTER TABLE … ADD COLUMN … PRIMARY KEY:
            // the column's pages were written with a null bitmap (the
            // writer requires it for historical-row backfill), but the
            // promote-to-PK path guarantees every live row has a non-null
            // value. Surfacing Nullable=false on the schema matches what
            // CREATE TABLE-time PK columns report.
            bool effectiveNullable = isPrimaryKey ? false : d.IsNullable;
            columns.Add(new ColumnInfo(d.Name, d.Kind, effectiveNullable)
            {
                IsArray = d.IsArray,
                DefaultExpression = defaultExpression,
                Identity = i == identityFooterIndex ? identitySpec : null,
                IsPrimaryKey = isPrimaryKey,
                ComputedExpression = computedExpression,
                MaxLength = d.MaxLength,
                FixedShape = d.FixedShape,
                IsBlankPadded = d.IsBlankPadded,
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



}
