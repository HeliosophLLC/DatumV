using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree;

/// <summary>
/// Header written at the start of the B+Tree section within a <c>.datum-index</c> file,
/// before the contiguous array of B+Tree pages. Describes the tree's shape so readers
/// can locate the root and compute page file offsets.
/// </summary>
/// <param name="ColumnName">The indexed column name.</param>
/// <param name="KeyKind">The <see cref="DataKind"/> of the indexed column's values.</param>
/// <param name="RootPageIndex">Zero-based index of the root page within the section's page array.</param>
/// <param name="EntryCount">Total number of key-RowPointer pairs across all leaves.</param>
/// <param name="TreeHeight">Number of levels in the tree (1 = leaf-only, 2 = root + leaves, etc.).</param>
/// <param name="PageSize">Page size in bytes (always <see cref="BPlusTreeConstants.PageSize"/>).</param>
/// <param name="PageCount">Total number of pages (leaves + internals) in the section.</param>
internal readonly record struct BPlusTreeSectionHeader(
    string ColumnName,
    DataKind KeyKind,
    uint RootPageIndex,
    long EntryCount,
    ushort TreeHeight,
    ushort PageSize,
    uint PageCount);
