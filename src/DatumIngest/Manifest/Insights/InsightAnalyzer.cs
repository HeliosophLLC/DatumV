namespace DatumIngest.Manifest.Insights;

using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Top-level orchestrator that evaluates all insight rules against a manifest,
/// clusters the raw findings into compound insights with syndrome detection,
/// and produces the final sorted list of <see cref="DatasetInsight"/> objects.
/// </summary>
internal static class InsightAnalyzer
{
    /// <summary>
    /// All registered insight rules, evaluated in order.
    /// </summary>
    private static readonly IReadOnlyList<IInsightRule> AllRules =
    [
        // Data quality.
        new HighMissingnessRule(),
        new ConstantFeatureRule(),
        new CorrelatedMissingnessRule(),
        new InformativeMissingnessRule(),

        // Distribution.
        new ZeroInflatedRule(),
        new SkewnessRule(),
        new HeavyTailedRule(),
        new ExtremeOutliersRule(),

        // Encoding.
        new PossibleOrdinalRule(),
        new CategoricalEncodingRule(),

        // Redundancy.
        new NearDuplicateRule(),

        // Dimensionality / identifiers.
        new PossibleIdentifierRule(),

        // Scale.
        new NormalizationRule(),

        // Image.
        new ImageQualityRule(),
    ];

    /// <summary>
    /// Analyzes a manifest using all registered rules and returns the final insights.
    /// </summary>
    /// <param name="manifest">The query results manifest to analyze.</param>
    /// <param name="thresholds">
    /// Optional thresholds controlling rule sensitivity. When <see langword="null"/>,
    /// default thresholds are used.
    /// </param>
    /// <returns>
    /// A finalized, sorted list of dataset insights with action routing applied.
    /// </returns>
    internal static IReadOnlyList<DatasetInsight> Analyze(
        QueryResultsManifest manifest,
        InsightThresholds? thresholds = null)
    {
        InsightThresholds effectiveThresholds = thresholds ?? new InsightThresholds();

        List<RawFinding> findings = new();

        foreach (IInsightRule rule in AllRules)
        {
            foreach (RawFinding finding in rule.Evaluate(manifest, effectiveThresholds))
            {
                findings.Add(finding);
            }
        }

        return InsightClusterer.Cluster(findings);
    }
}
