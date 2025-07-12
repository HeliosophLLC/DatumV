using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Indexing;

/// <summary>
/// Collection of <see cref="SortedValueIndex"/> instances keyed by column name.
/// Provides O(log n) key lookup and range scanning per indexed column.
/// </summary>
public sealed class SortedValueIndexSet
{
    private readonly Dictionary<string, SortedValueIndex> _indexes;

    /// <summary>Column names that have sorted value indexes.</summary>
    public IReadOnlyCollection<string> ColumnNames => _indexes.Keys;

    /// <summary>Number of indexed columns.</summary>
    public int Count => _indexes.Count;

    /// <summary>The underlying indexes dictionary (for serialization).</summary>
    internal IReadOnlyDictionary<string, SortedValueIndex> Indexes => _indexes;

    /// <summary>
    /// Creates a sorted value index set from a pre-built dictionary.
    /// </summary>
    /// <param name="indexes">Sorted value indexes keyed by column name (case-insensitive).</param>
    public SortedValueIndexSet(Dictionary<string, SortedValueIndex> indexes)
    {
        _indexes = indexes;
    }

    /// <summary>
    /// Retrieves the sorted value index for a specific column.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The sorted value index, or <c>null</c> if not available.</param>
    /// <returns><c>true</c> if a sorted index exists for the specified column.</returns>
    public bool TryGetIndex(string columnName, [NotNullWhen(true)] out SortedValueIndex? index)
    {
        return _indexes.TryGetValue(columnName, out index);
    }

    /// <summary>
    /// Checks whether a sorted value index exists for the specified column.
    /// </summary>
    /// <param name="columnName">Column name to check.</param>
    /// <returns><c>true</c> if a sorted index is available for this column.</returns>
    public bool HasColumn(string columnName)
    {
        return _indexes.ContainsKey(columnName);
    }
}
