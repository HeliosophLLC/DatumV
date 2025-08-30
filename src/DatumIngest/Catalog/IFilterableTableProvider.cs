using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Optional extension of <see cref="ITableProvider"/> for providers that can use
/// filter predicates to skip entire data partitions (e.g. Parquet row groups)
/// based on column statistics. When a provider implements this interface, the
/// query engine passes WHERE predicates as advisory hints so the provider can
/// consult min/max statistics and avoid reading partitions that cannot match.
/// </summary>
/// <remarks>
/// The filter is <b>advisory only</b> — callers must still apply the predicate
/// via a downstream filter operator for correctness. Providers may
/// return rows that do not satisfy the filter; they must never suppress rows
/// that do satisfy it.
/// </remarks>
public interface IFilterableTableProvider : ITableProvider
{
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
}
