namespace DatumIngest.Execution;

/// <summary>
/// Thread-safe accumulator for Query Unit (QU) costs incurred during query execution.
/// Created per query and optionally constrained by a budget. The meter is a passive
/// counter — budget enforcement is the responsibility of the caller (e.g. the gRPC
/// service layer), matching the governance pattern used for row budgets.
/// </summary>
public sealed class QueryMeter
{
    private long _functionQueryUnits;

    /// <summary>
    /// Creates a new query meter with an optional QU budget.
    /// </summary>
    /// <param name="budget">
    /// Maximum allowed Query Units for this query, or <see langword="null"/> for unlimited.
    /// </param>
    public QueryMeter(long? budget = null)
    {
        Budget = budget;
    }

    /// <summary>
    /// The QU budget for this query, or <see langword="null"/> if unlimited.
    /// </summary>
    public long? Budget { get; }

    /// <summary>
    /// Total Query Units accumulated from scalar function invocations.
    /// </summary>
    public long FunctionQueryUnits => Interlocked.Read(ref _functionQueryUnits);

    /// <summary>
    /// Returns <see langword="true"/> when a budget is set and the accumulated
    /// Query Units exceed it.
    /// </summary>
    public bool IsBudgetExceeded => Budget.HasValue && FunctionQueryUnits > Budget.Value;

    /// <summary>
    /// Records the cost of a function invocation. Thread-safe.
    /// </summary>
    /// <param name="cost">The Query Unit cost to add.</param>
    public void Add(int cost)
    {
        Interlocked.Add(ref _functionQueryUnits, cost);
    }
}
