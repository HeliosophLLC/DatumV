using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Interface for data source providers that stream rows from a backing store
/// (CSV, JSON, ZIP, HDF5, Parquet, etc.).
/// </summary>
public interface ITableProvider : IDisposable
{
    /// <summary>
    /// Returns the schema of the table described by <paramref name="descriptor"/>.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The inferred or declared schema.</returns>
    Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken);
    /// <summary>
    /// Opens the table and streams column batches asynchronously.
    /// String and JSON values are arena-backed; consumers must materialise
    /// them before the batch is disposed.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="requiredColumns">
    /// Set of column names the consumer needs, for projection pushdown.
    /// When null, all columns are returned.
    /// </param>
    /// <param name="filterHint">
    /// Optional predicate for zone-map pruning.  May be null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of column batches from the data source.</returns>
    IAsyncEnumerable<ColumnBatch> OpenColumnBatchAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the table and streams rows asynchronously.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="requiredColumns">
    /// Set of column names the consumer needs. The provider may skip
    /// producing columns not in this set for projection pushdown.
    /// When null, all columns are returned.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of row batches from the data source.</returns>
    IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the table with an advisory filter hint. The provider may use the filter
    /// to skip partitions whose statistics prove no rows can match, but the returned
    /// stream is not guaranteed to contain only matching rows.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="requiredColumns">
    /// Columns to include in the result rows (projection pushdown). When <c>null</c>, all columns are returned.
    /// </param>
    /// <param name="filterHint">
    /// An advisory WHERE predicate. The provider may consult column statistics to
    /// skip partitions that provably contain no matching rows. Must not be used to
    /// suppress individual rows — the caller applies the filter for correctness.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of row batches, possibly with non-matching partitions skipped.</returns>
    IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression filterHint,
        CancellationToken cancellationToken);

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

    /// <summary>
    /// Returns the row count for the table described by the given descriptor.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <returns>The row count for the table.</returns>
    long GetRowCount(TableDescriptor descriptor);

    /// <summary>
    /// Returns true if the provider supports seeking to specific row positions, enabling operators like <c>ScanOperator</c>
    /// to perform index seeks for equality predicates.
    /// </summary>
    bool Seekable { get; }
}
