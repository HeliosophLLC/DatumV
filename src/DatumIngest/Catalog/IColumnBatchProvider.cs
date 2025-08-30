using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Optional interface for providers that can produce <see cref="ColumnBatch"/>
/// data directly, bypassing the row-major <see cref="RowBatch"/> path.
/// </summary>
/// <remarks>
/// Only the <see cref="DatumIngest.Catalog.Providers.DatumFileTableProvider"/>
/// implements this today.  The column-batch pipeline is activated when the
/// query evaluator detects this interface on a provider, allowing the full
/// decode → evaluate → output path to stay in column-major layout.
/// </remarks>
public interface IColumnBatchProvider
{
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
}
