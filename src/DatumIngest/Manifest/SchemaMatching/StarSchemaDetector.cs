namespace DatumIngest.Manifest.SchemaMatching;

/// <summary>
/// Detects star schema relationships from a set of table manifests. A star schema
/// consists of a hub table with unique key values connected to multiple spoke tables
/// via foreign key relationships. Reuses the column matching and evidence scoring
/// infrastructure to discover one-to-many and one-to-one candidates, then groups
/// them by hub table and key column. One-to-one pairs (extension tables with
/// near-unique foreign keys) count as valid spokes.
/// </summary>
public static class StarSchemaDetector
{
    /// <summary>
    /// Minimum number of spoke tables required for a table to qualify as a hub.
    /// A table with only one one-to-many relationship is a pair, not a star.
    /// </summary>
    public const int MinSpokeCount = 2;

    /// <summary>
    /// Detects star schema hubs from the given table manifests.
    /// </summary>
    /// <param name="manifests">The table manifests to analyze (at least 2 required).</param>
    /// <param name="thresholds">
    /// Optional thresholds controlling column matching, evidence scoring, and candidate
    /// filtering. Uses <see cref="SchemaMatchingThresholds.Default"/> when null.
    /// </param>
    /// <returns>
    /// A <see cref="StarSchemaResult"/> containing discovered hubs (ordered by descending
    /// spoke count) and any tables not placed in a star relationship.
    /// </returns>
    public static StarSchemaResult Detect(
        IReadOnlyList<ManifestWithName> manifests,
        SchemaMatchingThresholds? thresholds = null)
    {
        if (manifests.Count < 2)
        {
            return new StarSchemaResult
            {
                Tables = manifests.Select(manifest => manifest.Name).ToList(),
                Hubs = [],
                UnmatchedTables = manifests.Select(manifest => manifest.Name).ToList(),
            };
        }

        SchemaMatchingThresholds effectiveThresholds = thresholds ?? SchemaMatchingThresholds.Default;

        // Phase 1: Discover all single-column candidates using the standard pipeline.
        List<JoinCandidate> candidates = DiscoverCandidates(manifests, effectiveThresholds);

        // Phase 2: Extract hub tables from one-to-many relationships.
        List<HubTable> hubs = ExtractHubs(candidates);

        // Phase 3: Identify tables not participating in any star.
        HashSet<string> participatingTables = CollectParticipatingTables(hubs);
        List<string> allTableNames = manifests.Select(manifest => manifest.Name).ToList();

        List<string> unmatchedTables = allTableNames
            .Where(name => !participatingTables.Contains(name))
            .ToList();

        return new StarSchemaResult
        {
            Tables = allTableNames,
            Hubs = hubs,
            UnmatchedTables = unmatchedTables,
        };
    }

    /// <summary>
    /// Runs column matching and evidence scoring across all table pairs to produce
    /// join candidates with cardinality classification. Many-to-many relationships
    /// are excluded; one-to-many, many-to-one, and one-to-one are retained.
    /// </summary>
    private static List<JoinCandidate> DiscoverCandidates(
        IReadOnlyList<ManifestWithName> manifests,
        SchemaMatchingThresholds thresholds)
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

                    // One-to-many, many-to-one, and one-to-one relationships are relevant for
                    // star schema detection. One-to-one pairs arise when the "many" side has a
                    // near-unique foreign key (e.g. payments with ≤5% duplicates). These are
                    // legitimate extension tables and should count as spokes.
                    if (joinType is JoinClassification.ManyToMany)
                    {
                        continue;
                    }

