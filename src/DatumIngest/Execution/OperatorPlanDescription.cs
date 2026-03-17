namespace DatumIngest.Execution;

/// <summary>
/// Self-description of an operator for EXPLAIN output. Each <see cref="QueryOperator"/>
/// implementation returns this from <see cref="QueryOperator.DescribeForExplain"/> to
/// provide its name, properties, children, warnings, and annotations. The
/// <see cref="QueryExplainer"/> uses these descriptions to build the
/// <see cref="ExplainPlanNode"/> tree, adding cross-cutting concerns like
/// cardinality estimation on top.
/// </summary>
public sealed class OperatorPlanDescription
{
    /// <summary>
    /// Creates an operator plan description.
    /// </summary>
    /// <param name="operatorName">The human-readable operator name (e.g. "Scan", "Filter", "INNER Join").</param>
    public OperatorPlanDescription(string operatorName)
    {
        OperatorName = operatorName;
    }

    /// <summary>The human-readable operator name (e.g. "Scan", "Filter", "INNER Join").</summary>
    public string OperatorName { get; }

    /// <summary>
    /// Structured key-value properties describing the operator's configuration.
    /// Rendered as parenthesized details in text output; preserved as-is in structured output.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>
    /// Child operators and their optional edge labels (e.g. "probe", "build").
    /// The <see cref="QueryExplainer"/> recurses into these to build the full plan tree.
    /// </summary>
    public IReadOnlyList<(QueryOperator Child, string? Label)> Children { get; init; } = [];

    /// <summary>Estimated number of rows this operator will produce, or <c>null</c> if unknown.</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>Performance warnings surfaced to the user.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Static plan annotations (e.g. "bounded top-N sort (N=100)").</summary>
    public IReadOnlyList<string> Annotations { get; init; } = [];

    /// <summary>
    /// Access strategy for scan operators. <c>null</c> for non-scan operators.
    /// </summary>
    public AccessStrategyDescription? AccessStrategy { get; init; }
}
