using System.Buffers;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads <c>.datum</c> native column-store files via the <see cref="DatumFileReader"/>.
/// Supports projection pushdown, zone-map-based row group pruning when a filter hint
/// is provided by the query engine, and random-access row reads via row group seeking.
/// </summary>
public sealed class DatumFileTableProvider : ITableProvider, IDisposable
{
    private const int DefaultBatchSize = 1024;

    /// <summary>
    /// Initializes the provider with the given descriptor and pool.
    /// </summary>
    /// <param name="descriptor">The table descriptor containing metadata and file path.</param>
    /// <param name="pool">The resource pool for managing provider resources.</param>
    public DatumFileTableProvider(TableDescriptor descriptor, Pool pool)
    {
        Descriptor = descriptor;
        Reader = DatumFileReader.Open(descriptor.FilePath);
        Pool = pool;
    }

    private DatumFileReader Reader { get; }

    private Pool Pool { get;}

    /// <inheritdoc/>
    private TableDescriptor Descriptor { get; }

    /// <summary>
    /// Optional value store for decoding string columns into Arena-backed values.
    /// Set by operators that have an <see cref="DatumIngest.Execution.ExecutionContext"/>.
    /// </summary>
    public IValueStore Store { get; set; } = new Arena();

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
    public Manifest.QueryResultsManifest? GetManifest()
        => throw new NotSupportedException("DatumFileTableProvider does not support manifests.");

    /// <inheritdoc/>
    public Indexing.SourceIndex? GetSourceIndex()
        => throw new NotSupportedException("DatumFileTableProvider does not support source indices.");

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Schema schema = Reader.Schema;

        // Resolve which column indices to decode (projection pushdown).
        int[] projectedIndices = ResolveProjection(schema, requiredColumns);
        string[] projectedNames = Array.ConvertAll(projectedIndices, i => schema.Columns[i].Name);

        Dictionary<string, int> nameIndex = BuildNameIndex(projectedNames);

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

