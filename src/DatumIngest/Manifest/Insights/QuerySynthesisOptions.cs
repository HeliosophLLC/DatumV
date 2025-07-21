namespace DatumIngest.Manifest.Insights;

/// <summary>
/// Options controlling query synthesis behavior. Passed to
/// <see cref="QuerySynthesizer"/> to configure output formatting.
/// </summary>
public sealed class QuerySynthesisOptions
{
    /// <summary>
    /// Gets or sets the source table or subquery expression used in the FROM clause.
    /// Defaults to <c>"source"</c>.
    /// </summary>
    public string SourceExpression { get; init; } = "source";
}
