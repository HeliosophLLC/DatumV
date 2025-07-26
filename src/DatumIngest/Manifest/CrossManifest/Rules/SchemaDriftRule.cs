namespace DatumIngest.Manifest.CrossManifest.Rules;

using DatumIngest.Manifest.Insights;
using DatumIngest.Manifest.Insights.Rules;

/// <summary>
/// Detects columns with the same name but different <see cref="DatumIngest.Model.DataKind"/>
/// across tables, suggesting schema drift or incompatible data sources.
/// </summary>
internal sealed class SchemaDriftRule : ICrossManifestInsightRule
{
    /// <inheritdoc />
    public IEnumerable<RawFinding> Evaluate(
        IReadOnlyList<ManifestWithName> manifests,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        // Build a map of column name → [(tableName, DataKind)].
        Dictionary<string, List<(string TableName, Model.DataKind Kind)>> columnTypes = new(StringComparer.OrdinalIgnoreCase);

        foreach (ManifestWithName manifest in manifests)
        {
            foreach (FeatureManifest feature in manifest.Manifest.Features)
            {
                if (!columnTypes.TryGetValue(feature.Name, out List<(string, Model.DataKind)>? entries))
                {
                    entries = new List<(string, Model.DataKind)>();
                    columnTypes[feature.Name] = entries;
                }

                entries.Add((manifest.Name, feature.Kind));
            }
        }

        foreach (KeyValuePair<string, List<(string TableName, Model.DataKind Kind)>> entry in columnTypes)
        {
            List<(string TableName, Model.DataKind Kind)> occurrences = entry.Value;

            if (occurrences.Count < 2)
            {
                continue;
            }

            // Check if all types are the same.
            Model.DataKind firstKind = occurrences[0].Kind;
            bool hasDrift = false;

            foreach ((string _, Model.DataKind kind) in occurrences)
            {
                if (kind != firstKind)
                {
                    hasDrift = true;
                    break;
                }
            }

            if (!hasDrift)
            {
                continue;
            }

            string columnName = entry.Key;
            List<string> affectedTables = new(occurrences.Count);
            EvidenceBuilder evidence = new();

            foreach ((string tableName, Model.DataKind kind) in occurrences)
            {
                affectedTables.Add(tableName);
                evidence.Add(tableName, $"{columnName}.dataKind", kind.ToString());
            }

            string typeList = string.Join(", ", occurrences.ConvertAll(
                occurrence => $"'{occurrence.TableName}' ({occurrence.Kind})"));

            yield return new RawFinding(
                InsightKind.SchemaDrift,
                InsightCategory.JoinQuality,
                InsightSeverity.Warning,
                0.85,
                InsightScope.CrossManifest,
                $"Column '{columnName}' has different types across tables: {typeList}.",
                "Joining on columns with mismatched types may cause implicit casting, data loss, or join failures. It often indicates schema evolution or incompatible data sources.",
                $"Harmonize the type of '{columnName}' across tables before joining. Cast to the wider type or investigate why schemas diverge.",
                Rationale: null,
                Alternatives: null,
                affectedTables,
                [],
                ConflictGroup: null,
                evidence.Build());
        }
    }
}
