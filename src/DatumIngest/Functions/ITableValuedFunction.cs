using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Interface for table-valued functions that produce a stream of rows
/// (e.g. <c>RANGE</c>).
/// </summary>
public interface ITableValuedFunction
{
    /// <summary>The SQL function name (case-insensitive matching).</summary>
    string Name { get; }

    /// <summary>
    /// Executes the function and yields rows asynchronously.
    /// </summary>
    /// <param name="arguments">The argument values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of row batches produced by the function.</returns>
    IAsyncEnumerable<RowBatch> ExecuteAsync(
        DataValue[] arguments,
        CancellationToken cancellationToken);
}
