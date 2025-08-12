using DatumIngest.Model;

namespace DatumIngest.Execution;

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

    /// <summary>
    /// Returns plan metadata describing this operator for EXPLAIN output.
    /// Every operator implementation must provide meaningful plan metadata
    /// including its name, properties, children, and any warnings or annotations.
    /// </summary>
    /// <returns>A description of this operator for the execution plan.</returns>
    OperatorPlanDescription DescribeForExplain();
}
