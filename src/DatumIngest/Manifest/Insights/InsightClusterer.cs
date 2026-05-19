namespace Heliosoph.DatumV.Manifest.Insights;

using System.Text.Json;

/// <summary>
/// Clusters raw findings into compound insights (syndromes), resolves conflict
/// groups, and transforms <see cref="RawFinding"/> instances into final
/// <see cref="DatasetInsight"/> objects with proper action routing.
/// </summary>
internal static class InsightClusterer
{
    /// <summary>
    /// Known syndrome definitions. Order matters — earlier syndromes take priority.
    /// </summary>
    private static readonly IReadOnlyList<SyndromeDefinition> Syndromes =
    [
        new SyndromeDefinition(
            InsightKind.ZeroInflatedSkewedNumeric,
            new HashSet<InsightKind> { InsightKind.ZeroInflated, InsightKind.RightSkewed },
            InsightCategory.Distribution,
            InsightSeverity.Warning),

        new SyndromeDefinition(
            InsightKind.NonNormalDistribution,
            new HashSet<InsightKind> { InsightKind.RightSkewed, InsightKind.HeavyTailed },
            InsightCategory.Distribution,
            InsightSeverity.Warning),

        new SyndromeDefinition(
            InsightKind.SystematicDataGap,
            new HashSet<InsightKind> { InsightKind.CorrelatedMissingness, InsightKind.InformativeMissingness },
            InsightCategory.DataQuality,
            InsightSeverity.Warning),

        new SyndromeDefinition(
            InsightKind.FeatureLeakageRisk,
            new HashSet<InsightKind> { InsightKind.NearDuplicateNumeric, InsightKind.FunctionalDependency },
            InsightCategory.Redundancy,
            InsightSeverity.Critical),

        new SyndromeDefinition(
            InsightKind.UnusableFeature,
            new HashSet<InsightKind> { InsightKind.ConstantFeature, InsightKind.CriticalMissingness },
            InsightCategory.DataQuality,
            InsightSeverity.Critical),
    ];

    /// <summary>
    /// Clusters raw findings into dataset insights. Detects syndromes, resolves
    /// conflict groups (highest confidence wins), and applies action routing.
    /// </summary>
    /// <param name="findings">Raw findings from all rules.</param>
    /// <returns>Finalized dataset insights, sorted by severity then confidence descending.</returns>
    internal static IReadOnlyList<DatasetInsight> Cluster(IReadOnlyList<RawFinding> findings)
    {
        if (findings.Count == 0)
        {
            return [];
        }

        // Index findings by affected features for syndrome detection.
        Dictionary<string, List<RawFinding>> findingsByFeature = BuildFeatureIndex(findings);

        // Detect syndromes and mark consumed findings.
        HashSet<RawFinding> consumed = new();
        List<DatasetInsight> insights = new();

        DetectSyndromes(findingsByFeature, consumed, insights);

        // Convert remaining (non-consumed) findings to insights.
        foreach (RawFinding finding in findings)
        {
            if (!consumed.Contains(finding))
            {
                insights.Add(ToInsight(finding));
            }
        }

        // Resolve conflict groups: keep highest-confidence insight per group.
        ResolveConflicts(insights);

        // Sort: Critical first, then Warning, then Info; within each, by confidence descending.
        insights.Sort(CompareInsights);

        return insights;
    }

    private static Dictionary<string, List<RawFinding>> BuildFeatureIndex(IReadOnlyList<RawFinding> findings)
    {
        Dictionary<string, List<RawFinding>> index = new();

        foreach (RawFinding finding in findings)
        {
            foreach (string feature in finding.AffectedFeatures)
            {
                if (!index.TryGetValue(feature, out List<RawFinding>? list))
                {
                    list = new List<RawFinding>();
                    index[feature] = list;
                }

                list.Add(finding);
            }
        }

        return index;
    }

