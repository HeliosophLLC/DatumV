namespace DatumIngest.Execution;

/// <summary>
/// Base type for exceptions deliberately thrown during query execution to surface a
/// user-actionable error. Subclasses represent specific failure modes (assertion abort,
/// expression evaluation failure, function argument mismatch, query-unit budget exhaustion,
/// recursive-CTE depth overflow, …); their messages are intended to be returned verbatim
/// to the caller.
/// </summary>
/// <remarks>
/// <para>
/// The catch boundary at the public surface (REPL, gRPC handler, CLI tool, language-server
/// diagnostics) should distinguish:
/// <list type="bullet">
/// <item><c>catch (ExecutionException ex)</c> — surface <c>ex.Message</c> to the user as
/// a query-level error. The user wrote a query that violated a contract we explicitly
/// guard against.</item>
/// <item><c>catch (Exception ex)</c> — log + return a generic "internal error" response.
/// The exception escaped from a code path that wasn't supposed to throw it; surfacing the
/// message risks leaking internal details (stack-frame strings, file paths, third-party
/// library quirks) to potentially-untrusted callers.</item>
/// </list>
/// </para>
/// <para>
/// New explicit-error sites should subclass <see cref="ExecutionException"/> rather than
/// throwing it directly, so the type system carries the failure-mode discriminator and
/// downstream code can pattern-match on subtypes for richer handling (e.g. mapping each
/// kind to a specific gRPC status code).
/// </para>
/// </remarks>
public class ExecutionException : Exception
{
    /// <summary>Creates a new <see cref="ExecutionException"/> with a user-facing message.</summary>
    public ExecutionException(string message)
        : base(message) { }

    /// <summary>
    /// Creates a new <see cref="ExecutionException"/> with a user-facing message and a wrapped
    /// inner exception (e.g. an underlying parse or evaluation failure that produced the
    /// user-actionable message).
    /// </summary>
    public ExecutionException(string message, Exception? innerException)
        : base(message, innerException) { }
}
