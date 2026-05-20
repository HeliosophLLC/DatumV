using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Thrown when an <see cref="AssertClause"/> with <see cref="AssertFailureMode.Abort"/>
/// evaluates to false or null for a row. Inherits from <see cref="ExecutionException"/>:
/// the message is safe to surface to the caller as a query-level error.
/// </summary>
public sealed class AssertionAbortException : ExecutionException
{
    /// <summary>
    /// Source span of the ASSERT clause that triggered the abort, if available.
    /// </summary>
    public SourceSpan? AssertSpan { get; }

    /// <summary>
    /// Creates a new <see cref="AssertionAbortException"/>.
    /// </summary>
    /// <param name="message">The diagnostic message (from the MESSAGE expression or the default).</param>
    /// <param name="span">Source location of the ASSERT keyword for diagnostic reporting.</param>
    public AssertionAbortException(string? message, SourceSpan? span = null)
        : base(message ?? "Assertion failed")
    {
        AssertSpan = span;
    }
}
