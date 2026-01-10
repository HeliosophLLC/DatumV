using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree.Mutable;

/// <summary>
/// In-memory representation of a mutable B+Tree leaf page. Decoded from disk on
/// read; rewritten to a fresh page id on insert (COW). Entries are sorted by key.
/// </summary>
internal sealed class MutableLeafPage
{
    private readonly ValueIndexEntry[] _entries;

    /// <summary>Page id within the file.</summary>
    internal uint PageId { get; }

    /// <summary>Number of entries in this page.</summary>
    internal int EntryCount => _entries.Length;

    /// <summary>Page id of the previous leaf in the chain, or <see cref="MutableBPlusTreeConstants.NoLinkedPage"/>.</summary>
    internal uint PreviousLeafPageId { get; }

    /// <summary>Page id of the next leaf in the chain, or <see cref="MutableBPlusTreeConstants.NoLinkedPage"/>.</summary>
    internal uint NextLeafPageId { get; }

    /// <summary>Sorted entries (read-only view).</summary>
    internal ReadOnlySpan<ValueIndexEntry> Entries => _entries;

    internal MutableLeafPage(uint pageId, ValueIndexEntry[] entries, uint previousLeafPageId, uint nextLeafPageId)
    {
        PageId = pageId;
        _entries = entries;
        PreviousLeafPageId = previousLeafPageId;
        NextLeafPageId = nextLeafPageId;
    }

    /// <summary>
    /// Finds the index of the first entry whose key equals <paramref name="key"/>,
    /// or a negative value if no match exists.
    /// </summary>
    internal int BinarySearchFirst(DataValue key)
    {
        int low = 0;
        int high = _entries.Length - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            int comparison = StatisticsPredicateEvaluator.CompareValues(_entries[mid].Key, key);

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
                high = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the position where <paramref name="key"/> should be inserted to keep
    /// the array sorted. Returns the first index whose key is &gt;= <paramref name="key"/>,
    /// or <see cref="EntryCount"/> if all existing keys are smaller.
    /// </summary>
    internal int BinarySearchInsertPosition(DataValue key)
    {
        int low = 0;
        int high = _entries.Length;

        while (low < high)
        {
            int mid = low + ((high - low) / 2);

            if (StatisticsPredicateEvaluator.CompareValues(_entries[mid].Key, key) < 0)
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
}
