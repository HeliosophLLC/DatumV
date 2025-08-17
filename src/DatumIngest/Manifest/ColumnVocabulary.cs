namespace DatumIngest.Manifest;

/// <summary>
/// An exhaustive, sorted vocabulary of all distinct values observed in a column.
/// Values are stored as strings in ordinal sort order for O(|A| + |B|) merge-based
/// set operations (intersection, union, containment).
/// </summary>
/// <remarks>
/// Only populated for columns classified as <see cref="ColumnRole.Identifier"/> or
/// <see cref="ColumnRole.ForeignKey"/> whose estimated distinct count falls within the
/// vocabulary accumulator's cap. When present, enables exact Jaccard and bidirectional
/// containment computation — replacing the approximate TopK-based Jaccard.
/// </remarks>
public sealed class ColumnVocabulary
{
    /// <summary>
    /// Gets the distinct values sorted by <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public required IReadOnlyList<string> Values { get; init; }

    /// <summary>
    /// Gets the number of distinct values in the vocabulary.
    /// </summary>
    public int Count => Values.Count;

    /// <summary>
    /// Computes the intersection size of two sorted vocabularies using a merge scan.
    /// </summary>
    /// <param name="left">Left vocabulary (sorted ordinal).</param>
    /// <param name="right">Right vocabulary (sorted ordinal).</param>
    /// <returns>The number of values present in both vocabularies.</returns>
    public static int ComputeIntersectionSize(ColumnVocabulary left, ColumnVocabulary right)
    {
        IReadOnlyList<string> leftValues = left.Values;
        IReadOnlyList<string> rightValues = right.Values;
        int leftIndex = 0;
        int rightIndex = 0;
        int intersection = 0;

        while (leftIndex < leftValues.Count && rightIndex < rightValues.Count)
        {
            int comparison = string.Compare(leftValues[leftIndex], rightValues[rightIndex], StringComparison.Ordinal);

            if (comparison < 0)
            {
                leftIndex++;
            }
            else if (comparison > 0)
            {
                rightIndex++;
            }
            else
            {
                intersection++;
                leftIndex++;
                rightIndex++;
            }
        }

        return intersection;
    }

    /// <summary>
    /// Computes exact Jaccard similarity: |A ∩ B| / |A ∪ B|.
    /// </summary>
    /// <param name="left">Left vocabulary.</param>
    /// <param name="right">Right vocabulary.</param>
    /// <returns>Jaccard coefficient in [0, 1], or 0 if both are empty.</returns>
    public static double ComputeJaccard(ColumnVocabulary left, ColumnVocabulary right)
    {
        if (left.Count == 0 && right.Count == 0)
        {
            return 0.0;
        }

        int intersection = ComputeIntersectionSize(left, right);
        int union = left.Count + right.Count - intersection;

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Computes directional containment: |A ∩ B| / |A|.
    /// A containment of 1.0 means every value in <paramref name="source"/> exists
    /// in <paramref name="target"/>, typical of a foreign key fully contained in a
    /// primary key column.
    /// </summary>
    /// <param name="source">The column whose coverage is measured.</param>
    /// <param name="target">The column against which coverage is measured.</param>
    /// <returns>Containment ratio in [0, 1], or 0 if <paramref name="source"/> is empty.</returns>
    public static double ComputeContainment(ColumnVocabulary source, ColumnVocabulary target)
    {
        if (source.Count == 0)
        {
            return 0.0;
        }

        int intersection = ComputeIntersectionSize(source, target);

        return (double)intersection / source.Count;
    }
}
