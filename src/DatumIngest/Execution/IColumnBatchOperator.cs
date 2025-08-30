using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// A node in the query execution plan tree that produces column-major
/// <see cref="ColumnBatch"/> results instead of row-major <see cref="RowBatch"/>.
/// Column-at-a-time execution enables better cache locality, fewer per-row
/// array allocations, and arena-backed string storage.
/// </summary>
/// <remarks>
/// <para>
/// This interface mirrors <see cref="IQueryOperator"/> but streams
/// <see cref="ColumnBatch"/> objects.  The query planner builds a columnar
/// sub-pipeline when the data source supports columnar output, chaining columnar
/// filter, project, limit, and alias operators.  At boundaries with operators
/// that require row-level access (e.g. hash join, group-by), a
/// <see cref="Operators.ColumnBatchToRowBatchAdapter"/> converts back to
/// <see cref="IQueryOperator"/>.
/// </para>
/// </remarks>
public interface IColumnBatchOperator
{
    /// <summary>
    /// Executes this operator and streams column batches asynchronously.
    /// </summary>
    /// <param name="context">Execution context with cancellation, functions, and catalog.</param>
    /// <returns>An async stream of column batches.</returns>
    IAsyncEnumerable<ColumnBatch> ExecuteColumnBatchAsync(ExecutionContext context);

    /// <summary>
    /// Returns plan metadata describing this operator for EXPLAIN output.
    /// </summary>
    /// <returns>A description of this operator for the execution plan.</returns>
    OperatorPlanDescription DescribeForExplain();
}
