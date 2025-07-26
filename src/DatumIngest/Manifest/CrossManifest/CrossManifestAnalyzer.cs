namespace DatumIngest.Manifest.CrossManifest;

using DatumIngest.Manifest.CrossManifest.Rules;
using DatumIngest.Manifest.Insights;

/// <summary>
/// Top-level orchestrator for cross-manifest join analysis. Takes N named manifests,
/// discovers join candidates, builds a join graph with transitive chains, produces
/// cross-manifest insights, and generates JOIN SQL with quality annotations.
/// </summary>
public static class CrossManifestAnalyzer
{
    /// <summary>
    /// All registered cross-manifest insight rules, evaluated in order.
    /// </summary>
    private static readonly IReadOnlyList<ICrossManifestInsightRule> AllRules =
    [
        new ManyToManyJoinRule(),
        new HighNullKeyRule(),
        new CardinalityMismatchRule(),
        new DisjointRangeRule(),
        new SchemaDriftRule(),
        new DenormalizationHintRule(),
        new StarSchemaRule(),
    ];

    /// <summary>
    /// Analyzes multiple named manifests to discover join candidates, build a join graph,
    /// and produce cross-manifest insights with recommended JOIN SQL.
    /// </summary>
    /// <param name="manifests">
    /// Named manifests to analyze. At least two manifests are required for meaningful results.
    /// </param>
    /// <param name="thresholds">
    /// Optional thresholds controlling analysis sensitivity. When <see langword="null"/>,
    /// default thresholds are used.
    /// </param>
    /// <param name="queryOptions">
    /// Optional options controlling SQL generation. When <see langword="null"/>,
    /// default options are used.
    /// </param>
    /// <returns>The cross-manifest analysis result.</returns>
    public static CrossManifestResult Analyze(
        IReadOnlyList<ManifestWithName> manifests,
        CrossManifestThresholds? thresholds = null,
        CrossManifestQueryOptions? queryOptions = null)
    {
        CrossManifestThresholds effectiveThresholds = thresholds ?? CrossManifestThresholds.Default;
        CrossManifestQueryOptions effectiveQueryOptions = queryOptions ?? CrossManifestQueryOptions.Default;

        if (manifests.Count < 2)
        {
            return new CrossManifestResult
            {
                Tables = manifests.Select(m => m.Name).ToList(),
                Candidates = [],
                JoinGraph = [],
            };
        }

        // Phase 1: Discover join candidates for each table pair.
        List<JoinCandidate> allCandidates = DiscoverCandidates(manifests, effectiveThresholds);

        // Phase 2: Detect composite keys from single-column candidates.
        IReadOnlyList<JoinCandidate> compositeKeys = CompositeKeyDetector.DetectCompositeKeys(
            allCandidates, effectiveThresholds);

        foreach (JoinCandidate composite in compositeKeys)
        {
            allCandidates.Add(composite);
        }

        // Phase 3: Build join graph and discover transitive chains.
        IReadOnlyList<JoinGraphEdge> graph = JoinGraphBuilder.BuildGraph(allCandidates, effectiveThresholds);
        IReadOnlyList<JoinChain>? chains = null;

        if (graph.Count > 0)
        {
            IReadOnlyList<JoinChain> foundChains = JoinGraphBuilder.FindTransitiveChains(graph, effectiveThresholds);
            chains = foundChains.Count > 0 ? foundChains : null;
        }

        // Phase 4: Evaluate cross-manifest insight rules.
        List<RawFinding> findings = new();

        foreach (ICrossManifestInsightRule rule in AllRules)
        {
            foreach (RawFinding finding in rule.Evaluate(manifests, allCandidates, effectiveThresholds))
            {
                findings.Add(finding);
            }
        }

        // Convert findings to DatasetInsight — cross-manifest findings don't need
        // syndrome detection (that's single-manifest), so we convert directly.
        IReadOnlyList<DatasetInsight>? insights = null;

        if (findings.Count > 0)
        {
            List<DatasetInsight> insightList = new(findings.Count);

            foreach (RawFinding finding in findings)
            {
                insightList.Add(InsightClusterer.ToInsight(finding));
            }

            insights = insightList;
        }

        // Phase 5: Generate SQL and annotations.
        string? recommendedQuery = CrossManifestQueryBuilder.BuildQuery(allCandidates, effectiveQueryOptions);
        IReadOnlyList<QueryAnnotation> annotations = CrossManifestQueryBuilder.GenerateAnnotations(
            allCandidates, effectiveQueryOptions);

        return new CrossManifestResult
        {
            Tables = manifests.Select(m => m.Name).ToList(),
            Candidates = allCandidates,
            JoinGraph = graph,
            TransitiveChains = chains,
            Insights = insights,
            RecommendedQuery = recommendedQuery,
            QueryAnnotations = annotations.Count > 0 ? annotations : null,
        };
    }

