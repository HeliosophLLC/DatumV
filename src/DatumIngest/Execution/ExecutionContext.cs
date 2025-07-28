using DatumIngest.Catalog;
using DatumIngest.Functions;

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
    /// Optional memory budget in bytes for operators that support spill-to-disk (e.g. hash join).
    /// When <see langword="null"/>, operators keep all intermediate state in memory (current default behavior).
    /// When set, operators may spill partitions to temporary files when estimated memory usage exceeds this budget.
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
    /// When set, operators such as the hash join may spill partitions to temporary
    /// files when estimated memory usage exceeds this budget.
    /// </summary>
    public long? MemoryBudgetBytes { get; }
}
