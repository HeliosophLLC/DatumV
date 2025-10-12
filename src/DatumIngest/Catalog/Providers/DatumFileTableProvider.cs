using System.Buffers;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads <c>.datum</c> native column-store files via the <see cref="DatumFileReader"/>.
/// Supports projection pushdown, zone-map-based row group pruning when a filter hint
/// is provided by the query engine, and random-access row reads via row group seeking.
/// </summary>
public sealed class DatumFileTableProvider : ITableProvider, IFilterableTableProvider, ISeekableTableProvider, IColumnBatchProvider, IDisposable
{
    private const int DefaultBatchSize = 1024;

    // Reused across multiple ReadRowRangeAsync calls within the same scan session.
    // Opening a DatumFileReader re-reads and decompresses all row-group metadata;
    // amortising that cost over the full index traversal is critical for B+Tree scans.
    private DatumFileReader? _cachedReader;
    private string? _cachedReaderPath;

    // Pre-allocated column buffers for ReadRowRangeAsync — mirrors the optimization
    // in OpenCoreAsync that eliminates LOH DataValue[] allocations per row group.
    // Rebuilt when the file or projected column set changes.
    private DataValue[][]? _seekColumnBuffers;
    private byte[]? _seekCompressedBuffer;
    private byte[]? _seekDecompressedBuffer;
    private int[]? _seekProjectedIndices;
    private string[]? _seekProjectedNames;
    private Dictionary<string, int>? _seekNameIndex;

    /// <summary>
    /// Optional value store for decoding string columns into Arena-backed values.
    /// Set by operators that have an <see cref="DatumIngest.Execution.ExecutionContext"/>
    /// before calling <see cref="ITableProvider.OpenAsync"/>.
    /// </summary>
    public IValueStore Store { get; set; } = new Arena();

    /// <summary>Total number of row groups examined in the most recent read.</summary>
    public int TotalRowGroups { get; private set; }

    /// <summary>Number of row groups skipped by zone-map pruning in the most recent read.</summary>
    public int PrunedRowGroups { get; private set; }

