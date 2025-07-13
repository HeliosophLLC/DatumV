using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Scans rows from a <see cref="ISeekableTableProvider"/> in the order defined
/// by a <see cref="SortedValueIndex"/>. Entries in the sorted index are walked
/// sequentially, and each row is fetched via random access — producing sorted
/// output without materializing and sorting the entire dataset.
/// </summary>
/// <remarks>
/// <para>
/// The query planner substitutes this operator for a
/// <see cref="ScanOperator"/> + <see cref="OrderByOperator"/> combination when
/// a sorted index exists for the ORDER BY column and the provider supports seeking.
/// </para>
/// <para>
/// Consecutive entries that fall within the same chunk are batched into a single
/// <see cref="ISeekableTableProvider.ReadRowRangeAsync"/> call to reduce I/O overhead.
/// </para>
/// </remarks>
public sealed class IndexScanOperator : IQueryOperator
{
    private readonly TableDescriptor _descriptor;
    private readonly IReadOnlySet<string>? _requiredColumns;
    private readonly SortedValueIndex _sortedIndex;
    private readonly IReadOnlyList<IndexChunk> _chunks;
    private readonly bool _descending;

    /// <summary>
    /// Creates an index scan operator.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the data source.</param>
    /// <param name="requiredColumns">Columns needed downstream; null means all columns.</param>
    /// <param name="sortedIndex">The sorted value index defining scan order.</param>
    /// <param name="chunks">The chunk directory for translating chunk-relative offsets to absolute row positions.</param>
    /// <param name="descending">Whether to walk the index in reverse (descending) order.</param>
    public IndexScanOperator(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        SortedValueIndex sortedIndex,
        IReadOnlyList<IndexChunk> chunks,
        bool descending)
    {
        _descriptor = descriptor;
        _requiredColumns = requiredColumns;
        _sortedIndex = sortedIndex;
        _chunks = chunks;
        _descending = descending;
    }

    /// <summary>The table descriptor this operator scans.</summary>
    public TableDescriptor Descriptor => _descriptor;

    /// <summary>The set of columns requested for projection pushdown.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <summary>Whether the scan walks the index in descending order.</summary>
    public bool Descending => _descending;

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

        // Copy entries to an array since ReadOnlySpan cannot live across await/yield.
        ValueIndexEntry[] entries = _sortedIndex.Entries.ToArray();
        int entryCount = entries.Length;

        if (entryCount == 0)
        {
            yield break;
        }

        // Walk entries in sorted order (ascending or descending).
        // Batch consecutive entries from the same chunk into a single read.
        int current = _descending ? entryCount - 1 : 0;
        int step = _descending ? -1 : 1;

        while (current >= 0 && current < entryCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int chunkIndex = entries[current].ChunkIndex;
            long absoluteRow = _chunks[chunkIndex].RowOffset
                + entries[current].RowOffsetInChunk;

            // Look ahead for consecutive entries in the same chunk to batch.
            int batchStart = current;
            int batchEnd = current;
            long minRow = absoluteRow;
            long maxRow = absoluteRow;

            while (true)
            {
                int next = batchEnd + step;
                if (next < 0 || next >= entryCount)
                {
                    break;
                }

                if (entries[next].ChunkIndex != chunkIndex)
                {
                    break;
                }

                long nextAbsoluteRow = _chunks[entries[next].ChunkIndex].RowOffset
                    + entries[next].RowOffsetInChunk;
                minRow = Math.Min(minRow, nextAbsoluteRow);
                maxRow = Math.Max(maxRow, nextAbsoluteRow);
                batchEnd = next;
            }

            int batchSize = Math.Abs(batchEnd - batchStart) + 1;

            if (batchSize == 1)
            {
                // Single entry: fetch exactly one row.
                await foreach (Row row in seekable.ReadRowRangeAsync(
                    _descriptor, _requiredColumns, absoluteRow, 1,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return row;
                }
            }
            else
            {
                // Batch: fetch the row range covering all entries and yield in index order.
                int rangeCount = (int)(maxRow - minRow + 1);
                Dictionary<long, Row> rowsByOffset = new(batchSize);

                await foreach (Row row in seekable.ReadRowRangeAsync(
                    _descriptor, _requiredColumns, minRow, rangeCount,
                    cancellationToken).ConfigureAwait(false))
                {
                    long fetchedRow = minRow + rowsByOffset.Count;
                    rowsByOffset[fetchedRow] = row;
                }

                // Yield rows in index order (ascending or descending).
                for (int i = batchStart; i != batchEnd + step; i += step)
                {
                    long batchAbsoluteRow = _chunks[entries[i].ChunkIndex].RowOffset
                        + entries[i].RowOffsetInChunk;

                    if (rowsByOffset.TryGetValue(batchAbsoluteRow, out Row? batchRow))
                    {
                        yield return batchRow;
                    }
                }
            }

            current = batchEnd + step;
        }
    }
}
