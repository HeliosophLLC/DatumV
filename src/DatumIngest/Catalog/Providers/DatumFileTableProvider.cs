using System.Buffers;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads <c>.datum</c> native column-store files via the <see cref="DatumFileReader"/>.
/// Supports projection pushdown, zone-map-based row group pruning when a filter hint
/// is provided by the query engine, and random-access row reads via row group seeking.
/// </summary>
public sealed class DatumFileTableProvider : ITableProvider, IDisposable
{
    private const int DefaultBatchSize = 1024;

    private readonly QueryResultsManifest? _manifest;
    private readonly MappedSourceIndexSet? _mappedIndexSet;
    private readonly SourceIndex? _sourceIndex;

    /// <summary>
    /// Initializes the provider with the given descriptor and pool. Auto-discovers
    /// <c>.datum-manifest</c> and <c>.datum-index</c> sidecars alongside the source
    /// file and caches them so <see cref="GetManifest"/> and <see cref="GetSourceIndex"/>
    /// return live data without re-parsing on every call. The source index is held via
    /// a <see cref="MappedSourceIndexSet"/> so multiple scans share the mmap.
    /// </summary>
    /// <param name="descriptor">The table descriptor containing metadata and file path.</param>
    /// <param name="pool">The resource pool for managing provider resources.</param>
    public DatumFileTableProvider(TableDescriptor descriptor, Pool pool)
    {
        Descriptor = descriptor;
        Reader = DatumFileReader.Open(descriptor.FilePath);
        Pool = pool;

        _manifest = TryLoadManifest(descriptor);
        (_mappedIndexSet, _sourceIndex) = TryLoadSourceIndex(descriptor);
    }

    private DatumFileReader Reader { get; }

    private Pool Pool { get;}

    /// <inheritdoc/>
    private TableDescriptor Descriptor { get; }

    /// <inheritdoc/>
    public long GetRowCount()
    {
        // When tombstones are present, report the active (non-deleted) row count
        // so the query planner sees the true logical size.
        if (Reader.Flags.HasFlag(DatumFileFlags.HasTombstones))
        {
            long activeCount = 0;
            for (int rowGroupIndex = 0; rowGroupIndex < Reader.RowGroupCount; rowGroupIndex++)
            {
                activeCount += Reader.GetRowGroupDescriptor(rowGroupIndex).ActiveRowCount;
            }

            return activeCount;
        }

        return Reader.TotalRowCount;
    }

    /// <inheritdoc/>
    public string Name => Descriptor.Name;

    /// <inheritdoc/>
    public bool Seekable => throw new Exception("TODO: Need to check if index side-cars are present for this file.");

    /// <inheritdoc/>
    public Schema GetSchema() => Reader.Schema;

    /// <inheritdoc/>
    public QueryResultsManifest? GetManifest() => _manifest;

    /// <inheritdoc/>
    public SourceIndex? GetSourceIndex() => _sourceIndex;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Schema schema = Reader.Schema;

        // Resolve which column indices to decode (projection pushdown).
        ColumnLookup columnLookup = ResolveProjection(schema, requiredColumns);