            for (int colIndex = 0; colIndex < projectedIndices.Length; colIndex++)
            {
                DatumColumnChunkDescriptor chunk = rgDescriptor.ColumnChunks[projectedIndices[colIndex]];
                int compressedSize = (int)chunk.CompressedByteLength;
                if (compressedSize > maxCompressedPageSize) maxCompressedPageSize = compressedSize;
                int uncompressedSize = (int)chunk.UncompressedByteLength;
                if (uncompressedSize > maxUncompressedPageSize) maxUncompressedPageSize = uncompressedSize;
            }
        }

        DataValue[][] columns = new DataValue[projectedIndices.Length][];

        for (int colIndex = 0; colIndex < projectedIndices.Length; colIndex++)
        {
            columns[colIndex] = new DataValue[maxRowGroupSize];
        }

        byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(maxCompressedPageSize);
        byte[] decompressedBuffer = ArrayPool<byte>.Shared.Rent(maxUncompressedPageSize);
        RowBatch? batch = null;

        try
        {
            for (int rgIndex = 0; rgIndex < Reader.RowGroupCount; rgIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DatumRowGroupDescriptor rowGroupDescriptor = Reader.GetRowGroupDescriptor(rgIndex);

                // Zone map pruning: only attempted when a filter hint was provided.
                if (filterHint is not null && filterColumnNames is not null)
                {
                    Dictionary<string, ColumnStatisticsRange> statistics =
                        BuildStatistics(schema, rowGroupDescriptor, filterColumnNames);

                    if (StatisticsPredicateEvaluator.CanSkipPartition(filterHint, statistics))
                    {
                        continue;
                    }
                }

                Reader.ReadColumnsInto(rgIndex, projectedIndices, columns, compressedBuffer, decompressedBuffer);

                // Skip fully-deleted row groups without emitting any rows.
                if (rowGroupDescriptor.ActiveRowCount == 0)
                {
                    continue;
                }

                long rowCount = rowGroupDescriptor.RowCount;
                for (long rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Skip tombstoned rows.
                    // TODO: Convert to long
                    if (rowGroupDescriptor.IsRowDeleted((int)rowIndex))
                    {
                        continue;
                    }

                    batch ??= Pool.RentRowBatch(DefaultBatchSize);
                    
                    DataValue[] values = Pool.RentDataValues(projectedIndices.Length);
                    for (int colPos = 0; colPos < projectedIndices.Length; colPos++)
                    {
                        values[colPos] = columns[colPos][rowIndex];
                    }

                    batch.Add(new Row(projectedNames, values, nameIndex));

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = null;
                    }
                }
            }

            if (batch is not null && batch.Count > 0)
            {
                yield return batch;
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
        // Snapshot the value store at open time so concurrent Store mutations from other
        // callers don't affect this session. The session is self-contained from here on.
        IValueStore storeSnapshot = Store;

        try
        {
            Schema schema = GetSchema();

            int[] projectedIndices = ResolveProjection(schema, requiredColumns);
            string[] projectedNames = Array.ConvertAll(projectedIndices, i => schema.Columns[i].Name);
            Dictionary<string, int> nameIndex = BuildNameIndex(projectedNames);

            // Size scratch buffers to the largest row group / page in the file.
            int maxRowGroupSize = 0;
            int maxCompressed = 0;
            int maxUncompressed = 0;
            for (int rg = 0; rg < Reader.RowGroupCount; rg++)
            {
                DatumRowGroupDescriptor rgd = Reader.GetRowGroupDescriptor(rg);
                int rgRows = (int)rgd.RowCount;
                if (rgRows > maxRowGroupSize) maxRowGroupSize = rgRows;

                for (int ci = 0; ci < projectedIndices.Length; ci++)
                {
                    DatumColumnChunkDescriptor chunk = rgd.ColumnChunks[projectedIndices[ci]];
                    int c = (int)chunk.CompressedByteLength;
                    int u = (int)chunk.UncompressedByteLength;
                    if (c > maxCompressed) maxCompressed = c;
                    if (u > maxUncompressed) maxUncompressed = u;
                }
            }

            DataValue[][] columnBuffers = new DataValue[projectedIndices.Length][];
            for (int ci = 0; ci < projectedIndices.Length; ci++)
            {
                columnBuffers[ci] = maxRowGroupSize > 0
                    ? Pool.RentDataValues(maxRowGroupSize)
                    : [];
            }

            byte[] compressedBuffer = maxCompressed > 0
                ? ArrayPool<byte>.Shared.Rent(maxCompressed)
                : [];
            byte[] decompressedBuffer = maxUncompressed > 0
                ? ArrayPool<byte>.Shared.Rent(maxUncompressed)
                : [];

            return new DatumFileSeekSession(
                Pool, Reader, projectedIndices, projectedNames, nameIndex,
                columnBuffers, compressedBuffer, decompressedBuffer);
        }
        catch
        {
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose() { }

    private static int[] ResolveProjection(Schema schema, IReadOnlySet<string>? requiredColumns)
    {
        if (requiredColumns is null)
        {
            int[] all = new int[schema.Columns.Count];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            return all;
        }

        List<int> projected = new(requiredColumns.Count);
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (requiredColumns.Contains(schema.Columns[i].Name))
            {
                projected.Add(i);
            }
        }

        return projected.ToArray();
    }

    private static Dictionary<string, int> BuildNameIndex(string[] projectedNames)
    {
        Dictionary<string, int> nameIndex = new(projectedNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < projectedNames.Length; i++)
        {
            nameIndex[projectedNames[i]] = i;
        }

        return nameIndex;
    }

    private Dictionary<string, ColumnStatisticsRange> BuildStatistics(
        Schema schema,
        DatumRowGroupDescriptor rowGroup,
        HashSet<string> filterColumnNames)
    {
        Dictionary<string, ColumnStatisticsRange> statistics =
            new(filterColumnNames.Count, StringComparer.OrdinalIgnoreCase);

        for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
        {
            string columnName = schema.Columns[columnIndex].Name;
            if (!filterColumnNames.Contains(columnName)) continue;

            DatumZoneMap zoneMap = rowGroup.ColumnChunks[columnIndex].ZoneMap;
            statistics[columnName] = new ColumnStatisticsRange(
                DataValueComparer.MakeFromBoxed(zoneMap.Kind, zoneMap.Minimum, Store),
                DataValueComparer.MakeFromBoxed(zoneMap.Kind, zoneMap.Maximum, Store),
                zoneMap.NullCount,
                rowGroup.RowCount);
        }

        return statistics;
    }
}
