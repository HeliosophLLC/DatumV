using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree;

/// <summary>
/// In-memory representation of a deserialized B+Tree internal (branch) page.
/// Contains separator keys and child page pointers. Each internal page has
/// <c>KeyCount + 1</c> child pointers — the subtree rooted at child <c>i</c>
/// contains all keys in the range [key[i−1], key[i]).
/// </summary>
internal sealed class BPlusTreeInternalPage
{
    private readonly DataValue[] _keys;
    private readonly uint[] _childPageIndexes;

    /// <summary>Zero-based page index within the B+Tree section.</summary>
    internal uint PageIndex { get; }

    /// <summary>Number of separator keys in this internal page.</summary>
    internal int KeyCount => _keys.Length;

    /// <summary>Number of child pointers (always <see cref="KeyCount"/> + 1).</summary>
    internal int ChildCount => _childPageIndexes.Length;

    /// <summary>The separator keys (for serialization and testing).</summary>
    internal ReadOnlySpan<DataValue> Keys => _keys;

    /// <summary>The child page indexes (for serialization and testing).</summary>
    internal ReadOnlySpan<uint> ChildPageIndexes => _childPageIndexes;

    /// <summary>
    /// Creates an internal page from separator keys and child pointers.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index within the section.</param>
    /// <param name="keys">Separator keys in ascending order.</param>
    /// <param name="childPageIndexes">
    /// Child page indexes. Must have exactly <c>keys.Length + 1</c> elements.
    /// </param>
    internal BPlusTreeInternalPage(
        uint pageIndex,
        DataValue[] keys,
        uint[] childPageIndexes)
    {
        PageIndex = pageIndex;
        _keys = keys;
        _childPageIndexes = childPageIndexes;
    }

    /// <summary>
    /// Returns the child page index to follow for the given search key.
    /// Uses binary search to find the first separator key strictly greater than
    /// <paramref name="key"/>, then returns the child pointer at that position.
    /// </summary>
    /// <remarks>
    /// For separator keys K₀, K₁, …, Kₘ₋₁ and children C₀, C₁, …, Cₘ:
    /// child Cᵢ covers all keys in [Kᵢ₋₁, Kᵢ). If the search key is ≥ all
    /// separator keys, the last child Cₘ is returned.
    /// </remarks>
    internal uint FindChildPageIndex(DataValue key)
    {
        int low = 0;
        int high = _keys.Length;

        while (low < high)
        {
            int mid = low + (high - low) / 2;

            if (CompareKeys(_keys[mid], key) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return _childPageIndexes[low];
    }

    /// <summary>
    /// Returns the separator key at the given index.
    /// </summary>
    internal DataValue GetKey(int index) => _keys[index];

    /// <summary>
    /// Returns the child page index at the given position.
    /// </summary>
    internal uint GetChildPageIndex(int index) => _childPageIndexes[index];

    private static int CompareKeys(DataValue left, DataValue right)
    {
        return StatisticsPredicateEvaluator.CompareValues(left, right);
    }
}
