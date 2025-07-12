using DatumQuery.Model;

namespace DatumQuery.Functions;

/// <summary>
/// Interface for table-valued functions that produce a stream of rows
/// (e.g. JSON_PATH, unnest).
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
    /// <returns>An async stream of rows produced by the function.</returns>
    IAsyncEnumerable<Row> ExecuteAsync(
        DataValue[] arguments,
        CancellationToken cancellationToken);
}