    /// <summary>
    /// Discovers join candidates for all pairwise table combinations.
    /// </summary>
    private static List<JoinCandidate> DiscoverCandidates(
        IReadOnlyList<ManifestWithName> manifests,
        CrossManifestThresholds thresholds)
    {
        List<JoinCandidate> candidates = new();

        for (int i = 0; i < manifests.Count; i++)
        {
            for (int j = i + 1; j < manifests.Count; j++)
            {
                ManifestWithName left = manifests[i];
                ManifestWithName right = manifests[j];

                IReadOnlyList<ColumnMatchCandidate> columnMatches =
                    ColumnMatcher.FindCandidatePairs(left, right, thresholds);

                foreach (ColumnMatchCandidate match in columnMatches)
                {
                    // Find the feature manifests for evidence scoring.
                    FeatureManifest? leftFeature = FindFeature(left.Manifest, match.LeftColumn);
                    FeatureManifest? rightFeature = FindFeature(right.Manifest, match.RightColumn);

                    if (leftFeature is null || rightFeature is null)
                    {
                        continue;
                    }

                    JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
                        leftFeature, left.Manifest.RowCount,
                        rightFeature, right.Manifest.RowCount,
                        match, thresholds);

                    if (evidence.CompositeConfidence < thresholds.CandidateMinConfidence)
                    {
                        continue;
                    }

                    JoinClassification joinType = JoinEvidenceScorer.ClassifyJoin(
                        leftFeature, left.Manifest.RowCount,
                        rightFeature, right.Manifest.RowCount);

                    double? fanout = JoinEvidenceScorer.EstimateFanout(
                        leftFeature, left.Manifest.RowCount,
                        rightFeature, right.Manifest.RowCount);

                    List<string>? warnings = BuildWarnings(evidence, thresholds);

                    candidates.Add(new JoinCandidate
                    {
                        LeftTable = left.Name,
                        RightTable = right.Name,
                        LeftColumns = [match.LeftColumn],
                        RightColumns = [match.RightColumn],
                        Evidence = evidence,
                        Confidence = evidence.CompositeConfidence,
                        EstimatedJoinType = joinType,
                        EstimatedFanout = fanout,
                        QualityWarnings = warnings,
                    });
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Finds a feature manifest by column name.
    /// </summary>
    private static FeatureManifest? FindFeature(QueryResultsManifest manifest, string columnName)
    {
        foreach (FeatureManifest feature in manifest.Features)
        {
            if (string.Equals(feature.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return feature;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds quality warnings for a join candidate based on its evidence.
    /// </summary>
    private static List<string>? BuildWarnings(JoinEvidence evidence, CrossManifestThresholds thresholds)
    {
        List<string>? warnings = null;

        if (evidence.NullKeyRatio > thresholds.HighNullKeyMinRatio)
        {
            warnings ??= new List<string>();
            warnings.Add($"High null-key ratio ({evidence.NullKeyRatio:P1})");
        }

        if (evidence.CardinalityRatio < thresholds.CardinalityMismatchMinRatio)
        {
            warnings ??= new List<string>();
            warnings.Add($"Cardinality mismatch (ratio={evidence.CardinalityRatio:F3})");
        }

        if (evidence.RangeOverlap.HasValue && evidence.RangeOverlap.Value < 0.05)
        {
            warnings ??= new List<string>();
            warnings.Add($"Low range overlap ({evidence.RangeOverlap.Value:P1})");
        }

        return warnings;
    }
}
