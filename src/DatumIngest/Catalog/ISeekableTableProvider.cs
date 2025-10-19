using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Optional extension of <see cref="ITableProvider"/> for providers whose source
/// format supports random-access row reads. When a provider implements this interface,
/// the execution engine can skip directly to specific row ranges instead of streaming
/// and discarding rows from pruned chunks.
/// </summary>
public interface ISeekableTableProvider : ITableProvider
{
    /// <summary>
    /// Reads a contiguous range of rows starting at <paramref name="startRow"/>.
    /// The provider seeks directly to the specified position without reading
    /// preceding rows.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the source file.</param>
    /// <param name="requiredColumns">
    /// Columns to include in the result rows (projection pushdown). When <c>null</c>, all columns are returned.
    /// </param>
    /// <param name="startRow">Zero-based index of the first row to read.</param>
    /// <param name="count">Maximum number of rows to read. The stream may yield fewer rows if the source is exhausted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of row batches from the specified range.</returns>
    IAsyncEnumerable<RowBatch> ReadRowRangeAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count,
        CancellationToken cancellationToken);
}
