namespace DatumIngest.Execution;

/// <summary>
/// Thrown when a query's accumulated Query Unit cost exceeds its budget.
/// Operators that materialize their full input (blocking operators) check
/// the budget periodically during materialization so the query can be
/// terminated early instead of consuming unbounded resources.
/// </summary>
public sealed class QueryBudgetExceededException : Exception
{
    /// <summary>
    /// Creates a new instance with the budget and consumed Query Units.
    /// </summary>
    /// <param name="budget">The maximum allowed Query Units for the query.</param>
    /// <param name="consumed">The actual Query Units consumed when the budget was exceeded.</param>
    public QueryBudgetExceededException(long budget, long consumed)
        : base($"Query Unit budget exceeded (limit: {budget}, used: {consumed}).")
    {
        Budget = budget;
        Consumed = consumed;
    }

    /// <summary>The maximum allowed Query Units for the query.</summary>
    public long Budget { get; }

    /// <summary>The accumulated Query Units at the time the budget was exceeded.</summary>
    public long Consumed { get; }
}
