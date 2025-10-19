using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Scans rows in the order defined
/// by an <see cref="IColumnIndex"/>. Entries in the index are walked
/// sequentially, and each row is fetched via random access — producing sorted
/// output without materializing and sorting the entire dataset.
/// </summary>
/// <remarks>
/// <para>
/// The query planner substitutes this operator for a
/// <see cref="ScanOperator"/> + <see cref="OrderByOperator"/> combination when
/// a sorted index exists for the ORDER BY column and the provider supports seeking.
/// </para>
/// </remarks>
public sealed class IndexScanOperator : IQueryOperator
{
    /// <summary>
    /// Maximum number of index entries accumulated before flushing, even when all
    /// entries belong to the same chunk. Without this cap, a single-chunk file
    /// (common for CSV imports) would accumulate all entries before yielding any
    /// rows — blocking early termination from downstream LIMIT or GROUP BY operators.
    /// </summary>
    private const int MaxIndexEntriesPerFlush = 8192;

    private readonly TableDescriptor _descriptor;
    private readonly IReadOnlySet<string>? _requiredColumns;
    private readonly IColumnIndex _columnIndex;
    private readonly IReadOnlyList<IndexChunk> _chunks;
    private readonly bool _descending;
    private readonly string _columnName;

    /// <summary>
    /// Creates an index scan operator.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the data source.</param>
    /// <param name="requiredColumns">Columns needed downstream; null means all columns.</param>
    /// <param name="columnIndex">The column index defining scan order.</param>
    /// <param name="chunks">The chunk directory for translating chunk-relative offsets to absolute row positions.</param>
    /// <param name="descending">Whether to walk the index in reverse (descending) order.</param>
    /// <param name="columnName">The name of the indexed column, used for plan descriptions.</param>
    public IndexScanOperator(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        IColumnIndex columnIndex,
        IReadOnlyList<IndexChunk> chunks,
        bool descending,
        string columnName = "unknown")
    {
        _descriptor = descriptor;
        _requiredColumns = requiredColumns;
        _columnIndex = columnIndex;
        _chunks = chunks;
        _descending = descending;
        _columnName = columnName;
    }

    /// <summary>The table descriptor this operator scans.</summary>
    public TableDescriptor Descriptor => _descriptor;

    /// <summary>The set of columns requested for projection pushdown.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <summary>Whether the scan walks the index in descending order.</summary>
    public bool Descending => _descending;

