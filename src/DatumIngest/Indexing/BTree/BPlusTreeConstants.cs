namespace DatumIngest.Indexing.BTree;

/// <summary>
/// Constants for the B+Tree on-disk format used within <c>.datum-index</c> files.
/// Pages are fixed 8 KiB, laid out sequentially within the B+Tree section.
/// </summary>
internal static class BPlusTreeConstants
{
    /// <summary>Page size in bytes. All pages (leaf and internal) occupy exactly this many bytes on disk.</summary>
    internal const int PageSize = 8192;

    /// <summary>Size of the common page header in bytes: page type (1) + key count (2) + reserved (1).</summary>
    internal const int PageHeaderSize = 4;

    /// <summary>
    /// Additional header bytes in a leaf page after the common header:
    /// previous leaf (4) + next leaf (4) + uncompressed payload size (4) + compressed payload size (4).
    /// </summary>
    internal const int LeafHeaderSize = 16;

    /// <summary>Maximum compressed payload a leaf page can hold.</summary>
    internal const int LeafPayloadCapacity = PageSize - PageHeaderSize - LeafHeaderSize;

    /// <summary>Usable space in an internal page for keys and child pointers.</summary>
    internal const int InternalPayloadCapacity = PageSize - PageHeaderSize;

    /// <summary>Sentinel value indicating no linked leaf (used for first/last leaf pointers).</summary>
    internal const uint NoLinkedPage = uint.MaxValue;

    /// <summary>
    /// Size of the B+Tree section header written before the page array:
    /// column name (length-prefixed string, variable) + DataKind (1) + root page index (4)
    /// + entry count (8) + tree height (2) + page size (2) + page count (4).
    /// The fixed-size portion is 21 bytes; the column name is variable.
    /// </summary>
    internal const int SectionHeaderFixedSize = 21;
}

/// <summary>
/// Discriminates page types within a B+Tree.
/// </summary>
internal enum BPlusTreePageType : byte
{
    /// <summary>Internal (branch) node containing separator keys and child page pointers.</summary>
    Internal = 1,

    /// <summary>Leaf node containing key-RowPointer pairs with compressed payload.</summary>
    Leaf = 2,
}