    private static void DetectSyndromes(
        Dictionary<string, List<RawFinding>> findingsByFeature,
        HashSet<RawFinding> consumed,
        List<DatasetInsight> insights)
    {
        foreach (KeyValuePair<string, List<RawFinding>> entry in findingsByFeature)
        {
            string feature = entry.Key;
            List<RawFinding> featureFindings = entry.Value;

            foreach (SyndromeDefinition syndrome in Syndromes)
            {
                List<RawFinding> matches = new();

                foreach (RawFinding finding in featureFindings)
                {
                    if (syndrome.ComponentKinds.Contains(finding.Kind) && !consumed.Contains(finding))
                    {
                        matches.Add(finding);
                    }
                }

                // Need at least 2 distinct component kinds to trigger a syndrome.
                HashSet<InsightKind> matchedKinds = new();

                foreach (RawFinding match in matches)
                {
                    matchedKinds.Add(match.Kind);
                }

                if (matchedKinds.Count < 2)
                {
                    continue;
                }

                // Merge the matched findings into a compound insight.
                DatasetInsight compoundInsight = MergeSyndrome(syndrome, matches, feature);
                insights.Add(compoundInsight);

                foreach (RawFinding match in matches)
                {
                    consumed.Add(match);
                }
            }
        }
    }

    private static DatasetInsight MergeSyndrome(
        SyndromeDefinition syndrome,
        List<RawFinding> components,
        string primaryFeature)
    {
        // Use max confidence from components.
        double confidence = 0.0;

        foreach (RawFinding component in components)
        {
            if (component.Confidence > confidence)
            {
                confidence = component.Confidence;
            }
        }

        // Merge affected features.
        HashSet<string> affectedFeatures = new();

        foreach (RawFinding component in components)
        {
            foreach (string feature in component.AffectedFeatures)
            {
                affectedFeatures.Add(feature);
            }
        }

        // Merge actions — preserve bundle identifiers.
        List<InsightAction> allActions = new();

        foreach (RawFinding component in components)
        {
            allActions.AddRange(component.Actions);
        }

        // Merge evidence.
        Dictionary<string, Dictionary<string, JsonElement>> mergedEvidence = new();

        foreach (RawFinding component in components)
        {
            if (component.Evidence is null)
            {
                continue;
            }

            foreach (KeyValuePair<string, IReadOnlyDictionary<string, JsonElement>> featureEvidence in component.Evidence)
            {
                if (!mergedEvidence.TryGetValue(featureEvidence.Key, out Dictionary<string, JsonElement>? stats))
                {
                    stats = new Dictionary<string, JsonElement>();
                    mergedEvidence[featureEvidence.Key] = stats;
                }

                foreach (KeyValuePair<string, JsonElement> stat in featureEvidence.Value)
                {
                    stats[stat.Key] = stat.Value;
                }
            }
        }

        // Merge observations, risks, recommendations.
        List<string> observations = new();
        List<string> risks = new();
        List<string> recommendations = new();

        foreach (RawFinding component in components)
        {
            observations.Add(component.Observation);
            risks.Add(component.Risk);
            recommendations.Add(component.Recommendation);
        }

        // Build cast-safe read-only evidence.
        Dictionary<string, IReadOnlyDictionary<string, JsonElement>>? evidence =
            mergedEvidence.Count > 0
                ? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(mergedEvidence.Count)
                : null;

        if (evidence is not null)
        {
            foreach (KeyValuePair<string, Dictionary<string, JsonElement>> entry in mergedEvidence)
            {
                evidence[entry.Key] = entry.Value;
            }
        }

        // Create a raw finding for action routing, then convert.
        RawFinding merged = new(
            syndrome.Kind,
            syndrome.Category,
            syndrome.Severity,
            confidence,
            InsightScope.Feature,
            string.Join(" ", observations),
            string.Join(" ", risks),
            string.Join(" ", recommendations),
            Rationale: null,
            Alternatives: null,
            affectedFeatures.ToList(),
            allActions,
            ConflictGroup: null,
            evidence);

        return ToInsight(merged);
    }

