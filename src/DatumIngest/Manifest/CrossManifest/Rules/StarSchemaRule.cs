namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects star schema patterns where a central fact table has one-to-many relationships
/// with multiple dimension tables. This is a structural insight — star schemas are common
/// in data warehouses and benefit from specific query and indexing strategies.
/// </summary>
internal sealed class StarSchemaRule : ICrossManifestInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        // Count how many dimension-like relationships each table has.
        // A dimension-like relationship is a OneToMany or ManyToOne join
        // where the "one" side is the dimension and the "many" side is the fact.
        Dictionary<string, List<string>> factToDimensions = new();

        foreach (JoinCandidate candidate in candidates)
        {
            if (candidate.Confidence < thresholds.GraphEdgeMinConfidence)
            {
                continue;
            }

            string? factTable = null;
            string? dimensionTable = null;

            switch (candidate.EstimatedJoinType)
            {
                case JoinClassification.OneToMany:
                    // Left is the "one" (dimension), right is the "many" (fact).
                    factTable = candidate.RightTable;
                    dimensionTable = candidate.LeftTable;
                    break;
                case JoinClassification.ManyToOne:
                    // Left is the "many" (fact), right is the "one" (dimension).
                    factTable = candidate.LeftTable;
                    dimensionTable = candidate.RightTable;
                    break;
            }

            if (factTable is null || dimensionTable is null)
            {
                continue;
            }

            if (!factToDimensions.TryGetValue(factTable, out List<string>? dimensions))
            {
                dimensions = new List<string>();
                factToDimensions[factTable] = dimensions;
            }

            if (!dimensions.Contains(dimensionTable))
            {
                dimensions.Add(dimensionTable);
            }
        }

        foreach (KeyValuePair<string, List<string>> entry in factToDimensions)
        {
            if (entry.Value.Count < thresholds.StarSchemaMinDimensions)
            {
                continue;
            }

            string dimensionList = string.Join(", ", entry.Value.ConvertAll(d => $"'{d}'"));

            EvidenceBuilder evidence = new EvidenceBuilder()
                .Add(entry.Key, "dimensionCount", (long)entry.Value.Count)
                .Add(entry.Key, "dimensions", dimensionList);

            yield return new RawFinding(
                InsightKind.StarSchema,
                InsightCategory.JoinQuality,
                InsightSeverity.Info,
                Math.Min(1.0, 0.6 + (entry.Value.Count * 0.1)),
                InsightScope.CrossManifest,
                $"Table '{entry.Key}' appears to be a fact table in a star schema with {entry.Value.Count} dimension tables: {dimensionList}.",
                "Star schemas are efficient for OLAP workloads but require specific join strategies. Joining all dimensions at once without filtering can produce very large intermediate results.",
                "When querying this star schema, filter the fact table first and join dimension tables incrementally. Consider materializing frequently used dimension lookups.",
                Rationale: null,
                Alternatives: null,
                [entry.Key, .. entry.Value],
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
