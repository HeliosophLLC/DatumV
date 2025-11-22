using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
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
    private readonly DatumFileReaderV2 _reader;
    private readonly Pool _pool;
    private readonly SidecarReadStore? _sidecar;
    private readonly Schema _schema;
    private readonly QueryResultsManifest? _manifest;
    private readonly MappedSourceIndexSet? _mappedIndexSet;
    private readonly SourceIndex? _sourceIndex;

    /// <summary>
    /// Initializes the provider with the given descriptor and pool. Opens
    /// the v2 <c>.datum</c> file, parses its footer, and (when the file
    /// declares <see cref="DatumFileFlagsV2.HasSidecarReferences"/>)
    /// memory-maps the companion <c>.datum-blob</c> for sidecar reads.
    /// Auto-discovers <c>.datum-manifest</c> and <c>.datum-index</c>
    /// sidecars alongside the source so <see cref="GetManifest"/> /
    /// <see cref="GetSourceIndex"/> return live data. Use
    /// <see cref="DatumFileTableProvider.Open"/> from
    /// <see cref="TableCatalog"/> rather than this constructor directly so
    /// v1 / v2 dispatch is centralized.
    /// </summary>
    public DatumFileTableProviderV2(TableDescriptor descriptor, Pool pool)
    {
        _descriptor = descriptor;
        _pool = pool;
        _reader = DatumFileReaderV2.Open(descriptor.FilePath);
        _sidecar = TryOpenSidecar(descriptor.FilePath, _reader);
        _schema = BuildSchema(_reader.Footer);
        _manifest = TryLoadManifest(descriptor);
        (_mappedIndexSet, _sourceIndex) = TryLoadSourceIndex(descriptor);
    }

    /// <inheritdoc/>
    public IBlobSource? Sidecar => _sidecar;

    /// <inheritdoc/>
    public byte SidecarStoreId { get; set; }

    /// <inheritdoc/>
    public string Name => _descriptor.Name;

    /// <inheritdoc/>
    public bool Seekable => true;

    /// <inheritdoc/>
    public long GetRowCount() => _reader.TotalRowCount;

    /// <inheritdoc/>
    public Schema GetSchema() => _schema;

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
        if (_reader.Footer.Columns.Count == 0)
        {
            yield break;
        }

        // Resolve the column projection. Maps lookup-index → schema-index
        // so the scan only opens decoders for projected columns.
        ColumnLookup columnLookup = ResolveProjection(_schema, requiredColumns);
        int projectedCount = columnLookup.Count;
        if (projectedCount == 0)
        {
            yield break;
        }

        // All columns share the same page count and per-page row count
        // because the writer flushes every encoder at the same row cadence.
        int pageCount = _reader.Footer.Columns[0].Pages.Count;
        int[] schemaIndices = new int[projectedCount];
        for (int i = 0; i < projectedCount; i++)
        {
            schemaIndices[i] = columnLookup.GetSchemaColumnIndex(i);
        }

        // Build a filter-column → schema-index lookup once. Skip the
        // pruning path entirely when the filter references no columns we
        // have stats for.
        Dictionary<string, int>? filterSchemaIndex = filterHint is null
            ? null
            : BuildFilterColumnIndex(filterHint);

        // Stats arena for boxed min/max values during partition checks.
        // Reused across all skip evaluations; values are tiny (numerics,
        // short strings) so growth is bounded.
        Arena statsArena = new();

        IPageDecoderV2[] decoders = new IPageDecoderV2[projectedCount];

        foreach (int pageIndex in EnumerateScanablePages(pageCount, filterHint, filterSchemaIndex, statsArena))
        {
            cancellationToken.ThrowIfCancellationRequested();

            int rowCount = _reader.Footer.Columns[schemaIndices[0]].Pages[pageIndex].RowCount;
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
                decoders[i] = _reader.OpenPageDecoder(
                    columnIndex: schemaIndices[i],
                    pageIndex: pageIndex,
                    sidecarStoreId: SidecarStoreId,
                    sidecarSource: _sidecar,
                    eagerStore: batch.Arena);
            }

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = _pool.RentDataValues(projectedCount);
                for (int i = 0; i < projectedCount; i++)
                {
                    values[i] = decoders[i].ReadValue(rowIndex);
                }
                batch.Add(values);
            }

            yield return batch;
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
    private IEnumerable<int> EnumerateScanablePages(
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
        int chapterCount = _reader.Footer.Columns[0].ChapterZoneMaps.Count;

        bool hasVolumes = (_reader.Header.Flags & DatumFileFlagsV2.HasVolumeZoneMaps) != 0
            && _reader.Footer.Columns[0].VolumeZoneMaps is { Count: > 0 };

        // When the file emits volume zone maps walk volumes; otherwise
        // the volume tier is collapsed and we walk chapters directly.
        int volumeIterCount = hasVolumes ? _reader.Footer.Columns[0].VolumeZoneMaps!.Count : 1;

        for (int v = 0; v < volumeIterCount; v++)
        {
            if (hasVolumes && CanSkipVolume(v, filterHint, filterSchemaIndex, statsArena))
            {
                continue;
            }

            int chapterStart = hasVolumes ? v * chaptersPerVolume : 0;
            int chapterEnd = hasVolumes
                ? Math.Min(chapterStart + chaptersPerVolume, chapterCount)
                : chapterCount;

            for (int c = chapterStart; c < chapterEnd; c++)
            {
                if (CanSkipChapter(c, filterHint, filterSchemaIndex, statsArena))
                {
                    continue;
                }

                int pageStart = c * pagesPerChapter;
                int pageEnd = Math.Min(pageStart + pagesPerChapter, pageCount);

                for (int p = pageStart; p < pageEnd; p++)
                {
                    if (CanSkipPage(p, filterHint, filterSchemaIndex, statsArena))
                    {
                        continue;
                    }
                    yield return p;
                }
            }
        }
    }

    private bool CanSkipVolume(int volumeIndex, Expression filter, Dictionary<string, int> filterSchemaIndex, Arena arena)
    {
        // Volume row count = sum of chapter row counts in this volume.
        // Chapter row count = sum of page row counts in that chapter.
        // We only need a bound; passing pageCount * pageSize is fine as a
        // ceiling for the predicate evaluator's null-vs-row arithmetic.
        long rowCount = ComputeVolumeRowCount(volumeIndex);
        Dictionary<string, ColumnStatisticsRange> stats = new(filterSchemaIndex.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int schemaIdx) in filterSchemaIndex)
        {
            DatumZoneMap zoneMap = _reader.Footer.Columns[schemaIdx].VolumeZoneMaps![volumeIndex];
            stats[name] = MakeRange(zoneMap, rowCount, arena);
        }
        using ColumnStatisticsRangeLookup lookup = new(stats);
        return StatisticsPredicateEvaluator.CanSkipPartition(filter, lookup, arena);
    }

    private bool CanSkipChapter(int chapterIndex, Expression filter, Dictionary<string, int> filterSchemaIndex, Arena arena)
    {
        long rowCount = ComputeChapterRowCount(chapterIndex);
        Dictionary<string, ColumnStatisticsRange> stats = new(filterSchemaIndex.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int schemaIdx) in filterSchemaIndex)
        {
            DatumZoneMap zoneMap = _reader.Footer.Columns[schemaIdx].ChapterZoneMaps[chapterIndex];
            stats[name] = MakeRange(zoneMap, rowCount, arena);
        }
        using ColumnStatisticsRangeLookup lookup = new(stats);
        return StatisticsPredicateEvaluator.CanSkipPartition(filter, lookup, arena);
    }

    private bool CanSkipPage(int pageIndex, Expression filter, Dictionary<string, int> filterSchemaIndex, Arena arena)
    {
        int rowCount = _reader.Footer.Columns[0].Pages[pageIndex].RowCount;
        Dictionary<string, ColumnStatisticsRange> stats = new(filterSchemaIndex.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int schemaIdx) in filterSchemaIndex)
        {
            DatumZoneMap? zoneMap = _reader.Footer.Columns[schemaIdx].Pages[pageIndex].ZoneMap;
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

    private long ComputeChapterRowCount(int chapterIndex)
    {
        int pagesPerChapter = DatumFormatV2.PagesPerChapter;
        int pageStart = chapterIndex * pagesPerChapter;
        var pages = _reader.Footer.Columns[0].Pages;
        int pageEnd = Math.Min(pageStart + pagesPerChapter, pages.Count);
        long total = 0;
        for (int p = pageStart; p < pageEnd; p++) total += pages[p].RowCount;
        return total;
    }

    private long ComputeVolumeRowCount(int volumeIndex)
    {
        int chaptersPerVolume = DatumFormatV2.ChaptersPerVolume;
        int chapterStart = volumeIndex * chaptersPerVolume;
        int chapterCount = _reader.Footer.Columns[0].ChapterZoneMaps.Count;
        int chapterEnd = Math.Min(chapterStart + chaptersPerVolume, chapterCount);
        long total = 0;
        for (int c = chapterStart; c < chapterEnd; c++) total += ComputeChapterRowCount(c);
        return total;
    }

    /// <summary>
    /// Builds a case-insensitive dictionary mapping every column name
    /// referenced in <paramref name="filter"/> to its schema column
    /// index. Columns not present in the schema are silently dropped
    /// (the predicate evaluator falls back to "do not skip" for those).
    /// </summary>
    private Dictionary<string, int>? BuildFilterColumnIndex(Expression filter)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(filter))
        {
            if (result.ContainsKey(columnName)) continue;
            for (int i = 0; i < _schema.Columns.Count; i++)
            {
                if (string.Equals(_schema.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
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
        ColumnLookup columnLookup = ResolveProjection(_schema, requiredColumns);
        int projectedCount = columnLookup.Count;
        int[] schemaIndices = new int[projectedCount];
        for (int i = 0; i < projectedCount; i++)
        {
            schemaIndices[i] = columnLookup.GetSchemaColumnIndex(i);
        }

        DatumFileReaderV2 sessionReader = DatumFileReaderV2.Open(_descriptor.FilePath);
        SidecarReadStore? sessionSidecar = null;
        try
        {
            sessionSidecar = TryOpenSidecar(_descriptor.FilePath, sessionReader);
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
        _sidecar?.Dispose();
        _reader.Dispose();
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Builds an engine-facing <see cref="Schema"/> from the v2 footer's
    /// column descriptors. <see cref="ColumnInfo.ArrayElementKind"/> is
    /// populated for typed-array columns so downstream type inference
    /// stays accurate.
    /// </summary>
    private static Schema BuildSchema(FooterV2 footer)
    {
        ColumnInfo[] columns = new ColumnInfo[footer.Columns.Count];
        for (int i = 0; i < footer.Columns.Count; i++)
        {
            ColumnDescriptorV2 d = footer.Columns[i].Descriptor;
            DataKind? arrayElementKind = d.IsArray ? d.Kind : null;
            columns[i] = new ColumnInfo(d.Name, d.Kind, d.IsNullable, arrayElementKind);
        }
        return new Schema(columns);
    }

    private static ColumnLookup ResolveProjection(Schema schema, IReadOnlySet<string>? requiredColumns)
    {
        if (requiredColumns is null)
        {
            return new ColumnLookup(schema.Columns);
        }

        (int index, int schemaIndex, string name)[] projected = new (int, int, string)[requiredColumns.Count];
        int index = 0;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (requiredColumns.Contains(schema.Columns[i].Name))
            {
                projected[index] = (index, i, schema.Columns[i].Name);
                index++;
            }
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
    /// <c>(null, null)</c> when absent. Multiple scan operators share the
    /// single mapped view via the kept <see cref="MappedSourceIndexSet"/>.
    /// </summary>
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
