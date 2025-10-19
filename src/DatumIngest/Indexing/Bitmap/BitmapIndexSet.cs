using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.BTree;

namespace DatumIngest.Indexing.Bitmap;

/// <summary>
/// Collection of <see cref="BitmapColumnIndex"/> instances keyed by column name.
/// Analogous to <see cref="SortedIndex"/> and <see cref="BPlusTreeIndexSet"/>
/// but for bitmap-backed indexes that provide per-value, per-chunk bitsets.
/// </summary>
internal sealed class BitmapIndexSet
{
    private readonly Dictionary<string, BitmapColumnIndex> _indexes;

    /// <summary>Column names that have bitmap indexes.</summary>
    internal IReadOnlyCollection<string> ColumnNames => _indexes.Keys;

    /// <summary>Number of indexed columns.</summary>
    internal int Count => _indexes.Count;

    /// <summary>
    /// Creates a bitmap index set from a pre-built dictionary.
    /// </summary>
    /// <param name="indexes">Bitmap column indexes keyed by column name (case-insensitive).</param>
    internal BitmapIndexSet(Dictionary<string, BitmapColumnIndex> indexes)
    {
        _indexes = indexes;
    }

    /// <summary>
    /// Retrieves the bitmap column index for a specific column.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The bitmap column index, or <c>null</c> if not available.</param>
    /// <returns><c>true</c> if a bitmap index exists for the specified column.</returns>
    internal bool TryGetIndex(string columnName, [NotNullWhen(true)] out BitmapColumnIndex? index)
    {
        return _indexes.TryGetValue(columnName, out index);
    }
}
