using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Thrown when an expression evaluation fails at runtime. Carries the
/// <see cref="SourceSpan"/> of the failing expression so that callers can
/// report the SQL source location to the user.
/// </summary>
public sealed class ExpressionEvaluationException : Exception
{
    /// <summary>
    /// Source span of the expression that triggered the error, if available.
    /// </summary>
    public SourceSpan? Span { get; }

    /// <summary>
    /// Creates a new <see cref="ExpressionEvaluationException"/>.
    /// </summary>
    /// <param name="message">Diagnostic message including source location context.</param>
    /// <param name="span">Source location of the failing expression.</param>
    /// <param name="innerException">The original exception that caused the evaluation failure.</param>
    public ExpressionEvaluationException(string message, SourceSpan? span, Exception? innerException = null)
        : base(message, innerException)
    {
        Span = span;
    }
}