    /// <summary>
    /// Converts a <see cref="RawFinding"/> to a <see cref="DatasetInsight"/>,
    /// applying the action routing invariant based on computed <see cref="ApplyMode"/>.
    /// </summary>
    internal static DatasetInsight ToInsight(RawFinding finding)
    {
        ApplyMode mode = ComputeApplyMode(finding);

        IReadOnlyList<InsightAction> actions;
        IReadOnlyList<InsightAction>? proposedActions;

        if (mode is ApplyMode.AutoSafe or ApplyMode.Suggest)
        {
            // All actions are executable under current policy.
            actions = finding.Actions;
            proposedActions = null;
        }
        else
        {
            // All actions migrate to proposedActions — actions is empty.
            actions = [];
            proposedActions = finding.Actions.Count > 0 ? finding.Actions : null;
        }

        return new DatasetInsight
        {
            Kind = finding.Kind,
            Category = finding.Category,
            Severity = finding.Severity,
            Confidence = finding.Confidence,
            Scope = finding.Scope,
            Observation = finding.Observation,
            Risk = finding.Risk,
            Recommendation = finding.Recommendation,
            Rationale = finding.Rationale,
            Alternatives = finding.Alternatives,
            AffectedFeatures = finding.AffectedFeatures,
            Actions = actions,
            ProposedActions = proposedActions,
            ConflictGroup = finding.ConflictGroup,
            RecommendedApplyMode = mode,
            Evidence = finding.Evidence,
        };
    }

    /// <summary>
    /// Derives the <see cref="ApplyMode"/> from the nature of the actions and confidence.
    /// </summary>
    internal static ApplyMode ComputeApplyMode(RawFinding finding)
    {
        if (finding.Actions.Count == 0)
        {
            // Informational — no actions to route.
            return ApplyMode.Suggest;
        }

        bool hasLossy = false;
        bool hasDrop = false;
        bool allLossless = true;

        foreach (InsightAction action in finding.Actions)
        {
            if (action.Lossy)
            {
                hasLossy = true;
                allLossless = false;
            }

            if (action.Kind == ActionKind.Drop)
            {
                hasDrop = true;
            }
        }

        // Constant-feature drops are safe (zero information loss).
        bool isConstantFeatureDrop = hasDrop && finding.Kind == InsightKind.ConstantFeature;

        if (finding.Confidence >= 0.95 && allLossless)
        {
            return ApplyMode.AutoSafe;
        }

        if (finding.Confidence >= 0.95 && isConstantFeatureDrop)
        {
            return ApplyMode.AutoSafe;
        }

        if (hasDrop && !isConstantFeatureDrop)
        {
            return ApplyMode.ManualOnly;
        }

        if (finding.Confidence >= 0.6 && hasLossy)
        {
            return ApplyMode.Suggest;
        }

        if (finding.Confidence < 0.6 && hasLossy)
        {
            return ApplyMode.ManualOnly;
        }

        return ApplyMode.Suggest;
    }

    /// <summary>
    /// Resolves conflict groups by keeping only the highest-confidence insight per group.
    /// </summary>
    private static void ResolveConflicts(List<DatasetInsight> insights)
    {
        // Build index: conflictGroup → highest-confidence insight.
        Dictionary<string, DatasetInsight> winners = new();

        foreach (DatasetInsight insight in insights)
        {
            if (insight.ConflictGroup is null)
            {
                continue;
            }

            if (!winners.TryGetValue(insight.ConflictGroup, out DatasetInsight? current) ||
                insight.Confidence > current.Confidence)
            {
                winners[insight.ConflictGroup] = insight;
            }
        }

        // Remove losers.
        insights.RemoveAll(insight =>
            insight.ConflictGroup is not null &&
            winners.TryGetValue(insight.ConflictGroup, out DatasetInsight? winner) &&
            !ReferenceEquals(insight, winner));
    }

    private static int CompareInsights(DatasetInsight left, DatasetInsight right)
    {
        // Severity: Critical first (higher enum value = more severe).
        int severityComparison = right.Severity.CompareTo(left.Severity);

        if (severityComparison != 0)
        {
            return severityComparison;
        }

        // Higher confidence first.
        return right.Confidence.CompareTo(left.Confidence);
    }
}