        // Collect the column names referenced in the filter to limit statistics construction.
        HashSet<string>? filterColumnNames = null;
        if (filterHint is not null)
        {
            filterColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(filterHint))
            {
                filterColumnNames.Add(columnName);
            }
        }

        // Pre-allocate column buffers for the maximum row group size so that
        // DecodeInto writes directly into reused arrays, eliminating ~1,980
        // LOH DataValue[] allocations (one per column per row group).
        // Also find the largest compressed page so a single byte[] can be reused
        // across all columns and row groups instead of renting/returning per page.
        int maxRowGroupSize = 0;
        int maxCompressedPageSize = 0;
        int maxUncompressedPageSize = 0;
        for (int rgIndex = 0; rgIndex < Reader.RowGroupCount; rgIndex++)
        {
            DatumRowGroupDescriptor rgDescriptor = Reader.GetRowGroupDescriptor(rgIndex);
            int rowCount = (int)rgDescriptor.RowCount;
            if (rowCount > maxRowGroupSize) maxRowGroupSize = rowCount;

            for (int colIndex = 0; colIndex < columnLookup.Count; colIndex++)
            {
                DatumColumnChunkDescriptor chunk = rgDescriptor.ColumnChunks[columnLookup.GetSchemaColumnIndex(colIndex)];
                int compressedSize = (int)chunk.CompressedByteLength;
                if (compressedSize > maxCompressedPageSize) maxCompressedPageSize = compressedSize;
                int uncompressedSize = (int)chunk.UncompressedByteLength;
                if (uncompressedSize > maxUncompressedPageSize) maxUncompressedPageSize = uncompressedSize;
            }
        }

        byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(maxCompressedPageSize);
        byte[] decompressedBuffer = ArrayPool<byte>.Shared.Rent(maxUncompressedPageSize);
        RowBatch? batch = null;

        try
        {
            for (int rgIndex = 0; rgIndex < Reader.RowGroupCount; rgIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ColumnBatch columnBatch = Pool.RentColumnBatch(columnLookup, maxRowGroupSize);

                try
                {
                    DatumRowGroupDescriptor rowGroupDescriptor = Reader.GetRowGroupDescriptor(rgIndex);

                    // Zone map pruning: only attempted when a filter hint was provided.
                    if (filterHint is not null && filterColumnNames is not null)
                    {
                        using ColumnStatisticsRangeLookup statistics = BuildStatistics(schema, rowGroupDescriptor, filterColumnNames, columnBatch.Arena);

                        if (StatisticsPredicateEvaluator.CanSkipPartition(filterHint, statistics, columnBatch.Arena))
                        {
                            continue;
                        }
                    }

                    Reader.ReadColumnsInto(rgIndex, columnBatch, compressedBuffer, decompressedBuffer);

                    // Skip fully-deleted row groups without emitting any rows.
                    if (rowGroupDescriptor.ActiveRowCount == 0)
                    {
                        continue;
                    }

                    int rowCount = (int)rowGroupDescriptor.RowCount;
                    for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Skip tombstoned rows.
                        // TODO: Convert to long
                        if (rowGroupDescriptor.IsRowDeleted(rowIndex))
                        {
                            continue;
                        }

                        batch ??= Pool.RentRowBatch(columnLookup, DefaultBatchSize, columnBatch.Arena);
                        
                        DataValue[] values = Pool.RentDataValues(columnLookup.Count);
                        columnBatch.CopyRow(rowIndex, values);
 
                        batch.Add(values);

                        if (batch.IsFull)
                        {
                            yield return batch;
                            batch = null;
                        }
                    }

                    if (batch is not null && batch.Count > 0)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
                finally
                {
                    Pool.ReturnColumnBatch(columnBatch);
                }
            }


        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressedBuffer);
            ArrayPool<byte>.Shared.Return(decompressedBuffer);
        }
    }
    
    /// <inheritdoc/>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns)
    {
        Schema schema = GetSchema();
        ColumnLookup columnLookup = ResolveProjection(schema, requiredColumns);

        // Size scratch buffers to the largest row group / page in the file.
        int maxRowGroupSize = 0;
        int maxCompressed = 0;
        int maxUncompressed = 0;
        for (int rg = 0; rg < Reader.RowGroupCount; rg++)
        {
            DatumRowGroupDescriptor rgd = Reader.GetRowGroupDescriptor(rg);
            int rgRows = (int)rgd.RowCount;
            if (rgRows > maxRowGroupSize) maxRowGroupSize = rgRows;

            for (int ci = 0; ci < columnLookup.Count; ci++)
            {
                DatumColumnChunkDescriptor chunk = rgd.ColumnChunks[columnLookup.GetSchemaColumnIndex(ci)];
                int c = (int)chunk.CompressedByteLength;
                int u = (int)chunk.UncompressedByteLength;
                if (c > maxCompressed) maxCompressed = c;
                if (u > maxUncompressed) maxUncompressed = u;
            }
        }

        ColumnBatch columnBatch = Pool.RentColumnBatch(columnLookup, maxRowGroupSize);

        byte[] compressedBuffer = maxCompressed > 0
            ? ArrayPool<byte>.Shared.Rent(maxCompressed)
            : [];
        byte[] decompressedBuffer = maxUncompressed > 0
            ? ArrayPool<byte>.Shared.Rent(maxUncompressed)
            : [];

        return new DatumFileSeekSession(Pool, Reader, columnBatch, compressedBuffer, decompressedBuffer);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _mappedIndexSet?.Dispose();
        Reader.Dispose();
    }

    /// <summary>
    /// Attempts to load a <c>.datum-manifest</c> sidecar alongside the source file.
    /// Returns the per-table <see cref="QueryResultsManifest"/> matching this provider's
    /// table name, or <c>null</c> when the sidecar is absent or contains no entry for
    /// this table. A malformed sidecar throws (corruption should be visible, not swallowed).
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
    /// Attempts to memory-map a <c>.datum-index</c> sidecar alongside the source file.
    /// Returns the owning <see cref="MappedSourceIndexSet"/> (so the mapping outlives this
    /// call) and the resolved <see cref="SourceIndex"/> for this table, or <c>(null, null)</c>
    /// when the sidecar is absent or has no entry for this table. Multiple scan operators
    /// share the single mapped view via the <see cref="MappedSourceIndexSet"/> the provider holds.
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
    /// Resolves a sidecar entry by the registered catalog table name, falling back to the
    /// file-convention-derived name. Handles the common case where a sidecar was written
    /// with the auto-derived name (e.g. <c>orders_csv</c>) while the catalog registers
    /// a different logical name, and the reverse.
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

    private ColumnStatisticsRangeLookup BuildStatistics(
        Schema schema,
        DatumRowGroupDescriptor rowGroup,
        HashSet<string> filterColumnNames,
        Arena arena)
    {
        Dictionary<string, ColumnStatisticsRange> statistics =
            new(filterColumnNames.Count, StringComparer.OrdinalIgnoreCase);

        for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
        {
            string columnName = schema.Columns[columnIndex].Name;
            if (!filterColumnNames.Contains(columnName)) continue;

            DatumZoneMap zoneMap = rowGroup.ColumnChunks[columnIndex].ZoneMap;
            statistics[columnName] = new ColumnStatisticsRange(
                DataValueComparer.MakeFromBoxed(zoneMap.Kind, zoneMap.Minimum, arena),
                DataValueComparer.MakeFromBoxed(zoneMap.Kind, zoneMap.Maximum, arena),
                zoneMap.NullCount,
                rowGroup.RowCount);
        }

        return new ColumnStatisticsRangeLookup(statistics);
    }
}
