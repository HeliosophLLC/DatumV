using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Indexing.Fts;
using DatumIngest.Ingestion;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Statistics;

namespace DatumIngest.Catalog.Providers;

public sealed partial class DatumFileTableProviderV2
{
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
}
