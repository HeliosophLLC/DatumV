using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Execution;

/// <summary>
/// A node in the query execution plan tree. Each operator produces
/// a stream of rows by pulling from its child operators or data sources.
/// </summary>
public interface IQueryOperator
{
    /// <summary>
    /// Executes this operator and streams rows asynchronously.
    /// </summary>
    /// <param name="context">Execution context with cancellation, functions, and catalog.</param>
    /// <returns>An async stream of result rows.</returns>
    IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context);
}
