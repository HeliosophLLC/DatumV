namespace DatumIngest.Execution;

/// <summary>
/// Thrown by <c>InsertExecutor</c> (via <c>DatumAppendSession</c>) when a row
/// would violate a <c>UNIQUE INDEX</c> constraint: the encoded composite key
/// already exists in the index (either from a prior row or another row in
/// the same INSERT batch). Rows with <c>NULL</c> in any covered column are
/// exempt from the check — NULLS DISTINCT (PG default) — so this exception
/// fires only for fully-populated key tuples that collide.
/// </summary>
/// <remarks>
/// Subclass of <see cref="ExecutionException"/> for the same reason as
/// <see cref="PrimaryKeyViolationException"/>: callers that catch the base
/// type get the message verbatim; future status-code emitters can
/// pattern-match this subtype.
/// </remarks>
public sealed class UniqueIndexViolationException : ExecutionException
{
    /// <summary>Creates a new exception with a user-facing message.</summary>
    public UniqueIndexViolationException(string message)
        : base(message) { }
}
