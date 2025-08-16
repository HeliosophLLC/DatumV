namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Detects groups of tables that are structurally equivalent — same schema, same hub
/// connections, with only many-to-many edges between them. These typically represent
/// partitions of the same entity (e.g., train/test splits).
/// </summary>
internal static class EquivalentTableDetector
{
    /// <summary>
    /// Minimum fraction of the smaller table's columns that must be shared (by name)
    /// for two tables to be considered structurally equivalent.
    /// </summary>
    private const double MinSchemaOverlap = 0.75;

    /// <summary>
    /// Maximum unique key score for an inter-table candidate to be treated as a
    /// non-key (many-to-many) edge. Candidates with a score above this threshold
    /// indicate a genuine foreign key relationship rather than partition duplication.
    /// </summary>
    private const double MaxUniqueKeyScore = 0.1;

    /// <summary>
    /// Detects equivalent table groups from the given manifests and candidates.
    /// </summary>
    /// <param name="manifests">Named manifests for all tables under analysis.</param>
    /// <param name="candidates">All scored join candidates.</param>
    /// <returns>Detected equivalent table groups, or an empty list if none are found.</returns>
    internal static IReadOnlyList<EquivalentTableGroup> Detect(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates)
    {
        // Index manifests by name for fast lookup.
        Dictionary<string, ManifestWithName> manifestsByName = new(StringComparer.OrdinalIgnoreCase);

        foreach (ManifestWithName manifest in manifests)
        {
            manifestsByName[manifest.Name] = manifest;
        }

        // Find pairwise table combinations that share enough schema overlap.
        List<EquivalentTableGroup> groups = new();

        for (int i = 0; i < manifests.Count; i++)
        {
            for (int j = i + 1; j < manifests.Count; j++)
            {
                ManifestWithName left = manifests[i];
                ManifestWithName right = manifests[j];

                IReadOnlyList<string> sharedColumns = FindSharedColumnNames(left, right);
                int smallerColumnCount = Math.Min(
                    left.Manifest.Features.Count,
                    right.Manifest.Features.Count);

                if (smallerColumnCount == 0)
                {
                    continue;
                }

                double schemaOverlap = (double)sharedColumns.Count / smallerColumnCount;

                if (schemaOverlap < MinSchemaOverlap)
                {
                    continue;
                }

                // Check that ALL inter-table candidates are ManyToMany with no unique key.
                if (!AllInterTableEdgesAreManyToMany(left.Name, right.Name, candidates))
                {
                    continue;
                }

                // Check that both tables connect to at least one common hub table.
                if (!ShareHubConnections(left.Name, right.Name, candidates))
                {
                    continue;
                }

                // Pick preferred table by max aggregate confidence to hub tables.
                string preferredTable = PickPreferredTable(
                    left.Name, right.Name, candidates);

                string nonPreferredTable = preferredTable == left.Name ? right.Name : left.Name;

                double preferredMaxConfidence = MaxHubConfidence(preferredTable, candidates);
                double nonPreferredMaxConfidence = MaxHubConfidence(nonPreferredTable, candidates);

                Dictionary<string, long> rowCounts = new()
                {
                    [left.Name] = left.Manifest.RowCount,
                    [right.Name] = right.Manifest.RowCount,
                };

                string reason = $"Schema overlap {schemaOverlap:P0} ({sharedColumns.Count}/{smallerColumnCount} columns). " +
                    $"All inter-table edges are many-to-many with no unique key. " +
                    $"'{preferredTable}' has stronger hub connections " +
                    $"(max confidence {preferredMaxConfidence:F3} vs {nonPreferredMaxConfidence:F3}).";

                groups.Add(new EquivalentTableGroup
                {
                    Tables = [left.Name, right.Name],
                    SharedColumns = sharedColumns,
                    SchemaOverlap = schemaOverlap,
                    PreferredTable = preferredTable,
                    Reason = reason,
                    RowCounts = rowCounts,
                });
            }
        }

        return groups;
    }

    /// <summary>
    /// Finds column names present in both manifests (case-insensitive match).
    /// </summary>
    private static IReadOnlyList<string> FindSharedColumnNames(ManifestWithName left, ManifestWithName right)
    {
        HashSet<string> rightColumnNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (FeatureManifest feature in right.Manifest.Features)
        {
            rightColumnNames.Add(feature.Name);
        }

        List<string> shared = new();

        foreach (FeatureManifest feature in left.Manifest.Features)
        {
            if (rightColumnNames.Contains(feature.Name))
            {
                shared.Add(feature.Name);
            }
        }

        return shared;
    }

