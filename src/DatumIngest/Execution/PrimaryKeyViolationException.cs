namespace DatumIngest.Execution;

/// <summary>
/// Thrown by <c>InsertExecutor</c> when a row would violate the
/// target table's <c>PRIMARY KEY</c> constraint: a duplicate key
/// (against existing rows or another row in the same INSERT batch)
/// or a NULL in any PK column. Subclass of <see cref="ExecutionException"/>
/// so callers that already catch the base type at their query
/// boundary surface the message verbatim; pattern-matching on this
/// subtype lets a future planner or REPL emit a constraint-specific
/// status code.
/// </summary>
public sealed class PrimaryKeyViolationException : ExecutionException
{
    /// <summary>Creates a new exception with a user-facing message.</summary>
    public PrimaryKeyViolationException(string message)
        : base(message) { }
}
