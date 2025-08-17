namespace DatumIngest.Manifest;

/// <summary>
/// Column vocabularies for a single logical table. Each entry maps a column name to its
/// sorted list of distinct values, enabling exact Jaccard and containment scoring during
/// schema matching.
/// </summary>
public sealed class TableVocabularySet
{
    /// <summary>
    /// Gets the column vocabularies, keyed by column name.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Columns { get; init; }

    /// <summary>
    /// Extracts vocabularies from a <see cref="QueryResultsManifest"/>, returning a
    /// <see cref="TableVocabularySet"/> containing only columns that have a non-null
    /// <see cref="FeatureManifest.Vocabulary"/>. Returns <c>null</c> if no vocabularies
    /// are present.
    /// </summary>
    /// <param name="manifest">The table manifest whose vocabularies to extract.</param>
    /// <returns>
    /// A vocabulary set for this table, or <c>null</c> if no features have vocabularies.
    /// </returns>
    public static TableVocabularySet? ExtractFrom(QueryResultsManifest manifest)
    {
        Dictionary<string, IReadOnlyList<string>> columns = new();

        foreach (FeatureManifest feature in manifest.Features)
        {
            if (feature.Vocabulary is not null)
            {
                columns[feature.Name] = feature.Vocabulary.Values;
            }
        }

        return columns.Count > 0
            ? new TableVocabularySet { Columns = columns }
            : null;
    }

    /// <summary>
    /// Applies vocabularies from this set to the corresponding feature manifests
    /// in the given <see cref="QueryResultsManifest"/>. Columns whose names match
    /// entries in this vocabulary set will have their <see cref="FeatureManifest.Vocabulary"/>
    /// populated.
    /// </summary>
    /// <param name="manifest">The table manifest to attach vocabularies to.</param>
    public void ApplyTo(QueryResultsManifest manifest)
    {
        foreach (FeatureManifest feature in manifest.Features)
        {
            if (Columns.TryGetValue(feature.Name, out IReadOnlyList<string>? values))
            {
                feature.Vocabulary = new ColumnVocabulary { Values = values };
            }
        }
    }
}