    /// <summary>
    /// Returns true if every inter-table candidate between the two tables is
    /// ManyToMany with a uniqueKeyScore below <see cref="MaxUniqueKeyScore"/>.
    /// Also returns false if there are no inter-table candidates at all (no evidence).
    /// </summary>
    private static bool AllInterTableEdgesAreManyToMany(
        string tableA,
        string tableB,
        IReadOnlyList<JoinCandidate> candidates)
    {
        bool foundAny = false;

        foreach (JoinCandidate candidate in candidates)
        {
            if (!IsEdgeBetween(candidate, tableA, tableB))
            {
                continue;
            }

            foundAny = true;

            if (candidate.EstimatedJoinType != JoinClassification.ManyToMany)
            {
                return false;
            }

            if (candidate.Evidence.UniqueKeyScore > MaxUniqueKeyScore)
            {
                return false;
            }
        }

        return foundAny;
    }

    /// <summary>
    /// Returns true if both tables share at least one common hub table — i.e., both
    /// have candidates connecting to the same third table on the same column name.
    /// </summary>
    private static bool ShareHubConnections(
        string tableA,
        string tableB,
        IReadOnlyList<JoinCandidate> candidates)
    {
        // Collect hub tables that tableA connects to (excluding tableB).
        Dictionary<string, HashSet<string>> hubsA = CollectHubConnections(tableA, tableB, candidates);
        Dictionary<string, HashSet<string>> hubsB = CollectHubConnections(tableB, tableA, candidates);

        // Check if any hub table is shared with at least one common join column.
        foreach (KeyValuePair<string, HashSet<string>> entry in hubsA)
        {
            if (hubsB.TryGetValue(entry.Key, out HashSet<string>? columnsB))
            {
                foreach (string column in entry.Value)
                {
                    if (columnsB.Contains(column))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Collects hub table connections for a given table — maps hub table name to
    /// the set of column names used to join to it.
    /// </summary>
    private static Dictionary<string, HashSet<string>> CollectHubConnections(
        string table,
        string excludeTable,
        IReadOnlyList<JoinCandidate> candidates)
    {
        Dictionary<string, HashSet<string>> hubs = new(StringComparer.OrdinalIgnoreCase);

        foreach (JoinCandidate candidate in candidates)
        {
            string? hubTable = null;
            string? joinColumn = null;

            if (string.Equals(candidate.LeftTable, table, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.RightTable, excludeTable, StringComparison.OrdinalIgnoreCase))
            {
                hubTable = candidate.RightTable;
                joinColumn = candidate.LeftColumns[0];
            }
            else if (string.Equals(candidate.RightTable, table, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(candidate.LeftTable, excludeTable, StringComparison.OrdinalIgnoreCase))
            {
                hubTable = candidate.LeftTable;
                joinColumn = candidate.RightColumns[0];
            }

            if (hubTable is null || joinColumn is null)
            {
                continue;
            }

            // Only consider non-ManyToMany candidates as hub connections.
            if (candidate.EstimatedJoinType == JoinClassification.ManyToMany)
            {
                continue;
            }

            if (!hubs.TryGetValue(hubTable, out HashSet<string>? columns))
            {
                columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                hubs[hubTable] = columns;
            }

            columns.Add(joinColumn);
        }

        return hubs;
    }

    /// <summary>
    /// Picks the preferred table by comparing the maximum hub confidence for each table.
    /// The table with the higher maximum confidence to any hub table is preferred.
    /// </summary>
    private static string PickPreferredTable(
        string tableA,
        string tableB,
        IReadOnlyList<JoinCandidate> candidates)
    {
        double maxA = MaxHubConfidence(tableA, candidates);
        double maxB = MaxHubConfidence(tableB, candidates);

        return maxA >= maxB ? tableA : tableB;
    }

    /// <summary>
    /// Gets the maximum confidence of any non-ManyToMany candidate involving the given table
    /// and a different table (its hub connections).
    /// </summary>
    private static double MaxHubConfidence(string table, IReadOnlyList<JoinCandidate> candidates)
    {
        double max = 0.0;

        foreach (JoinCandidate candidate in candidates)
        {
            if (candidate.EstimatedJoinType == JoinClassification.ManyToMany)
            {
                continue;
            }

            if (string.Equals(candidate.LeftTable, table, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.RightTable, table, StringComparison.OrdinalIgnoreCase))
            {
                if (candidate.Confidence > max)
                {
                    max = candidate.Confidence;
                }
            }
        }

        return max;
    }

    /// <summary>
    /// Returns true if the candidate is an edge between the two specified tables.
    /// </summary>
    private static bool IsEdgeBetween(JoinCandidate candidate, string tableA, string tableB)
    {
        return (string.Equals(candidate.LeftTable, tableA, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.RightTable, tableB, StringComparison.OrdinalIgnoreCase)) ||
               (string.Equals(candidate.LeftTable, tableB, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.RightTable, tableA, StringComparison.OrdinalIgnoreCase));
    }
}
