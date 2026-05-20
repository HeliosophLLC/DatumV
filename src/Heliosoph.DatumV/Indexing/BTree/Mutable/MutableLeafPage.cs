using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing.BTree.Mutable;

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
    /// Finds the position where <paramref name="entry"/> should be inserted
    /// to keep the array sorted by composite (Key, ChunkIndex,
    /// RowOffsetInChunk). Returns the first index whose composite is &gt;
    /// <paramref name="entry"/>, or <see cref="EntryCount"/> if every
    /// existing entry sorts before it.
    /// </summary>
    /// <remarks>
    /// Comparing the full composite (not just the key) is what keeps
    /// duplicate-key entries in their natural (chunk, row) order — a
    /// later insert of the same key with a larger (chunk, row) lands after
    /// existing duplicates instead of before them.
    /// </remarks>
    internal int BinarySearchInsertPosition(ValueIndexEntry entry)
    {
        int low = 0;
        int high = _entries.Length;

        while (low < high)
        {
            int mid = low + ((high - low) / 2);

            if (CompareEntries(_entries[mid], entry) < 0)
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

    private static int CompareEntries(ValueIndexEntry a, ValueIndexEntry b)
    {
        int cmp = StatisticsPredicateEvaluator.CompareValues(a.Key, b.Key);
        if (cmp != 0) return cmp;

        cmp = a.ChunkIndex.CompareTo(b.ChunkIndex);
        if (cmp != 0) return cmp;

        return a.RowOffsetInChunk.CompareTo(b.RowOffsetInChunk);
    }
}
