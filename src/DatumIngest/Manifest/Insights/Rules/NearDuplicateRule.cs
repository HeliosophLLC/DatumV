namespace DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects near-duplicate feature pairs — numeric columns with near-perfect
/// Pearson/Spearman correlation and categorical columns with near-perfect Cramér's V.
/// </summary>
internal sealed class NearDuplicateRule : IInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(QueryResultsManifest manifest, InsightThresholds thresholds)
    {
        if (manifest.Interactions is null)
        {
            yield break;
        }

        foreach (ColumnInteraction interaction in manifest.Interactions)
        {
            RawFinding? finding = TryDetectNumericDuplicate(interaction, thresholds)
                ?? TryDetectCategoricalDuplicate(interaction, thresholds);

            if (finding is not null)
            {
                yield return finding;
            }
        }
    }

    private static RawFinding? TryDetectNumericDuplicate(ColumnInteraction interaction, InsightThresholds thresholds)
    {
        double? correlation = interaction.Pearson ?? interaction.Spearman;

        if (!correlation.HasValue || Math.Abs(correlation.Value) < thresholds.NearDuplicateMinCorrelation)
        {
            return null;
        }

        double absCorrelation = Math.Abs(correlation.Value);
        double confidence = Math.Min(1.0, 0.7 + (absCorrelation - thresholds.NearDuplicateMinCorrelation) * 6.0);

        // Choose survivor: alphabetically first (deterministic, simple).
        string survivor = string.Compare(interaction.ColumnA, interaction.ColumnB, StringComparison.Ordinal) < 0
            ? interaction.ColumnA
            : interaction.ColumnB;
        string dropCandidate = survivor == interaction.ColumnA ? interaction.ColumnB : interaction.ColumnA;

        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(interaction.ColumnA, "correlation", absCorrelation)
            .Add(interaction.ColumnB, "correlation", absCorrelation)
            .Add(dropCandidate, "survivor", survivor)
            .Add(dropCandidate, "survivorReason", "alphabetical");

        if (interaction.Pearson.HasValue)
        {
            evidence.Add(interaction.ColumnA, "pearson", interaction.Pearson.Value);
        }

        if (interaction.Spearman.HasValue)
        {
            evidence.Add(interaction.ColumnA, "spearman", interaction.Spearman.Value);
        }

        return new RawFinding(
            InsightKind.NearDuplicateNumeric,
            InsightCategory.Redundancy,
            InsightSeverity.Warning,
            confidence,
            InsightScope.FeaturePair,
            $"Columns '{interaction.ColumnA}' and '{interaction.ColumnB}' have a correlation of {absCorrelation:F3}, indicating near-perfect linear relationship.",
            "Near-duplicate features inflate dimensionality without adding information. They cause multicollinearity in linear models and waste compute in all models.",
            $"Drop '{dropCandidate}' and keep '{survivor}'.",
            Rationale: null,
            Alternatives: [$"Drop '{survivor}' and keep '{dropCandidate}' instead.", "Use PCA to combine them into a single component."],
            [interaction.ColumnA, interaction.ColumnB],
            [new InsightAction(
                ActionKind.Drop,
                dropCandidate,
                Expression: null,
                Alias: null,
                Lossy: true,
                Reversible: false,
                BundleIdentifier: null)],
            $"near-duplicate-{interaction.ColumnA}-{interaction.ColumnB}",
            evidence.Build());
    }

    private static RawFinding? TryDetectCategoricalDuplicate(ColumnInteraction interaction, InsightThresholds thresholds)
    {
        if (!interaction.CramerV.HasValue || interaction.CramerV.Value < thresholds.NearDuplicateMinCramerV)
        {
            return null;
        }

        double cramerV = interaction.CramerV.Value;
        double confidence = Math.Min(1.0, 0.7 + (cramerV - thresholds.NearDuplicateMinCramerV) * 6.0);

        string survivor = string.Compare(interaction.ColumnA, interaction.ColumnB, StringComparison.Ordinal) < 0
            ? interaction.ColumnA
            : interaction.ColumnB;
        string dropCandidate = survivor == interaction.ColumnA ? interaction.ColumnB : interaction.ColumnA;

        EvidenceBuilder evidence = new EvidenceBuilder()
            .Add(interaction.ColumnA, "cramerV", cramerV)
            .Add(interaction.ColumnB, "cramerV", cramerV)
            .Add(dropCandidate, "survivor", survivor)
            .Add(dropCandidate, "survivorReason", "alphabetical");

        return new RawFinding(
            InsightKind.NearDuplicateCategorical,
            InsightCategory.Redundancy,
            InsightSeverity.Warning,
            confidence,
            InsightScope.FeaturePair,
            $"Columns '{interaction.ColumnA}' and '{interaction.ColumnB}' have Cramér's V of {cramerV:F3}, indicating near-perfect categorical association.",
            "Near-duplicate categoricals carry the same information. Encoding both multiplies feature count without adding signal.",
            $"Drop '{dropCandidate}' and keep '{survivor}'.",
            Rationale: null,
            Alternatives: [$"Drop '{survivor}' and keep '{dropCandidate}' instead."],
            [interaction.ColumnA, interaction.ColumnB],
            [new InsightAction(
                ActionKind.Drop,
                dropCandidate,
                Expression: null,
                Alias: null,
                Lossy: true,
                Reversible: false,
                BundleIdentifier: null)],
            $"near-duplicate-{interaction.ColumnA}-{interaction.ColumnB}",
            evidence.Build());
    }
}