    /// <inheritdoc/>
    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);
        return Task.FromResult(reader.Schema);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken)
        => OpenCoreAsync(descriptor, requiredColumns, filterHint: null, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression filterHint,
        CancellationToken cancellationToken)
        => OpenCoreAsync(descriptor, requiredColumns, filterHint, cancellationToken);

    /// <inheritdoc/>
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);

        // When tombstones are present, report the active (non-deleted) row count
        // so the query planner sees the true logical size.
        long rowCount = reader.TotalRowCount;
        if (reader.Flags.HasFlag(DatumFileFlags.HasTombstones))
        {
            long activeCount = 0;
            for (int rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
            {
                activeCount += reader.GetRowGroupDescriptor(rowGroupIndex).ActiveRowCount;
            }

            rowCount = activeCount;
        }

        return Task.FromResult(new ProviderCapabilities(
            rowCount,
            EstimatedRowSizeBytes: null,
            SupportsSeek: true,
            ColumnCosts: new Dictionary<string, ColumnCost>()));
    }

    private async IAsyncEnumerable<RowBatch> OpenCoreAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);
        reader.Store = Store;
        Schema schema = reader.Schema;

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

        TotalRowGroups = reader.RowGroupCount;
        PrunedRowGroups = 0;

        // Pre-allocate column buffers for the maximum row group size so that
        // DecodeInto writes directly into reused arrays, eliminating ~1,980
        // LOH DataValue[] allocations (one per column per row group).
        // Also find the largest compressed page so a single byte[] can be reused
        // across all columns and row groups instead of renting/returning per page.
        int maxRowGroupSize = 0;
        int maxCompressedPageSize = 0;
        int maxUncompressedPageSize = 0;
        for (int rgIndex = 0; rgIndex < reader.RowGroupCount; rgIndex++)
        {
            DatumRowGroupDescriptor rgDescriptor = reader.GetRowGroupDescriptor(rgIndex);
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
        for (int rgIndex = 0; rgIndex < reader.RowGroupCount; rgIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DatumRowGroupDescriptor rowGroupDescriptor = reader.GetRowGroupDescriptor(rgIndex);

            // Zone map pruning: only attempted when a filter hint was provided.
            if (filterHint is not null && filterColumnNames is not null)
            {
                Dictionary<string, ColumnStatisticsRange> statistics =
                    BuildStatistics(schema, rowGroupDescriptor, filterColumnNames);

                if (StatisticsPredicateEvaluator.CanSkipPartition(filterHint, statistics))
                {
                    PrunedRowGroups++;
                    continue;
                }
            }

            int rowCount = (int)rowGroupDescriptor.RowCount;
            reader.ReadColumnsInto(rgIndex, projectedIndices, columns, compressedBuffer, decompressedBuffer);

            // Skip fully-deleted row groups without emitting any rows.
            if (rowGroupDescriptor.ActiveRowCount == 0)
            {
                continue;
            }

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip tombstoned rows.
                if (rowGroupDescriptor.IsRowDeleted(rowIndex))
                {
                    continue;
                }

                DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(projectedIndices.Length);
                for (int colPos = 0; colPos < projectedIndices.Length; colPos++)
                {
                    values[colPos] = columns[colPos][rowIndex];
                }

                batch ??= RowBatch.Rent(DefaultBatchSize);
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
    public async IAsyncEnumerable<ColumnBatch> OpenColumnBatchAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);
        reader.Store = Store;
        Schema schema = reader.Schema;

        int[] projectedIndices = ResolveProjection(schema, requiredColumns);
        string[] projectedNames = Array.ConvertAll(projectedIndices, i => schema.Columns[i].Name);
        Dictionary<string, int> nameIndex = BuildNameIndex(projectedNames);

        HashSet<string>? filterColumnNames = null;
        if (filterHint is not null)
        {
            filterColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(filterHint))
            {
                filterColumnNames.Add(columnName);
            }
        }

        TotalRowGroups = reader.RowGroupCount;
        PrunedRowGroups = 0;

        for (int rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DatumRowGroupDescriptor rowGroupDescriptor = reader.GetRowGroupDescriptor(rowGroupIndex);

            if (filterHint is not null && filterColumnNames is not null)
            {
                Dictionary<string, ColumnStatisticsRange> statistics =
                    BuildStatistics(schema, rowGroupDescriptor, filterColumnNames);

                if (StatisticsPredicateEvaluator.CanSkipPartition(filterHint, statistics))
                {
                    PrunedRowGroups++;
                    continue;
                }
            }

            // Skip row groups where all rows have been tombstoned.
            if (rowGroupDescriptor.ActiveRowCount == 0)
            {
                continue;
            }

            ColumnBatch fullBatch = reader.ReadColumnsAsColumnBatch(
                rowGroupIndex, projectedIndices, projectedNames, nameIndex);

            // When some rows are tombstoned, copy only the active rows into a new batch.
            if (rowGroupDescriptor.TombstoneBitmap is not null
                && rowGroupDescriptor.ActiveRowCount < rowGroupDescriptor.RowCount)
            {
                int activeCount = (int)rowGroupDescriptor.ActiveRowCount;
                ColumnBatch filtered = ColumnBatch.Create(projectedNames, nameIndex, activeCount);
                int destinationIndex = 0;

                for (int rowIndex = 0; rowIndex < (int)rowGroupDescriptor.RowCount; rowIndex++)
                {
                    if (rowGroupDescriptor.IsRowDeleted(rowIndex)) continue;

                    for (int columnIndex = 0; columnIndex < projectedIndices.Length; columnIndex++)
                    {
                        filtered.GetColumnBuffer(columnIndex)[destinationIndex] =
                            fullBatch.GetColumnBuffer(columnIndex)[rowIndex];
                    }

                    destinationIndex++;
                }

                filtered.SetRowCount(activeCount);
                fullBatch.Dispose();
                yield return filtered;
            }
            else
            {
                yield return fullBatch;
            }
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ReadRowRangeAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_cachedReader is null || _cachedReaderPath != descriptor.FilePath)
        {
            _cachedReader?.Dispose();
            _cachedReader = DatumFileReader.Open(descriptor.FilePath);
            _cachedReader.Store = Store;
            _cachedReaderPath = descriptor.FilePath;
            // Invalidate seek buffers — they are sized to the previous file's row groups.
            InvalidateSeekBuffers();
        }

        DatumFileReader reader = _cachedReader;
        Schema schema = reader.Schema;

        int[] projectedIndices = ResolveProjection(schema, requiredColumns);

        // Rebuild seek buffers when the projected column set changes.
        // In the common case (INLJ probes the same join column each call) the
        // projection is stable and the buffers are reused across all seeks.
        if (!IndicesEqual(projectedIndices, _seekProjectedIndices))
        {
            RebuildSeekBuffers(reader, projectedIndices, schema);
        }

        string[] projectedNames = _seekProjectedNames!;
        Dictionary<string, int> nameIndex = _seekNameIndex!;
        DataValue[][] columns = _seekColumnBuffers!;
        byte[] compressedBuffer = _seekCompressedBuffer!;
        byte[] decompressedBuffer = _seekDecompressedBuffer!;

        long endRow = startRow + count;
        long cumulativeRow = 0;
        int emitted = 0;

        RowBatch? batch = null;

        for (int rgIndex = 0; rgIndex < reader.RowGroupCount && emitted < count; rgIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DatumRowGroupDescriptor rowGroupDescriptor = reader.GetRowGroupDescriptor(rgIndex);
            long rgRowCount = rowGroupDescriptor.RowCount;
            long rgEnd = cumulativeRow + rgRowCount;

            // Skip row groups entirely before the requested range.
            if (rgEnd <= startRow)
            {
                cumulativeRow = rgEnd;
                continue;
            }

            // Stop once we've passed the requested range.
            if (cumulativeRow >= endRow)
            {
                break;
            }

            // Decode directly into pre-allocated column buffers — no LOH allocation.
            reader.ReadColumnsInto(rgIndex, projectedIndices, columns, compressedBuffer, decompressedBuffer);

            // Calculate the slice within this row group.
            int sliceStart = (int)Math.Max(startRow - cumulativeRow, 0);
            int sliceEnd = (int)Math.Min(endRow - cumulativeRow, rgRowCount);

            for (int rowIndex = sliceStart; rowIndex < sliceEnd && emitted < count; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip tombstoned rows.
                if (rowGroupDescriptor.IsRowDeleted(rowIndex))
                {
                    continue;
                }

                DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(projectedIndices.Length);
                for (int colPos = 0; colPos < projectedIndices.Length; colPos++)
                {
                    values[colPos] = columns[colPos][rowIndex];
                }

                batch ??= RowBatch.Rent(Math.Min(count, DefaultBatchSize));
                batch.Add(new Row(projectedNames, values, nameIndex));
                emitted++;

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }

            cumulativeRow = rgEnd;
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Allocates the per-call-reusable buffers for <see cref="ReadRowRangeAsync"/>:
    /// one <see cref="DataValue"/> array per projected column (sized to the largest
    /// row group), plus byte buffers for compressed/decompressed page data.
    /// These replace the per-call <c>ReadColumns</c> allocations that previously
    /// produced LOH objects (~1.57 MB each) on every INLJ point-seek.
    /// </summary>
    private void RebuildSeekBuffers(DatumFileReader reader, int[] projectedIndices, Schema schema)
    {
        InvalidateSeekBuffers();

        int maxRowGroupSize = 0;
        int maxCompressed = 0;
        int maxUncompressed = 0;

        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            DatumRowGroupDescriptor rgd = reader.GetRowGroupDescriptor(rg);
            int rgRows = (int)rgd.RowCount;
            if (rgRows > maxRowGroupSize) maxRowGroupSize = rgRows;

            for (int ci = 0; ci < projectedIndices.Length; ci++)
            {
                DatumColumnChunkDescriptor chunk = rgd.ColumnChunks[projectedIndices[ci]];
                int compressed = (int)chunk.CompressedByteLength;
                int uncompressed = (int)chunk.UncompressedByteLength;
                if (compressed > maxCompressed) maxCompressed = compressed;
                if (uncompressed > maxUncompressed) maxUncompressed = uncompressed;
            }
        }

        // Rent from GlobalBufferPool so these LOH arrays survive across executions
        // without triggering repeated Gen2 collections. Returned in InvalidateSeekBuffers.
        DataValue[][] columnBuffers = new DataValue[projectedIndices.Length][];
        for (int ci = 0; ci < projectedIndices.Length; ci++)
        {
            columnBuffers[ci] = maxRowGroupSize > 0
                ? DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(maxRowGroupSize)
                : [];
        }

        string[] projectedNames = Array.ConvertAll(projectedIndices, i => schema.Columns[i].Name);

        _seekProjectedIndices = projectedIndices;
        _seekProjectedNames = projectedNames;
        _seekNameIndex = BuildNameIndex(projectedNames);
        _seekColumnBuffers = columnBuffers;
        _seekCompressedBuffer = maxCompressed > 0
            ? ArrayPool<byte>.Shared.Rent(maxCompressed)
            : [];
        _seekDecompressedBuffer = maxUncompressed > 0
            ? ArrayPool<byte>.Shared.Rent(maxUncompressed)
            : [];
    }

    private void InvalidateSeekBuffers()
    {
        if (_seekColumnBuffers is not null)
        {
            foreach (DataValue[] buffer in _seekColumnBuffers)
            {
                if (buffer.Length > 0) DatumIngest.Execution.Pooling.GlobalPool.Backing.Return(buffer);
            }
        }

        if (_seekCompressedBuffer is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(_seekCompressedBuffer);
        }

        if (_seekDecompressedBuffer is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(_seekDecompressedBuffer);
        }

        _seekColumnBuffers = null;
        _seekCompressedBuffer = null;
        _seekDecompressedBuffer = null;
        _seekProjectedIndices = null;
        _seekProjectedNames = null;
        _seekNameIndex = null;
    }

    private static bool IndicesEqual(int[] a, int[]? b)
    {
        if (b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cachedReader?.Dispose();
        _cachedReader = null;
        InvalidateSeekBuffers();
    }

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

    private static Dictionary<string, ColumnStatisticsRange> BuildStatistics(
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
                zoneMap.Minimum,
                zoneMap.Maximum,
                zoneMap.NullCount,
                rowGroup.RowCount);
        }

        return statistics;
    }
}
