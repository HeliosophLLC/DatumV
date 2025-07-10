using DatumQuery.Model;

namespace DatumQuery.Catalog;

/// <summary>
/// Optional extension of <see cref="ITableProvider"/> for providers that support
/// efficient random access by key values. When a provider implements this interface,
/// the query planner can defer expensive columns (e.g. decompressed file bytes) and
/// fetch them only for rows that survive all joins and filters.
/// </summary>
public interface IKeyedTableProvider : ITableProvider
{
    /// <summary>
    /// Fetches rows matching the given key values, returning only the requested columns.
    /// The key column is always included in the result so callers can build a lookup.
    /// </summary>
    /// <param name="descriptor">Table descriptor with file path and provider options.</param>
    /// <param name="keyColumn">
    /// The column whose values identify individual entries (e.g. <c>file_name</c> for ZIP).
    /// </param>
    /// <param name="keyValues">
    /// The set of key values to retrieve. Only entries matching one of these values are returned.
    /// </param>
    /// <param name="requiredColumns">
    /// Columns to include in the result rows (projection pushdown). The key column is always
    /// included regardless of this set. When null, all columns are returned.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of rows for matching entries.</returns>
    IAsyncEnumerable<Row> FetchByKeysAsync(
        TableDescriptor descriptor,
        string keyColumn,
        IReadOnlySet<DataValue> keyValues,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken);
}