                    candidates.Add(new JoinCandidate
                    {
                        LeftTable = left.Name,
                        RightTable = right.Name,
                        LeftColumns = [match.LeftColumn],
                        RightColumns = [match.RightColumn],
                        Evidence = evidence,
                        Confidence = evidence.CompositeConfidence,
                        EstimatedJoinType = joinType,
                        EstimatedFanout = null,
                        QualityWarnings = null,
                    });
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Groups one-to-many candidates by (hub table, key columns) and builds
    /// <see cref="HubTable"/> instances for groups meeting the minimum spoke threshold.
    /// When multiple candidates connect the same hub to the same spoke on the same
    /// key, the highest-confidence candidate wins.
    /// </summary>
    private static List<HubTable> ExtractHubs(List<JoinCandidate> candidates)
    {
        // Key: (hubTable, keyColumn) → list of (spokeTable, spokeColumn, confidence, classification).
        Dictionary<(string HubTable, string KeyColumn), List<(string SpokeTable, string ForeignKeyColumn, double Confidence, JoinClassification Classification)>> hubGroups = new(HubKeyComparer.Instance);

        foreach (JoinCandidate candidate in candidates)
        {
            string hubTable;
            string hubColumn;
            string spokeTable;
            string spokeColumn;

            if (candidate.EstimatedJoinType == JoinClassification.OneToMany)
            {
                hubTable = candidate.LeftTable;
                hubColumn = candidate.LeftColumns[0];
                spokeTable = candidate.RightTable;
                spokeColumn = candidate.RightColumns[0];
            }
            else if (candidate.EstimatedJoinType == JoinClassification.ManyToOne)
            {
                // ManyToOne: right side is the hub.
                hubTable = candidate.RightTable;
                hubColumn = candidate.RightColumns[0];
                spokeTable = candidate.LeftTable;
                spokeColumn = candidate.LeftColumns[0];
            }
            else
            {
                // OneToOne: both sides have near-unique keys. Add in both directions so
                // the table that accumulates enough spokes from other relationships
                // naturally becomes the hub via the MinSpokeCount filter.
                AddSpoke(hubGroups, candidate.LeftTable, candidate.LeftColumns[0],
                    candidate.RightTable, candidate.RightColumns[0],
                    candidate.Confidence, candidate.EstimatedJoinType);
                AddSpoke(hubGroups, candidate.RightTable, candidate.RightColumns[0],
                    candidate.LeftTable, candidate.LeftColumns[0],
                    candidate.Confidence, candidate.EstimatedJoinType);
                continue;
            }

            AddSpoke(hubGroups, hubTable, hubColumn, spokeTable, spokeColumn,
                candidate.Confidence, candidate.EstimatedJoinType);
        }

        List<HubTable> hubs = new();

        foreach (KeyValuePair<(string HubTable, string KeyColumn), List<(string SpokeTable, string ForeignKeyColumn, double Confidence, JoinClassification Classification)>> entry in hubGroups)
        {
            // Deduplicate: if the same spoke appears multiple times for the same hub+key,
            // keep only the highest-confidence candidate.
            Dictionary<string, (string ForeignKeyColumn, double Confidence, JoinClassification Classification)> bestPerSpoke = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string spokeTable, string foreignKeyColumn, double confidence, JoinClassification classification) in entry.Value)
            {
                if (!bestPerSpoke.TryGetValue(spokeTable, out (string, double Confidence, JoinClassification) existing) ||
                    confidence > existing.Confidence)
                {
                    bestPerSpoke[spokeTable] = (foreignKeyColumn, confidence, classification);
                }
            }

            if (bestPerSpoke.Count < MinSpokeCount)
            {
                continue;
            }

            List<SpokeTable> spokeList = bestPerSpoke
                .OrderByDescending(spoke => spoke.Value.Confidence)
                .Select(spoke => new SpokeTable
                {
                    TableName = spoke.Key,
                    ForeignKeyColumns = [spoke.Value.ForeignKeyColumn],
                    Confidence = spoke.Value.Confidence,
                    JoinClassification = spoke.Value.Classification,
                })
                .ToList();

            hubs.Add(new HubTable
            {
                TableName = entry.Key.HubTable,
                KeyColumns = [entry.Key.KeyColumn],
                Spokes = spokeList,
            });
        }

        // Order by descending spoke count, then by table name for stability.
        hubs.Sort((a, b) =>
        {
            int countComparison = b.SpokeCount.CompareTo(a.SpokeCount);
            return countComparison != 0
                ? countComparison
                : string.Compare(a.TableName, b.TableName, StringComparison.OrdinalIgnoreCase);
        });

        return hubs;
    }

    /// <summary>
    /// Adds a spoke entry to the hub groups dictionary, creating the list if necessary.
    /// </summary>
    private static void AddSpoke(
        Dictionary<(string HubTable, string KeyColumn), List<(string SpokeTable, string ForeignKeyColumn, double Confidence, JoinClassification Classification)>> hubGroups,
        string hubTable, string hubColumn,
        string spokeTable, string spokeColumn,
        double confidence, JoinClassification classification)
    {
        (string, string) key = (hubTable, hubColumn);

        if (!hubGroups.TryGetValue(key, out List<(string, string, double, JoinClassification)>? spokes))
        {
            spokes = new();
            hubGroups[key] = spokes;
        }

        spokes.Add((spokeTable, spokeColumn, confidence, classification));
    }

    /// <summary>
    /// Collects all table names that participate as either a hub or a spoke.
    /// </summary>
    private static HashSet<string> CollectParticipatingTables(List<HubTable> hubs)
    {
        HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);

        foreach (HubTable hub in hubs)
        {
            tables.Add(hub.TableName);

            foreach (SpokeTable spoke in hub.Spokes)
            {
                tables.Add(spoke.TableName);
            }
        }

        return tables;
    }

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
    /// Case-insensitive equality comparer for (hubTable, keyColumn) tuples.
    /// </summary>
    private sealed class HubKeyComparer : IEqualityComparer<(string HubTable, string KeyColumn)>
    {
        internal static readonly HubKeyComparer Instance = new();

        public bool Equals((string HubTable, string KeyColumn) x, (string HubTable, string KeyColumn) y)
        {
            return string.Equals(x.HubTable, y.HubTable, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.KeyColumn, y.KeyColumn, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string HubTable, string KeyColumn) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.HubTable),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.KeyColumn));
        }
    }
}
