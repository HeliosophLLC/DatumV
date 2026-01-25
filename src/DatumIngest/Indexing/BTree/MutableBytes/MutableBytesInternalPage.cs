namespace DatumIngest.Indexing.BTree.MutableBytes;

/// <summary>
/// In-memory representation of a bytes-keyed B+Tree internal (branch) page.
/// Each page has <c>KeyCount + 1</c> child pointers: child i covers all
/// keys in [Key[i-1], Key[i]).
/// </summary>
internal sealed class MutableBytesInternalPage
{
    private readonly byte[][] _keys;
    private readonly uint[] _childPageIds;

    internal uint PageId { get; }

    internal int KeyCount => _keys.Length;

    internal int ChildCount => _childPageIds.Length;

    internal ReadOnlySpan<byte[]> Keys => _keys;

    internal ReadOnlySpan<uint> ChildPageIds => _childPageIds;

    internal MutableBytesInternalPage(uint pageId, byte[][] keys, uint[] childPageIds)
    {
        if (childPageIds.Length != keys.Length + 1)
        {
            throw new ArgumentException(
                $"Internal page child count ({childPageIds.Length}) must equal key count + 1 ({keys.Length + 1}).",
                nameof(childPageIds));
        }

        PageId = pageId;
        _keys = keys;
        _childPageIds = childPageIds;
    }

    /// <summary>
    /// Returns the slot index <c>i</c> into the child-page array such that
    /// the subtree rooted at child <c>i</c> may contain <paramref name="key"/>.
    /// Equivalently, the number of separator keys that are &lt;=
    /// <paramref name="key"/>.
    /// </summary>
    internal int FindChildSlot(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = _keys.Length;

        while (low < high)
        {
            int mid = low + ((high - low) / 2);

            if (((ReadOnlySpan<byte>)_keys[mid]).SequenceCompareTo(key) <= 0)
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

    /// <summary>Returns the child page id at the given slot (0-based).</summary>
    internal uint GetChildPageId(int slot) => _childPageIds[slot];
}
