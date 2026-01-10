using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

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
    private readonly QueryResultsManifest? _manifest;
    /// <summary>
    /// Lazily-loaded <c>.datum-index</c> sidecar mapping, captured at
    /// provider construction. Becomes stale after any mutation
    /// (mutation invalidates the source file's fingerprint); the
    /// mutation path nulls these fields out so subsequent
    /// <see cref="GetSourceIndex"/> calls return <see langword="null"/>
    /// and queries fall back to scan instead of using a stale index.
    /// A future REINDEX command (PR12) will repopulate.
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
    private MutableBPlusTree? _pkIndex;

    /// <summary>
    /// Schema column index of the single-column PK when <see cref="_pkIndex"/>
    /// is non-null. -1 otherwise. Captured at provider construction; the
    /// schema-build pipeline guarantees it doesn't move (ALTER DROP COLUMN of
    /// a PK column is rejected by the catalog).
    /// </summary>
    private int _pkColumnIndex = -1;

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
    }

    /// <summary>
    /// Opens the <c>.datum-pkindex</c> sidecar when the schema has a
    /// single-column PK and the file exists. No-op for composite PKs
    /// (PR10h scope) or when the sidecar is missing — those tables fall
    /// back to <c>InsertExecutor</c>'s scan-based PK check.
    /// </summary>
    private void TryOpenPrimaryKeyIndex()
    {
        IReadOnlyList<int> pkIndices = _snapshot.Schema.PrimaryKeyColumnIndices;
        if (pkIndices.Count != 1) return;

        string pkIndexPath = GetPrimaryKeyIndexPath(_descriptor.FilePath);
        if (!File.Exists(pkIndexPath)) return;

        _pkIndex = MutableBPlusTree.Open(pkIndexPath);
        _pkColumnIndex = pkIndices[0];
    }

    /// <summary>
    /// Returns the <c>.datum-pkindex</c> path companion for the given
    /// <c>.datum</c> file path.
    /// </summary>
    internal static string GetPrimaryKeyIndexPath(string datumPath) =>
        Path.ChangeExtension(datumPath, ".datum-pkindex");

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
    public QueryResultsManifest? GetManifest() => _manifest;

    /// <inheritdoc/>
    public SourceIndex? GetSourceIndex() => _sourceIndex;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
                    decoders[i] = s.Reader.OpenPageDecoder(
                        columnIndex: schemaIndices[i],
                        pageIndex: pageIndex,
                        sidecarStoreId: SidecarStoreId,
                        sidecarSource: s.Sidecar,
                        eagerStore: batch.Arena);
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
        _pkIndex?.Dispose();
        _pkIndex = null;

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
    public IPrimaryKeyLookup? GetPrimaryKeyLookup() =>
        _pkIndex is null ? null : new MutableBPlusTreePrimaryKeyLookup(_pkIndex);

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

        // IDENTITY spec — at most one column carries it, identified by
        // footer-column index. Resolve to the schema-column index after
        // tombstone-skipping below.
        IdentitySpec? identitySpec = null;
        int identityFooterIndex = footer.Prologue.IdentityColumnIndex;
        if (identityFooterIndex >= 0)
        {
            identitySpec = new IdentitySpec(
                footer.Prologue.IdentitySeed,
                footer.Prologue.IdentityStep);
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

            footerIndexToSchemaIndex[i] = columns.Count;
            columns.Add(new ColumnInfo(d.Name, d.Kind, d.IsNullable)
            {
                IsArray = d.IsArray,
                DefaultExpression = defaultExpression,
                Identity = i == identityFooterIndex ? identitySpec : null,
                IsPrimaryKey = pkFooterIndexSet is not null && pkFooterIndexSet.Contains((ushort)i),
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

        MappedSourceIndexSet mapped = UnifiedIndexReader.Open(path);
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
        return storedFingerprint.MatchesAsync(fs, CancellationToken.None).GetAwaiter().GetResult();
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

        _mutationLock.Wait();
        try
        {
            DatumFileWriterV2.AddColumn(_descriptor.FilePath, descriptor);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
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

    /// <summary>
    /// Builds a new <see cref="Snapshot"/> from the on-disk file post-
    /// mutation and atomically swaps it in. Caller must hold
    /// <see cref="_mutationLock"/>. When
    /// <paramref name="sidecarMayHaveGrown"/> is <see langword="true"/>
    /// (i.e. an append just ran), the catalog's
    /// <see cref="SidecarRegistry"/> is updated with the new
    /// <see cref="IBlobSource"/> so existing storeId-stamped DataValues
    /// in flight resolve through bytes the new mmap can see.
    /// </summary>
    private void RebuildSnapshotAfterMutation(bool sidecarMayHaveGrown)
    {
        Snapshot next = OpenSnapshot(_descriptor.FilePath);
        if (sidecarMayHaveGrown && next.Sidecar is not null && SidecarRegistry is not null)
        {
            SidecarRegistry.UpdateAt(SidecarStoreId, next.Sidecar);
        }
        SwapSnapshot(next);

        // Invalidate the cached source index — the mutation just
        // changed the source file's fingerprint, so any cached
        // .datum-index sidecar is now stale. Drop our handle so
        // GetSourceIndex returns null and queries fall back to scan.
        // The .datum-index file on disk is left in place; a future
        // REINDEX rebuilds it, and TryLoadSourceIndex's fingerprint
        // check rejects it on the next provider open.
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

        // PK keys queued for index commit. Populated per WriteAsync row
        // when the parent provider has an open PK index; flushed into
        // the tree on CommitAsync (after the data commit succeeds).
        // null when there's no on-disk PK index — InsertExecutor's
        // scan-based fallback path handles uniqueness for those tables.
        private readonly List<DataValue>? _pendingPkKeys;
        private readonly int _pkColumnIndex;

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

            // PK index state. Captured once; the column index can't move
            // because ALTER DROP COLUMN of a PK column is rejected.
            if (provider._pkIndex is not null)
            {
                _pendingPkKeys = new List<DataValue>();
                _pkColumnIndex = provider._pkColumnIndex;
            }
            else
            {
                _pkColumnIndex = -1;
            }
        }

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

            // Capture PK keys before WriteRowBatch streams to the writer —
            // the row reads need to happen against the live batch values,
            // and the batch may be released back to the pool after commit.
            // Stabilise each value into a fresh DataValue so the queued
            // key survives any arena lifecycle the source batch had.
            if (_pendingPkKeys is not null)
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    DataValue key = row[_pkColumnIndex];
                    _pendingPkKeys.Add(key);
                }
            }

            _writer ??= _provider.OpenAppendWriter();
            _writer.WriteRowBatch(batch);
            _anyWrites = true;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("CommitAsync was already called.");
            cancellationToken.ThrowIfCancellationRequested();

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
                    _provider.RebuildSnapshotAfterMutation(sidecarMayHaveGrown: _anyWrites);
                }

                // Flush queued PK keys into the on-disk B+Tree. Done after
                // the .datum file commit succeeds so a writer crash between
                // the two leaves the index either consistent (no data, no
                // index update) or slightly stale (rows present, missing
                // from index). The latter only widens the window for a
                // duplicate to slip past on the next INSERT — caught at
                // REINDEX (PR12) until we add a 2-phase commit.
                if (_pendingPkKeys is not null && _provider._pkIndex is not null)
                {
                    foreach (DataValue key in _pendingPkKeys)
                    {
                        // ChunkIndex / RowOffset are placeholder zeros —
                        // PR10h's lookup is uniqueness-only; the actual
                        // (chunk, row) addressing for "find me the row"
                        // is a follow-up PR. Storing 0/0 keeps the entry
                        // shape compatible.
                        _provider._pkIndex.Insert(new ValueIndexEntry(key, ChunkIndex: 0, RowOffsetInChunk: 0L));
                    }
                    _pendingPkKeys.Clear();
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

            return Task.CompletedTask;
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
