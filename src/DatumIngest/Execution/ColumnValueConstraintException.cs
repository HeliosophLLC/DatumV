namespace DatumIngest.Execution;

/// <summary>
/// Thrown when an INSERT or UPDATE value would violate a declared
/// column constraint that cannot be silently coerced — currently the
/// <c>VARCHAR(N)</c> / <c>CHAR(N)</c> overlength check. Subclass of
/// <see cref="ExecutionException"/> so callers that already catch the
/// base type at their query boundary surface the message verbatim;
/// pattern-matching on this subtype lets a future REPL or planner emit
/// a constraint-specific status code.
/// </summary>
public sealed class ColumnValueConstraintException : ExecutionException
{
    /// <summary>Creates a new exception with a user-facing message.</summary>
    public ColumnValueConstraintException(string message)
        : base(message) { }
}
