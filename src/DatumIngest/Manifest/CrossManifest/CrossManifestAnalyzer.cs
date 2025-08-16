namespace DatumIngest.Manifest.CrossManifest;

using DatumIngest.Manifest.CrossManifest.Rules;
using DatumIngest.Manifest.Insights;

/// <summary>
/// Top-level orchestrator for cross-manifest join analysis. Takes N named manifests,
/// discovers join candidates, builds join graphs (primary + alternates for equivalent
/// table partitions), produces cross-manifest insights, and generates JOIN SQL.
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
        new EquivalentTablePartitionRule(),
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
                JoinGraphs = [],
                PerTableInsights = AnalyzePerTable(manifests),
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

        // Phase 3: Detect equivalent table groups (e.g., train/test splits).
        IReadOnlyList<EquivalentTableGroup> equivalentGroups =
            EquivalentTableDetector.Detect(manifests, allCandidates);

        // Collect all non-preferred tables to exclude from the primary graph.
        HashSet<string> nonPreferredTables = new(StringComparer.OrdinalIgnoreCase);

        foreach (EquivalentTableGroup group in equivalentGroups)
        {
            foreach (string table in group.Tables)
            {
                if (!string.Equals(table, group.PreferredTable, StringComparison.OrdinalIgnoreCase))
                {
                    nonPreferredTables.Add(table);
                }
            }
        }

        // Phase 4: Build primary join graph (excluding non-preferred equivalent tables).
        // Pass allCandidates with exclusion set so CandidateIndex values stay valid
        // against the global Candidates list on CrossManifestResult.
        IReadOnlyList<JoinGraphEdge> primaryEdges = JoinGraphBuilder.BuildGraph(
            allCandidates, effectiveThresholds, nonPreferredTables);

        // Transitive chains are computed from the primary graph only.
        IReadOnlyList<JoinChain>? chains = null;

        if (primaryEdges.Count > 0)
        {
            IReadOnlyList<JoinChain> foundChains = JoinGraphBuilder.FindTransitiveChains(primaryEdges, effectiveThresholds);
            chains = foundChains.Count > 0 ? foundChains : null;
        }

        // Phase 5: Analyze per-table column insights.
        IReadOnlyDictionary<string, IReadOnlyList<DatasetInsight>>? perTableInsights = AnalyzePerTable(manifests);

        // Phase 6: Build join graphs (primary + alternates).
        List<JoinGraph> joinGraphs = BuildJoinGraphs(
            allCandidates, primaryEdges, equivalentGroups, nonPreferredTables,
            manifests, effectiveThresholds, effectiveQueryOptions, perTableInsights);

        // Phase 7: Evaluate cross-manifest insight rules.
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

        return new CrossManifestResult
        {
            Tables = manifests.Select(m => m.Name).ToList(),
            Candidates = allCandidates,
            JoinGraphs = joinGraphs,
            EquivalentTableGroups = equivalentGroups.Count > 0 ? equivalentGroups : null,
            TransitiveChains = chains,
            Insights = insights,
            PerTableInsights = perTableInsights,
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

    /// <summary>
    /// Runs single-manifest <see cref="InsightAnalyzer"/> on each table to produce
    /// per-column insights (nullity, skew, encoding, outliers, etc.).
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<DatasetInsight>>? AnalyzePerTable(
        IReadOnlyList<ManifestWithName> manifests)
    {
        Dictionary<string, IReadOnlyList<DatasetInsight>>? result = null;

        foreach (ManifestWithName manifest in manifests)
        {
            IReadOnlyList<DatasetInsight> tableInsights = InsightAnalyzer.Analyze(manifest.Manifest);

            if (tableInsights.Count > 0)
            {
                result ??= new Dictionary<string, IReadOnlyList<DatasetInsight>>(StringComparer.Ordinal);
                result[manifest.Name] = tableInsights;
            }
        }

        return result;
    }

    /// <summary>
    /// Filters candidates to exclude any that involve a table in the exclusion set.
    /// </summary>
    private static IReadOnlyList<JoinCandidate> FilterCandidates(
        IReadOnlyList<JoinCandidate> candidates,
        HashSet<string> excludedTables)
    {
        if (excludedTables.Count == 0)
        {
            return candidates;
        }

        List<JoinCandidate> filtered = new();

        foreach (JoinCandidate candidate in candidates)
        {
            if (!excludedTables.Contains(candidate.LeftTable) &&
                !excludedTables.Contains(candidate.RightTable))
            {
                filtered.Add(candidate);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Builds the primary join graph and any alternate graphs for equivalent table partitions.
    /// Each graph carries its own recommended query and annotations.
    /// </summary>
    private static List<JoinGraph> BuildJoinGraphs(
        IReadOnlyList<JoinCandidate> allCandidates,
        IReadOnlyList<JoinGraphEdge> primaryEdges,
        IReadOnlyList<EquivalentTableGroup> equivalentGroups,
        HashSet<string> nonPreferredTables,
        IReadOnlyList<ManifestWithName> manifests,
        CrossManifestThresholds thresholds,
        CrossManifestQueryOptions queryOptions,
        IReadOnlyDictionary<string, IReadOnlyList<DatasetInsight>>? perTableInsights)
    {
        // Build primary graph candidates (excluding non-preferred equivalent tables).
        IReadOnlyList<JoinCandidate> primaryCandidates = FilterCandidates(allCandidates, nonPreferredTables);

        string? primaryQuery = CrossManifestQueryBuilder.BuildQuery(
            primaryCandidates, queryOptions, perTableInsights);
        IReadOnlyList<QueryAnnotation> primaryAnnotations = CrossManifestQueryBuilder.GenerateAnnotations(
            primaryCandidates, queryOptions);

        // Determine primary graph label/reason — null when no equivalent groups exist.
        string? primaryLabel = null;
        string? primaryReason = null;
        IReadOnlyList<string>? primaryExcluded = null;
        long? primaryRowCount = null;

        if (equivalentGroups.Count > 0)
        {
            // Label with the preferred table name from the first group.
            primaryLabel = equivalentGroups[0].PreferredTable;

            ManifestWithName? preferredManifest = FindManifest(manifests, equivalentGroups[0].PreferredTable);
            long preferredRows = preferredManifest?.Manifest.RowCount ?? 0;

            primaryReason = $"Primary graph — uses '{equivalentGroups[0].PreferredTable}' " +
                $"({FormatRowCount(preferredRows)} rows, stronger hub connections).";
            primaryExcluded = nonPreferredTables.ToList();
            primaryRowCount = preferredRows > 0 ? preferredRows : null;
        }

        List<JoinGraph> joinGraphs =
        [
            new JoinGraph
            {
                Label = primaryLabel,
                Reason = primaryReason,
                Edges = primaryEdges,
                ExcludedTables = primaryExcluded,
                RecommendedQuery = primaryQuery,
                QueryAnnotations = primaryAnnotations.Count > 0 ? primaryAnnotations : null,
                EstimatedRowCount = primaryRowCount,
            },
        ];

        // Build alternate graphs — one per non-preferred table in each equivalence group.
        foreach (EquivalentTableGroup group in equivalentGroups)
        {
            foreach (string alternateTable in group.Tables)
            {
                if (string.Equals(alternateTable, group.PreferredTable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Exclude all non-preferred tables EXCEPT the current alternate.
                HashSet<string> alternateExcluded = new(nonPreferredTables, StringComparer.OrdinalIgnoreCase);
                alternateExcluded.Remove(alternateTable);
                alternateExcluded.Add(group.PreferredTable);

                // Build the alternate graph from allCandidates with the exclusion set so
                // CandidateIndex values remain valid against CrossManifestResult.Candidates.
                // InheritHubEdges adds back any structurally validated hub connections from
                // the primary graph that fell below GraphEdgeMinConfidence.
                IReadOnlyList<JoinGraphEdge> alternateEdges = InheritHubEdges(
                    JoinGraphBuilder.BuildGraph(allCandidates, thresholds, alternateExcluded),
                    allCandidates, primaryEdges,
                    group.PreferredTable, alternateTable);

                // Collect candidates that were inherited below the query builder's threshold
                // so the recommended SQL and annotations include the structurally validated edges.
                IReadOnlyList<JoinCandidate> alternateCandidates = FilterCandidates(allCandidates, alternateExcluded);
                HashSet<JoinCandidate>? inheritedCandidates = CollectInheritedCandidates(
                    alternateEdges, allCandidates);

                string? alternateQuery = CrossManifestQueryBuilder.BuildQuery(
                    alternateCandidates, queryOptions, perTableInsights, inheritedCandidates);
                IReadOnlyList<QueryAnnotation> alternateAnnotations = CrossManifestQueryBuilder.GenerateAnnotations(
                    alternateCandidates, queryOptions, inheritedCandidates);

                ManifestWithName? altManifest = FindManifest(manifests, alternateTable);
                ManifestWithName? prefManifest = FindManifest(manifests, group.PreferredTable);

                long alternateRows = altManifest?.Manifest.RowCount ?? 0;
                long preferredRows = prefManifest?.Manifest.RowCount ?? 0;

                joinGraphs.Add(new JoinGraph
                {
                    Label = alternateTable,
                    Reason = $"Alternate graph — substitutes '{alternateTable}' for " +
                        $"'{group.PreferredTable}' ({FormatRowCount(alternateRows)} rows vs " +
                        $"{FormatRowCount(preferredRows)} rows).",
                    Edges = alternateEdges,
                    ExcludedTables = alternateExcluded.ToList(),
                    RecommendedQuery = alternateQuery,
                    QueryAnnotations = alternateAnnotations.Count > 0 ? alternateAnnotations : null,
                    EstimatedRowCount = alternateRows > 0 ? alternateRows : null,
                });
            }
        }

        return joinGraphs;
    }

    /// <summary>
    /// Finds a manifest by table name.
    /// </summary>
    private static ManifestWithName? FindManifest(IReadOnlyList<ManifestWithName> manifests, string tableName)
    {
        foreach (ManifestWithName manifest in manifests)
        {
            if (string.Equals(manifest.Name, tableName, StringComparison.OrdinalIgnoreCase))
            {
                return manifest;
            }
        }

        return null;
    }

    /// <summary>
    /// Formats a row count as a human-readable string with magnitude suffix.
    /// </summary>
    private static string FormatRowCount(long rowCount)
    {
        return rowCount switch
        {
            >= 1_000_000_000 => $"{rowCount / 1_000_000_000.0:F1}B",
            >= 1_000_000 => $"{rowCount / 1_000_000.0:F1}M",
            >= 1_000 => $"{rowCount / 1_000.0:F1}K",
            _ => rowCount.ToString(),
        };
    }

    /// <summary>
    /// Ensures the alternate join graph inherits hub connections that the primary graph
    /// established for the preferred table. When equivalent table detection validates
    /// that two tables share hub connections, the alternate table's candidate may still
    /// fall below <see cref="CrossManifestThresholds.GraphEdgeMinConfidence"/> due to low
    /// cardinality overlap (e.g., a smaller partition covering only a fraction of the hub's keys).
    /// This method finds those missing hub edges and adds them back.
    /// </summary>
    private static IReadOnlyList<JoinGraphEdge> InheritHubEdges(
        IReadOnlyList<JoinGraphEdge> alternateEdges,
        IReadOnlyList<JoinCandidate> allCandidates,
        IReadOnlyList<JoinGraphEdge> primaryEdges,
        string preferredTable,
        string alternateTable)
    {
        // Collect hub tables connected to the preferred table in the primary graph,
        // along with the hub-side columns and the primary edge's confidence.
        List<(string HubTable, IReadOnlyList<string> HubColumns, double PrimaryConfidence, int PrimaryCandidateIndex)>? primaryHubConnections = null;

        foreach (JoinGraphEdge edge in primaryEdges)
        {
            JoinCandidate candidate = allCandidates[edge.CandidateIndex];

            if (string.Equals(edge.LeftTable, preferredTable, StringComparison.OrdinalIgnoreCase))
            {
                primaryHubConnections ??= new();
                primaryHubConnections.Add((edge.RightTable, candidate.RightColumns, edge.Confidence, edge.CandidateIndex));
            }
            else if (string.Equals(edge.RightTable, preferredTable, StringComparison.OrdinalIgnoreCase))
            {
                primaryHubConnections ??= new();
                primaryHubConnections.Add((edge.LeftTable, candidate.LeftColumns, edge.Confidence, edge.CandidateIndex));
            }
        }

        if (primaryHubConnections is null)
        {
            return alternateEdges;
        }

        // Determine which hub tables the alternate graph already connects to.
        HashSet<string> existingAlternateHubs = new(StringComparer.OrdinalIgnoreCase);

        foreach (JoinGraphEdge edge in alternateEdges)
        {
            if (string.Equals(edge.LeftTable, alternateTable, StringComparison.OrdinalIgnoreCase))
            {
                existingAlternateHubs.Add(edge.RightTable);
            }
            else if (string.Equals(edge.RightTable, alternateTable, StringComparison.OrdinalIgnoreCase))
            {
                existingAlternateHubs.Add(edge.LeftTable);
            }
        }

        // Find missing hub connections and recover candidates that fell below threshold.
        List<JoinGraphEdge>? inherited = null;

        foreach ((string hubTable, IReadOnlyList<string> hubColumns, double primaryConfidence, int primaryCandidateIndex) in primaryHubConnections)
        {
            if (existingAlternateHubs.Contains(hubTable))
            {
                continue;
            }

            // Find the best candidate in allCandidates connecting the alternate table
            // to this hub on the same hub-side columns.
            int bestIndex = -1;
            double bestConfidence = -1;

            for (int i = 0; i < allCandidates.Count; i++)
            {
                JoinCandidate candidate = allCandidates[i];

                if (string.Equals(candidate.LeftTable, alternateTable, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.RightTable, hubTable, StringComparison.OrdinalIgnoreCase) &&
                    ColumnsMatch(candidate.RightColumns, hubColumns) &&
                    candidate.Confidence > bestConfidence)
                {
                    bestIndex = i;
                    bestConfidence = candidate.Confidence;
                }
                else if (string.Equals(candidate.LeftTable, hubTable, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(candidate.RightTable, alternateTable, StringComparison.OrdinalIgnoreCase) &&
                         ColumnsMatch(candidate.LeftColumns, hubColumns) &&
                         candidate.Confidence > bestConfidence)
                {
                    bestIndex = i;
                    bestConfidence = candidate.Confidence;
                }
            }

            if (bestIndex >= 0)
            {
                JoinCandidate bestCandidate = allCandidates[bestIndex];
                inherited ??= new();

                inherited.Add(new JoinGraphEdge(
                    bestCandidate.LeftTable,
                    bestCandidate.RightTable,
                    CandidateIndex: bestIndex,
                    bestCandidate.Confidence)
                {
                    InheritedFrom = new InheritedEdgeOrigin(primaryCandidateIndex, primaryConfidence),
                });
            }
        }

        if (inherited is null)
        {
            return alternateEdges;
        }

        List<JoinGraphEdge> combined = new(alternateEdges.Count + inherited.Count);
        combined.AddRange(alternateEdges);
        combined.AddRange(inherited);
        return combined;
    }

    /// <summary>
    /// Checks whether two column lists contain the same column names in order (case-insensitive).
    /// </summary>
    private static bool ColumnsMatch(IReadOnlyList<string> columnsA, IReadOnlyList<string> columnsB)
    {
        if (columnsA.Count != columnsB.Count)
        {
            return false;
        }

        for (int i = 0; i < columnsA.Count; i++)
        {
            if (!string.Equals(columnsA[i], columnsB[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the set of candidates referenced by inherited edges (those that were added
    /// back by equivalent table detection). Returns <see langword="null"/> when no edges
    /// are inherited (the common case for non-partition graphs).
    /// </summary>
    private static HashSet<JoinCandidate>? CollectInheritedCandidates(
        IReadOnlyList<JoinGraphEdge> edges,
        IReadOnlyList<JoinCandidate> allCandidates)
    {
        HashSet<JoinCandidate>? inherited = null;

        foreach (JoinGraphEdge edge in edges)
        {
            if (edge.InheritedFrom is not null)
            {
                inherited ??= new(ReferenceEqualityComparer.Instance);
                inherited.Add(allCandidates[edge.CandidateIndex]);
            }
        }

        return inherited;
    }
}
