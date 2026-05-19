using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing.BTree.Mutable;

/// <summary>
/// In-memory representation of a mutable B+Tree internal (branch) page.
/// Each page has <c>SeparatorCount + 1</c> child pointers: child i covers all
/// entries whose composite (Key, ChunkIndex, RowOffsetInChunk) falls in
/// [<c>Separators[i-1]</c>, <c>Separators[i]</c>).
/// </summary>
/// <remarks>
/// Separators are full composite keys (<see cref="ValueIndexEntry"/>) rather
/// than bare <see cref="DataValue"/>s. Key-only separators can't disambiguate
/// duplicate-key inserts: when key K spans two leaves, a new (K, c, r) entry
/// must route by the composite, not by K alone, or the global composite-sort
/// invariant breaks.
/// </remarks>
internal sealed class MutableInternalPage
{
    private readonly ValueIndexEntry[] _separators;
    private readonly uint[] _childPageIds;

    internal uint PageId { get; }

    internal int SeparatorCount => _separators.Length;

    /// <summary>
    /// Alias for <see cref="SeparatorCount"/> kept for readability where the
    /// page is being treated as a "keys" page.
    /// </summary>
    internal int KeyCount => _separators.Length;

    internal int ChildCount => _childPageIds.Length;

    internal ReadOnlySpan<ValueIndexEntry> Separators => _separators;

    internal ReadOnlySpan<uint> ChildPageIds => _childPageIds;

    internal MutableInternalPage(uint pageId, ValueIndexEntry[] separators, uint[] childPageIds)
    {
        if (childPageIds.Length != separators.Length + 1)
        {
            throw new ArgumentException(
                $"Internal page child count ({childPageIds.Length}) must equal separator count + 1 ({separators.Length + 1}).",
                nameof(childPageIds));
        }

        PageId = pageId;
        _separators = separators;
        _childPageIds = childPageIds;
    }

    /// <summary>
    /// Returns the slot index <c>i</c> into the child-page array such that the
    /// subtree rooted at child <c>i</c> may contain <paramref name="composite"/>.
    /// Equivalently, the number of separators that are &lt;= <paramref name="composite"/>
    /// in composite order.
    /// </summary>
    internal int FindChildSlot(ValueIndexEntry composite)
    {
        int low = 0;
        int high = _separators.Length;

        while (low < high)
        {
            int mid = low + ((high - low) / 2);

            if (CompareComposite(_separators[mid], composite) <= 0)
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

    /// <summary>
    /// Composite lex compare on (Key, ChunkIndex, RowOffsetInChunk).
    /// </summary>
    internal static int CompareComposite(ValueIndexEntry a, ValueIndexEntry b)
    {
        int cmp = StatisticsPredicateEvaluator.CompareValues(a.Key, b.Key);
        if (cmp != 0) return cmp;

        cmp = a.ChunkIndex.CompareTo(b.ChunkIndex);
        if (cmp != 0) return cmp;

        return a.RowOffsetInChunk.CompareTo(b.RowOffsetInChunk);
    }

    /// <summary>
    /// Synthesizes a composite that sorts strictly less than any real
    /// <c>(key, chunk, row)</c> entry. Used by range-scan descent
    /// (<c>FindRange</c>) which lands on the leftmost candidate leaf and
    /// then steps forward — landing one leaf too far left is benign.
    /// </summary>
    internal static ValueIndexEntry MinCompositeForKey(DataValue key) =>
        new(key, ChunkIndex: int.MinValue, RowOffsetInChunk: long.MinValue);

    /// <summary>
    /// Synthesizes a composite that sorts strictly greater than any real
    /// <c>(key, chunk, row)</c> entry. Used by point-lookup descent
    /// (<c>TryFind</c>) which must land in the leaf actually holding the
    /// entry — the rightmost leaf whose left-boundary key ≤ <paramref name="key"/>.
    /// A min-composite probe would land one leaf to the left, where the
    /// entry isn't, and the point lookup would spuriously miss.
    /// </summary>
    internal static ValueIndexEntry MaxCompositeForKey(DataValue key) =>
        new(key, ChunkIndex: int.MaxValue, RowOffsetInChunk: long.MaxValue);

    /// <summary>Returns the child page id at the given slot (0-based).</summary>
    internal uint GetChildPageId(int slot) => _childPageIds[slot];
}
