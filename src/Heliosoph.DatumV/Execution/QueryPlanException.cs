namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Thrown by the query planner when a parsed-but-semantically-invalid SQL statement is
/// rejected before execution begins. Inherits from <see cref="ExecutionException"/>:
/// the message is safe to surface to the caller as a query-level error.
/// </summary>
/// <remarks>
/// <para>
/// Examples include: recursive CTE body shape mismatch (anchor must be a single SELECT),
/// reference to a non-existent table or column that survived parsing, type incompatibility
/// in a UNION's branches, etc. Each of these is a *user* error — the SQL was syntactically
/// valid but couldn't be planned into a working operator tree.
/// </para>
/// <para>
/// Many call sites in <c>QueryPlanner</c> currently throw <see cref="System.InvalidOperationException"/>
/// for these conditions. Migrate them to <see cref="QueryPlanException"/> as the
/// catch-boundary work matures and you have a need to discriminate plan-time errors from
/// execution-time ones at the public surface.
/// </para>
/// </remarks>
public class QueryPlanException : ExecutionException
{
    /// <summary>Creates a new <see cref="QueryPlanException"/> with a user-facing message.</summary>
    public QueryPlanException(string message)
        : base(message) { }

    /// <summary>
    /// Creates a new <see cref="QueryPlanException"/> with a user-facing message and a
    /// wrapped inner exception (e.g. an underlying parser exception that produced the
    /// user-actionable message).
    /// </summary>
    public QueryPlanException(string message, Exception? innerException)
        : base(message, innerException) { }
}
