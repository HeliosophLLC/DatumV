using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Scans rows from a <see cref="ISeekableTableProvider"/> in the order defined
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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);

        if (provider is not ISeekableTableProvider seekable)
        {
            throw new InvalidOperationException(
                $"IndexScanOperator requires a seekable provider, but '{_descriptor.Name}' " +
                $"uses '{provider.GetType().Name}' which does not implement ISeekableTableProvider.");
        }

        // Traverse the index in sorted order (ascending or descending).
        // Batch consecutive entries from the same chunk into a single read.
        IEnumerable<ValueIndexEntry> traversal = _descending
            ? _columnIndex.TraverseBackward()
            : _columnIndex.TraverseForward();

        List<ValueIndexEntry> batch = new();
        int batchChunkIndex = -1;

        foreach (ValueIndexEntry entry in traversal)
        {
            if (batch.Count > 0 && entry.ChunkIndex != batchChunkIndex)
            {
                // Chunk boundary: flush the accumulated batch.
                await foreach (Row row in FlushBatchAsync(
                    seekable, batch, cancellationToken).ConfigureAwait(false))
                {
                    yield return row;
                }

                batch.Clear();
            }

            batch.Add(entry);
            batchChunkIndex = entry.ChunkIndex;
        }

        // Flush any remaining entries.
        if (batch.Count > 0)
        {
            await foreach (Row row in FlushBatchAsync(
                seekable, batch, cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Reads the rows identified by a batch of index entries that share the same chunk.
    /// Single-entry batches fetch exactly one row; larger batches read the covering
    /// range and yield rows in the order they appear in the batch.
    /// </summary>
    private async IAsyncEnumerable<Row> FlushBatchAsync(
        ISeekableTableProvider seekable,
        List<ValueIndexEntry> batch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (batch.Count == 1)
        {
            ValueIndexEntry entry = batch[0];
            long absoluteRow = _chunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;

            await foreach (Row row in seekable.ReadRowRangeAsync(
                _descriptor, _requiredColumns, absoluteRow, 1,
                cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }

            yield break;
        }

        // Compute the covering range across all entries in the batch.
        long minRow = long.MaxValue;
        long maxRow = long.MinValue;

        foreach (ValueIndexEntry entry in batch)
        {
            long absoluteRow = _chunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;
            minRow = Math.Min(minRow, absoluteRow);
            maxRow = Math.Max(maxRow, absoluteRow);
        }

        int rangeCount = (int)(maxRow - minRow + 1);
        Dictionary<long, Row> rowsByOffset = new(batch.Count);

        await foreach (Row row in seekable.ReadRowRangeAsync(
            _descriptor, _requiredColumns, minRow, rangeCount,
            cancellationToken).ConfigureAwait(false))
        {
            long fetchedRow = minRow + rowsByOffset.Count;
            rowsByOffset[fetchedRow] = row;
        }

        // Yield rows in index order (the batch order from the traversal).
        foreach (ValueIndexEntry entry in batch)
        {
            long absoluteRow = _chunks[entry.ChunkIndex].RowOffset + entry.RowOffsetInChunk;

            if (rowsByOffset.TryGetValue(absoluteRow, out Row? batchRow))
            {
                yield return batchRow;
            }
        }
    }
}
