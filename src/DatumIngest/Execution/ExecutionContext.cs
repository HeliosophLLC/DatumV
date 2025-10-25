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
    /// Creates a new execution context from an existing context, copying all properties. Useful for creating a child
    /// </summary>
    public ExecutionContext(ExecutionContext context)
    {
        CancellationToken = context.CancellationToken;
        FunctionRegistry = context.FunctionRegistry;
        Catalog = context.Catalog;
        LocalBufferPool = context.LocalBufferPool;
        Store = context.Store;
        QueryMeter = context.QueryMeter;
        MemoryBudgetBytes = context.MemoryBudgetBytes;
        BatchSize = context.BatchSize;
        AssertionDiagnostics = context.AssertionDiagnostics;
        MaxStratifyClasses = context.MaxStratifyClasses;
    }

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
  /// <param name="localBufferPool">
  /// Pool for reusing <see cref="Model.Row"/> objects and their backing
  /// <see cref="Model.DataValue"/> arrays in join operators.
  /// </param>
  /// <param name="store">
  /// Optional value store for reference-type payloads. Defaults to a new <see cref="Model.Arena"/>
  /// if not provided.
  /// </param>
  public ExecutionContext(
        CancellationToken cancellationToken,
        FunctionRegistry functionRegistry,
        TableCatalog catalog,
        LocalBufferPool localBufferPool,
        QueryMeter? queryMeter = null,
        long? memoryBudgetBytes = null,
        Arena? store = null)
    {
        CancellationToken = cancellationToken;
        FunctionRegistry = functionRegistry;
        Catalog = catalog;
        LocalBufferPool = localBufferPool;
        Store = store ?? new Arena();
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
    /// Value store for string, byte, float, and object payloads during query execution.
    /// Operators use this store for all reference-type <see cref="DataValue"/> access
    /// for all reference-type payloads.
    /// </summary>
    public Arena Store { get; }

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
    /// Number of parallel workers an operator may spawn for CPU-bound work (e.g.
    /// parallel hash join probe, parallel hash aggregate). Defaults to <c>1</c>
    /// (single-threaded). Set to <see cref="Environment.ProcessorCount"/> or higher
    /// to enable intra-query parallelism.
    /// </summary>
    public int DegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// Optional global concurrency budget that bounds the total number of parallel
    /// operator workers across all concurrent queries. When <see langword="null"/>,
    /// operators may spawn up to <see cref="DegreeOfParallelism"/> workers without
    /// limit — appropriate for single-query CLI usage. On the server, a shared
    /// <see cref="ParallelismBudget"/> prevents thread pool oversubscription.
    /// </summary>
    public ParallelismBudget? ParallelismBudget { get; init; }

    /// <summary>
    /// Maximum number of rows per <see cref="Model.RowBatch"/>. Operators fill
    /// batches up to this size before yielding. Defaults to <c>1024</c>.
    /// </summary>
    public int BatchSize { get; init; } = 1024;

    /// <summary>
    /// Pool for reusing <see cref="Model.Row"/> objects and their backing
    /// <see cref="Model.DataValue"/> arrays in join operators. Join operators
    /// rent rows from this pool instead of allocating, and downstream consumers
    /// (e.g. GROUP BY) return rows after extracting values.
    /// </summary>
    public LocalBufferPool LocalBufferPool { get; }

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
            LocalBufferPool,
            QueryMeter,
            MemoryBudgetBytes,
            Store)
        {
            OuterRow = outerRow,
            RowLimit = RowLimit,
            MaxRecursionDepth = MaxRecursionDepth,
            DegreeOfParallelism = DegreeOfParallelism,
            ParallelismBudget = ParallelismBudget,
            BatchSize = BatchSize,
            AssertionDiagnostics = AssertionDiagnostics,
            MaxStratifyClasses = MaxStratifyClasses,
        };
    }

    /// <summary>
    /// Accumulates skip/warn counts and sample messages for <c>ASSERT</c> clauses evaluated
    /// during this query. <see langword="null"/> when no ASSERT clauses are present or when
    /// diagnostics collection has not been requested.
    /// </summary>
    public AssertionDiagnostics? AssertionDiagnostics { get; init; }

    /// <summary>
    /// Maximum number of distinct classes allowed in a TABLESAMPLE BALANCED
    /// stratification column. Limits the number of per-class reservoirs to
    /// bound memory usage. <see langword="null"/> means use the operator's
    /// internal default (10,000).
    /// </summary>
    public int? MaxStratifyClasses { get; init; }
}
