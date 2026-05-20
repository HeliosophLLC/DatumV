namespace Heliosoph.DatumV.Manifest;

/// <summary>
/// Describes the preferred index type for a single column, auto-generated from
/// manifest statistics or supplied explicitly. Used by <see cref="Indexing.SourceIndexBuilder"/>
/// to override automatic index-type selection.
/// </summary>
/// <param name="ColumnName">The column name this hint applies to.</param>
/// <param name="PreferredType">The recommended index type for the column.</param>
public sealed record ColumnIndexHint(string ColumnName, IndexHintType PreferredType);

/// <summary>
/// The index type recommended for a column. <see cref="Auto"/> defers to the
/// runtime auto-cascade (cardinality ≤ 256 → Bitmap, entry count &gt; 5 M → B+Tree, else → Sorted).
/// </summary>
public enum IndexHintType
{
    /// <summary>No index should be built for this column.</summary>
    None,

    /// <summary>A bitmap index is recommended (low cardinality, ≤ 256 distinct values).</summary>
    Bitmap,

    /// <summary>A sorted value index is recommended.</summary>
    Sorted,

    /// <summary>A B+Tree index is recommended (very high cardinality, &gt; 5 M entries).</summary>
    BTree,

    /// <summary>Defer to the runtime auto-cascade logic.</summary>
    Auto
}
