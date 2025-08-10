using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree;

/// <summary>
/// In-memory representation of a deserialized B+Tree leaf page.
/// Contains key–value entries sorted by key, plus pointers to adjacent
/// leaves for chain traversal. Leaf pages form a doubly-linked list
/// at the bottom of the tree.
/// </summary>
internal sealed class BPlusTreeLeafPage
{
    private readonly ValueIndexEntry[] _entries;

    /// <summary>Zero-based page index within the B+Tree section.</summary>
    internal uint PageIndex { get; }

    /// <summary>Number of entries in this leaf page.</summary>
    internal int EntryCount => _entries.Length;

    /// <summary>
    /// Page index of the previous leaf in the chain, or
    /// <see cref="BPlusTreeConstants.NoLinkedPage"/> if this is the first leaf.
    /// </summary>
    internal uint PreviousLeafPageIndex { get; }

    /// <summary>
    /// Page index of the next leaf in the chain, or
    /// <see cref="BPlusTreeConstants.NoLinkedPage"/> if this is the last leaf.
    /// </summary>
    internal uint NextLeafPageIndex { get; }

    /// <summary>The sorted entries (for serialization and testing).</summary>
    internal ReadOnlySpan<ValueIndexEntry> Entries => _entries;

    /// <summary>
    /// Creates a leaf page from pre-sorted entries.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index within the section.</param>
    /// <param name="entries">Entries sorted by key value.</param>
    /// <param name="previousLeafPageIndex">Previous leaf's page index.</param>
    /// <param name="nextLeafPageIndex">Next leaf's page index.</param>
    internal BPlusTreeLeafPage(
        uint pageIndex,
        ValueIndexEntry[] entries,
        uint previousLeafPageIndex,
        uint nextLeafPageIndex)
    {
        PageIndex = pageIndex;
        _entries = entries;
        PreviousLeafPageIndex = previousLeafPageIndex;
        NextLeafPageIndex = nextLeafPageIndex;
    }

    /// <summary>
    /// Returns the entry at the given index within this page.
    /// </summary>
    internal ValueIndexEntry GetEntry(int index) => _entries[index];

    /// <summary>
    /// Returns the key at the given index within this page.
    /// </summary>
    internal DataValue GetKey(int index) => _entries[index].Key;

    /// <summary>
    /// Finds the index of the first entry whose key equals <paramref name="key"/>.
    /// Returns a negative value if no matching entry exists in this page.
    /// </summary>
    internal int BinarySearchFirst(DataValue key)
    {
        int low = 0;
        int high = _entries.Length - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            int comparison = CompareKeys(_entries[mid].Key, key);

            if (comparison < 0)
            {
                low = mid + 1;
            }
            else if (comparison > 0)
            {
                high = mid - 1;
            }
            else
            {
                result = mid;
                high = mid - 1; // Continue searching left for the first occurrence.
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the index of the first entry whose key is greater than or equal to
    /// <paramref name="key"/>. Returns <see cref="EntryCount"/> if all entries
    /// have keys less than the search key.
    /// </summary>
    internal int BinarySearchFirstGreaterOrEqual(DataValue key)
    {
        int low = 0;
        int high = _entries.Length;

        while (low < high)
        {
            int mid = low + (high - low) / 2;

            if (CompareKeys(_entries[mid].Key, key) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static int CompareKeys(DataValue left, DataValue right)
    {
        return StatisticsPredicateEvaluator.CompareValues(left, right);
    }
}
