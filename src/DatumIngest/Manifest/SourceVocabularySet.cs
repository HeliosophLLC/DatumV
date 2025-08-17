namespace DatumIngest.Manifest;

/// <summary>
/// Container for per-table column vocabularies within a single source file.
/// A <c>.datum-vocabulary</c> sidecar contains a <see cref="SourceVocabularySet"/>,
/// keyed by logical table name, mirroring the structure of <see cref="SourceManifest"/>.
/// </summary>
/// <remarks>
/// Each table maps to a <see cref="TableVocabularySet"/> that holds the sorted distinct
/// values for every column classified as <see cref="ColumnRole.Identifier"/> or
/// <see cref="ColumnRole.ForeignKey"/> whose vocabulary was not capped during indexing.
/// </remarks>
public sealed class SourceVocabularySet
{
    /// <summary>
    /// Gets the per-table vocabulary sets, keyed by catalog table name.
    /// </summary>
    public required IReadOnlyDictionary<string, TableVocabularySet> Tables { get; init; }

    /// <summary>
    /// Extracts vocabularies from a <see cref="SourceManifest"/>, returning a
    /// <see cref="SourceVocabularySet"/> containing only columns that have a non-null
    /// <see cref="FeatureManifest.Vocabulary"/>. Returns <c>null</c> if no vocabularies
    /// are present in any table.
    /// </summary>
    /// <param name="sourceManifest">The source manifest whose vocabularies to extract.</param>
    /// <returns>
    /// A vocabulary set containing all non-null vocabularies, or <c>null</c> if the
    /// manifest contains no vocabulary data.
    /// </returns>
    public static SourceVocabularySet? ExtractFrom(SourceManifest sourceManifest)
    {
        Dictionary<string, TableVocabularySet> tables = new();

        foreach (KeyValuePair<string, QueryResultsManifest> tableEntry in sourceManifest.Tables)
        {
            TableVocabularySet? tableVocabularySet = TableVocabularySet.ExtractFrom(tableEntry.Value);

            if (tableVocabularySet is not null)
            {
                tables[tableEntry.Key] = tableVocabularySet;
            }
        }

        return tables.Count > 0
            ? new SourceVocabularySet { Tables = tables }
            : null;
    }

    /// <summary>
    /// Applies vocabularies from this set to the corresponding feature manifests
    /// in the given <see cref="SourceManifest"/>. Columns whose names match entries
    /// in this vocabulary set will have their <see cref="FeatureManifest.Vocabulary"/>
    /// populated with a <see cref="ColumnVocabulary"/> instance.
    /// </summary>
    /// <param name="sourceManifest">The source manifest to attach vocabularies to.</param>
    public void ApplyTo(SourceManifest sourceManifest)
    {
        foreach (KeyValuePair<string, QueryResultsManifest> tableEntry in sourceManifest.Tables)
        {
            if (!Tables.TryGetValue(tableEntry.Key, out TableVocabularySet? tableVocabularySet))
            {
                continue;
            }

            tableVocabularySet.ApplyTo(tableEntry.Value);
        }
    }
}
