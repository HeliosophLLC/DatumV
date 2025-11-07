namespace DatumIngest.Execution;

/// <summary>
/// Thrown when a recursive CTE's iteration count exceeds
/// <see cref="ExecutionContext.MaxRecursionDepth"/> while still producing new rows.
/// Indicates the user's recursion didn't reach a fixpoint within the configured bound —
/// either the recursive member's predicate doesn't terminate, or the depth limit is
/// genuinely too low for the workload. Either way, the message is safe to surface to the
/// caller as a query-level error.
/// </summary>
public sealed class RecursionDepthExceededException : ExecutionException
{
    /// <summary>The CTE name whose recursion overflowed.</summary>
    public string CteName { get; }

    /// <summary>The depth limit that was hit.</summary>
    public int MaxDepth { get; }

    /// <summary>
    /// Creates a new <see cref="RecursionDepthExceededException"/>.
    /// </summary>
    /// <param name="cteName">The CTE name whose recursion overflowed.</param>
    /// <param name="maxDepth">The depth limit that was hit.</param>
    public RecursionDepthExceededException(string cteName, int maxDepth)
        : base($"Recursive CTE '{cteName}' exceeded maximum recursion depth of {maxDepth}.")
    {
        CteName = cteName;
        MaxDepth = maxDepth;
    }
}