    /// <summary>The name of the indexed column.</summary>
    public string ColumnName => _columnName;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["table"] = _descriptor.Name,
            ["provider"] = _descriptor.Provider,
            ["column"] = _columnName,
            ["direction"] = _descending ? "DESC" : "ASC",
        };

        if (_requiredColumns is not null)
        {
            properties["columns"] = string.Join(", ", _requiredColumns);
        }

        return new OperatorPlanDescription("Index Scan")
        {
            Properties = properties,
            AccessStrategy = new AccessStrategyDescription(AccessMethod.IndexScan),
            Annotations = ["produces sorted output without materializing; eliminates separate Sort operator"],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);
        if (provider is Catalog.Providers.DatumFileTableProvider datumProvider)
            datumProvider.Store = context.Store;

        if (!provider.Seekable)
        {
            throw new InvalidOperationException(
                $"IndexScanOperator requires a seekable provider, but '{_descriptor.Name}' " +
                $"uses '{provider.GetType().Name}' which does not indicate it is seekable.");
        }

        try
        {
            // Traverse the index in sorted order (ascending or descending).
            // Batch consecutive entries from the same chunk into a single read.
            IEnumerable<ValueIndexEntry> traversal = _descending
                ? _columnIndex.TraverseBackward()
                : _columnIndex.TraverseForward();

            List<ValueIndexEntry> indexEntries = new();
            int currentChunkIndex = -1;
            long indexScanRowsYielded = 0;
            RowBatch? outputBatch = null;

            if (ExecutionTracer.IsEnabled)
            {
                ExecutionTracer.Write(
                    $"IndexScan  start  table={_descriptor.Name}  totalEntries={_columnIndex.EntryCount:N0}");
            }

            foreach (ValueIndexEntry entry in traversal)
            {
                if (indexEntries.Count > 0
                    && (entry.ChunkIndex != currentChunkIndex
                        || indexEntries.Count >= MaxIndexEntriesPerFlush))
                {
                    // Chunk boundary or size cap: flush the accumulated entries.
                    await foreach (Row row in FlushIndexEntriesAsync(
                        provider, indexEntries, cancellationToken).ConfigureAwait(false))
                    {
                        if (ExecutionTracer.IsEnabled)
                        {
                            indexScanRowsYielded++;

                            if (indexScanRowsYielded % 1_000_000 == 0)
                            {
                                ExecutionTracer.Write(
                                    $"IndexScan  {_descriptor.Name}  yielded {indexScanRowsYielded:N0} rows");
                            }
                        }

                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(row);

                        if (outputBatch.IsFull)
                        {
                            yield return outputBatch;
                            outputBatch = null;
                        }
                    }

                    indexEntries.Clear();
                }

                indexEntries.Add(entry);
                currentChunkIndex = entry.ChunkIndex;
            }

            // Flush any remaining entries.
            if (indexEntries.Count > 0)
            {
                await foreach (Row row in FlushIndexEntriesAsync(provider, indexEntries, cancellationToken).ConfigureAwait(false))
                {
                    if (ExecutionTracer.IsEnabled)
                    {
                        indexScanRowsYielded++;

                        if (indexScanRowsYielded % 1_000_000 == 0)
                        {
                            ExecutionTracer.Write(
                                $"IndexScan  {_descriptor.Name}  yielded {indexScanRowsYielded:N0} rows");
                        }
                    }

                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(row);

                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }
            }

            if (outputBatch is not null)
            {
                yield return outputBatch;
            }

            if (ExecutionTracer.IsEnabled)
            {
                ExecutionTracer.Write(
                    $"IndexScan  done  table={_descriptor.Name}  totalYielded={indexScanRowsYielded:N0}");
            }
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Reads the rows identified by a batch of index entries that share the same chunk.
    /// Single-entry batches fetch exactly one row; larger batches read the covering
    /// range and yield rows in the order they appear in the batch.
    /// </summary>
    private async IAsyncEnumerable<Row> FlushIndexEntriesAsync(
        ITableProvider provider,
        List<ValueIndexEntry> indexEntries,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (indexEntries.Count == 1)
        {
            ValueIndexEntry entry = indexEntries[0];
            long absoluteRow = _chunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;

            await foreach (RowBatch inputBatch in provider.ReadRowRangeAsync(
                _descriptor, _requiredColumns, absoluteRow, 1,
                cancellationToken).ConfigureAwait(false))
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    yield return inputBatch[i];
                }

                inputBatch.Return();
            }

            yield break;
        }

        // Compute the covering range across all entries in the batch.
        long minRow = long.MaxValue;
        long maxRow = long.MinValue;

        foreach (ValueIndexEntry entry in indexEntries)
        {
            long absoluteRow = _chunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;
            minRow = Math.Min(minRow, absoluteRow);
            maxRow = Math.Max(maxRow, absoluteRow);
        }

        int rangeCount = (int)(maxRow - minRow + 1);
        Dictionary<long, Row> rowsByOffset = new(indexEntries.Count);
        long totalFetched = 0;

        await foreach (RowBatch inputBatch in provider.ReadRowRangeAsync(
            _descriptor, _requiredColumns, minRow, rangeCount,
            cancellationToken).ConfigureAwait(false))
        {
            for (int i = 0; i < inputBatch.Count; i++)
            {
                long fetchedRow = minRow + totalFetched;
                rowsByOffset[fetchedRow] = inputBatch[i];
                totalFetched++;
            }

            inputBatch.Return();
        }

        // Yield rows in index order (the batch order from the traversal).
        foreach (ValueIndexEntry entry in indexEntries)
        {
            long absoluteRow = _chunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;

            if (rowsByOffset.TryGetValue(absoluteRow, out Row indexRow))
            {
                yield return indexRow;
            }
        }
    }
}
