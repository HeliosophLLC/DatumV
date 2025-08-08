using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Shared context passed through the operator tree during query execution.
/// Carries the cancellation token, function registry, table catalog, and
/// optional query meter for cost tracking.
/// </summary>
public sealed class ExecutionContext
{
    /// <summary>
    /// Creates a new execution context.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <param name="functionRegistry">Registry of scalar and table-valued functions.</param>
    /// <param name="catalog">Registry of named tables and provider factories.</param>
    /// <param name="queryMeter">Optional meter for accumulating Query Unit costs, or <see langword="null"/> for unmetered execution.</param>
    /// <param name="memoryBudgetBytes">
    /// Optional memory budget in bytes for operators that support spill-to-disk.
    /// When <see langword="null"/>, operators keep all intermediate state in memory.
    /// When set, operators spill to temporary files when estimated memory exceeds this budget.
    /// Supported operators: hash join, ORDER BY, GROUP BY, DISTINCT, PIVOT, UNION/INTERSECT/EXCEPT,
    /// and materialised CTEs.
    /// </param>
    public ExecutionContext(
        CancellationToken cancellationToken,
        FunctionRegistry functionRegistry,
        TableCatalog catalog,
        QueryMeter? queryMeter = null,
        long? memoryBudgetBytes = null)
    {
        CancellationToken = cancellationToken;
        FunctionRegistry = functionRegistry;
        Catalog = catalog;
        QueryMeter = queryMeter;
        MemoryBudgetBytes = memoryBudgetBytes;
    }

    /// <summary>Cancellation token for cooperative cancellation.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Registry of scalar and table-valued functions.</summary>
    public FunctionRegistry FunctionRegistry { get; }

    /// <summary>Registry of named tables and provider factories.</summary>
    public TableCatalog Catalog { get; }

    /// <summary>
    /// Optional meter for accumulating Query Unit costs during execution.
    /// <see langword="null"/> when metering is not active (e.g. CLI execution).
    /// </summary>
    public QueryMeter? QueryMeter { get; }

    /// <summary>
    /// Optional memory budget in bytes for operators that support spill-to-disk.
    /// When <see langword="null"/>, operators keep all intermediate state in memory.
    /// When set, operators spill intermediate state to temporary files when estimated
    /// memory usage exceeds this budget. Covered operators: hash join, ORDER BY
    /// (external sort), GROUP BY (partitioned re-aggregation), DISTINCT, PIVOT,
    /// UNION/INTERSECT/EXCEPT (hash-partitioned), and materialised CTEs.
    /// </summary>
    public long? MemoryBudgetBytes { get; }

    /// <summary>
    /// Maximum recursion depth for recursive CTEs. Limits how many iterations
    /// the recursive member executes before the operator raises an error.
    /// Defaults to <c>1000</c>.
    /// </summary>
    public int MaxRecursionDepth { get; init; } = 1000;

    /// <summary>
    /// The outer row from a correlated scalar subquery, or <see langword="null"/> when
    /// not inside a correlated subquery. Used by <see cref="ExpressionEvaluator"/> to
    /// resolve column references to outer-scope tables.
    /// </summary>
    public Row? OuterRow { get; init; }

    /// <summary>
    /// Maximum number of output rows that a downstream <see cref="Operators.LimitOperator"/>
    /// will consume (LIMIT + OFFSET). Operators such as join can use this hint to choose
    /// cheaper strategies (e.g. index nested-loop) when only a small number of rows are needed.
    /// <see langword="null"/> when no LIMIT clause is active.
    /// </summary>
    public int? RowLimit { get; init; }

    /// <summary>
    /// Returns a new context with the given outer row set for correlated subquery execution.
    /// All other properties are copied from the current context.
    /// </summary>
    /// <param name="outerRow">The outer row providing correlated column values.</param>
    /// <returns>A new <see cref="ExecutionContext"/> with <see cref="OuterRow"/> set.</returns>
    public ExecutionContext WithOuterRow(Row outerRow)
    {
        return new ExecutionContext(
            CancellationToken,
            FunctionRegistry,
            Catalog,
            QueryMeter,
            MemoryBudgetBytes)
        {
            OuterRow = outerRow,
            RowLimit = RowLimit,
            MaxRecursionDepth = MaxRecursionDepth,
        };
    }
}
