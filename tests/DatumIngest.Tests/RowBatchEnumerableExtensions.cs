using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Convenience extensions over the <see cref="IAsyncEnumerable{RowBatch}"/>
/// streams produced by <see cref="StatementPlan.ExecuteAsync(CancellationToken, DatumIngest.Execution.BatchContext)"/>
/// and <see cref="TableCatalog.ExecuteAsync(StatementPlan, CancellationToken)"/>.
/// </summary>
public static class RowBatchEnumerableExtensions
{
    /// <summary>
    /// Iterates <paramref name="source"/> to completion and discards every
    /// batch. Use when the caller only needs the plan's side effect
    /// (DDL apply, DML write) and not the rows. Equivalent to
    /// <c>await foreach (var _ in source) { }</c> but reads better at the
    /// call site.
    /// </summary>
    public static async Task DrainAsync(this IAsyncEnumerable<RowBatch> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        await foreach (RowBatch _ in source.ConfigureAwait(false))
        {
        }
    }
}
