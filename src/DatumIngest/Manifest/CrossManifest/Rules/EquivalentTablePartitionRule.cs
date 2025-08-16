namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Emits an <see cref="InsightKind.EquivalentTablePartition"/> insight when the
/// <see cref="EquivalentTableDetector"/> identifies tables that are structurally
/// equivalent partitions of the same entity (e.g., train/test splits).
/// </summary>
internal sealed class EquivalentTablePartitionRule : ICrossManifestInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        foreach (EquivalentTableGroup group in groups)
        {
            string tableList = string.Join(" and ", group.Tables.Select(t =>
            {
                long rowCount = group.RowCounts[t];
                return $"'{t}' ({FormatRowCount(rowCount)} rows)";
            }));

            long largerRowCount = group.RowCounts.Values.Max();
            long smallerRowCount = group.RowCounts.Values.Min();
            double ratio = smallerRowCount > 0 ? (double)largerRowCount / smallerRowCount : 0;

            string splitHint = ratio > 1.5
                ? $" The row count ratio ({ratio:F1}:1) suggests a train/test split."
                : string.Empty;

            EvidenceBuilder evidence = new();

            foreach (string table in group.Tables)
            {
                evidence
                    .Add(table, "sharedColumns", (long)group.SharedColumns.Count)
                    .Add(table, "schemaOverlap", group.SchemaOverlap)
                    .Add(table, "rowCount", group.RowCounts[table])
                    .Add(table, "selected", table == group.PreferredTable);
            }

            yield return new RawFinding(
                InsightKind.EquivalentTablePartition,
                InsightCategory.JoinQuality,
                InsightSeverity.Info,
                Confidence: Math.Min(1.0, 0.5 + (group.SchemaOverlap * 0.5)),
                InsightScope.CrossManifest,
                $"Tables {tableList} share {group.SharedColumns.Count}/{group.SharedColumns.Count} columns " +
                $"with identical names. All edges between them are many-to-many with no unique key. " +
                $"They appear to be partitions of the same entity.",
                "Joining both into the same query produces a Cartesian product on shared columns. " +
                "Using both simultaneously will explode row counts.",
                $"Primary graph uses '{group.PreferredTable}' (stronger hub connections). " +
                $"An alternate graph using the other partition is available.{splitHint}",
                Rationale: null,
                Alternatives:
                [
                    "UNION ALL both tables before joining if you need the combined dataset.",
                    $"Use the smaller table for faster iteration during development.",
                ],
                group.Tables.ToList(),
                Actions: [],
                ConflictGroup: null,
                evidence.Build());
        }
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
}
