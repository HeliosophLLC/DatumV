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
    /// Streams all rows from the table, optionally applying an advisory filter hint for
    /// statistics-based partition pruning. The caller is responsible for applying the
    /// filter for correctness — the stream may still contain non-matching rows.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="requiredColumns">
    /// Columns needed downstream. When <c>null</c>, all columns are returned. Providers
    /// that support projection pushdown may skip producing columns outside this set.
    /// </param>
    /// <param name="filterHint">
    /// Optional predicate used for zone-map / chunk-level pruning. May be <c>null</c>.
    /// Must not be used to suppress individual rows — the caller applies the filter for
    /// correctness.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of row batches from the data source.</returns>
    IAsyncEnumerable<RowBatch> ScanAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a contiguous range of rows starting at <paramref name="startRow"/>. Requires
    /// <see cref="Seekable"/> to be <c>true</c>. Used by the index-scan and index-NLJ paths
    /// to fetch specific physical rows identified by a prior index lookup.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the source file.</param>
    /// <param name="requiredColumns">
    /// Columns needed downstream. When <c>null</c>, all columns are returned.
    /// </param>
    /// <param name="startRow">Zero-based index of the first row to read.</param>
    /// <param name="count">Maximum number of rows to read. The stream may yield fewer rows if the source is exhausted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of row batches from the specified range.</returns>
    IAsyncEnumerable<RowBatch> SeekAsync(
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
    /// Returns true if the provider supports seeking to specific row positions via
    /// <see cref="SeekAsync"/>, enabling operators like <c>ScanOperator</c> to perform
    /// index seeks for equality predicates.
    /// </summary>
    bool Seekable { get; }
}
