using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing.BTree;

namespace DatumIngest.Indexing;

/// <summary>
/// Collection of <see cref="BPlusTreeColumnIndex"/> instances keyed by column name.
/// Analogous to <see cref="SortedValueIndexSet"/> but for B+Tree-backed indexes
/// that provide demand-paged, compressed leaf access.
/// </summary>
internal sealed class BPlusTreeIndexSet
{
    private readonly Dictionary<string, BPlusTreeColumnIndex> _indexes;

    /// <summary>Column names that have B+Tree indexes.</summary>
    internal IReadOnlyCollection<string> ColumnNames => _indexes.Keys;

    /// <summary>Number of indexed columns.</summary>
    internal int Count => _indexes.Count;

    /// <summary>
    /// Creates a B+Tree index set from a pre-built dictionary.
    /// </summary>
    /// <param name="indexes">B+Tree column indexes keyed by column name (case-insensitive).</param>
    internal BPlusTreeIndexSet(Dictionary<string, BPlusTreeColumnIndex> indexes)
    {
        _indexes = indexes;
    }

    /// <summary>
    /// Retrieves the B+Tree column index for a specific column.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The column index, or <c>null</c> if not available.</param>
    /// <returns><c>true</c> if a B+Tree index exists for the specified column.</returns>
    internal bool TryGetIndex(string columnName, [NotNullWhen(true)] out BPlusTreeColumnIndex? index)
    {
        return _indexes.TryGetValue(columnName, out index);
    }
}
