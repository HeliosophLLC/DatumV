using System.IO.MemoryMappedFiles;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree;

/// <summary>
/// Reads and navigates a B+Tree stored as an array of raw 8 KiB pages.
/// Pages are decoded on demand via <see cref="BPlusTreePageCodec"/> and cached
/// in a <see cref="BPlusTreePageCache"/>. Provides point lookups, range scans,
/// and leaf chain traversal.
/// </summary>
/// <remarks>
/// The reader holds the raw (compressed) page byte arrays in memory. Pages are
/// decompressed on first access and cached. This avoids keeping the original
/// file stream open while still providing demand-paged decompression.
/// </remarks>
internal sealed class BPlusTreeReader
{
    private readonly byte[][]? _rawPages;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly long _pagesBaseOffset;
    private readonly BPlusTreeSectionHeader _header;
    private readonly BPlusTreePageCache _cache;

    /// <summary>Default number of decoded pages to keep in the LRU cache.</summary>
    private const int DefaultCacheCapacity = 128;

    /// <summary>Total number of entries across all leaves.</summary>
    internal long EntryCount => _header.EntryCount;

    /// <summary>Name of the indexed column.</summary>
    internal string ColumnName => _header.ColumnName;

    /// <summary>The section header describing this tree's shape.</summary>
    internal BPlusTreeSectionHeader Header => _header;

    /// <summary>
    /// The raw 8 KiB page byte arrays (for serialization). For memory-mapped trees,
    /// reads all pages from the accessor into newly allocated arrays.
    /// </summary>
    internal byte[][] RawPages
    {
        get
        {
            if (_rawPages is not null)
            {
                return _rawPages;
            }

            byte[][] pages = new byte[_header.PageCount][];

            for (uint pageIndex = 0; pageIndex < _header.PageCount; pageIndex++)
            {
                pages[pageIndex] = GetRawPageBytes(pageIndex);
            }

            return pages;
        }
    }

    /// <summary>
    /// Creates a reader over pre-loaded raw page data.
    /// </summary>
    /// <param name="header">The B+Tree section header describing the tree shape.</param>
    /// <param name="rawPages">
    /// Array of raw 8 KiB page byte arrays, indexed by page number within the section.
    /// </param>
    /// <param name="cacheCapacity">Maximum number of decoded pages to cache.</param>
    internal BPlusTreeReader(BPlusTreeSectionHeader header, byte[][] rawPages, int cacheCapacity = DefaultCacheCapacity)
    {
        _header = header;
        _rawPages = rawPages;
        _cache = new BPlusTreePageCache(cacheCapacity);
    }

    /// <summary>
    /// Creates a memory-mapped reader that reads raw page bytes from a
    /// <see cref="MemoryMappedViewAccessor"/> on demand instead of holding
    /// all pages in managed heap memory.
    /// </summary>
    /// <param name="header">The B+Tree section header describing the tree shape.</param>
    /// <param name="accessor">The shared view accessor spanning the index file.</param>
    /// <param name="pagesBaseOffset">Absolute byte offset of the first page in the file.</param>
    /// <param name="cacheCapacity">Maximum number of decoded pages to cache.</param>
    internal BPlusTreeReader(
        BPlusTreeSectionHeader header,
        MemoryMappedViewAccessor accessor,
        long pagesBaseOffset,
        int cacheCapacity = DefaultCacheCapacity)
    {
        _header = header;
        _accessor = accessor;
        _pagesBaseOffset = pagesBaseOffset;
        _cache = new BPlusTreePageCache(cacheCapacity);
    }

    /// <summary>
    /// Finds all entries whose key exactly matches <paramref name="key"/>.
    /// Navigates from the root to the target leaf, then scans forward through
    /// the leaf chain to collect all duplicates.
    /// </summary>
    internal IReadOnlyList<ValueIndexEntry> FindExact(DataValue key)
    {
        BPlusTreeLeafPage leaf = NavigateToLeaf(key);
        int position = leaf.BinarySearchFirst(key);

        if (position < 0)
        {
            return Array.Empty<ValueIndexEntry>();
        }

        List<ValueIndexEntry> results = new();

        // Scan forward through this leaf.
        CollectMatchingEntries(leaf, position, key, results);

        // Continue into subsequent leaves if duplicates span pages.
        while (leaf.NextLeafPageIndex != BPlusTreeConstants.NoLinkedPage)
        {
            leaf = LoadLeafPage(leaf.NextLeafPageIndex);

            if (leaf.EntryCount == 0 || CompareKeys(leaf.GetKey(0), key) != 0)
            {
                break;
            }

            CollectMatchingEntries(leaf, 0, key, results);
        }

        return results;
    }

    /// <summary>
    /// Returns all entries whose key falls within the inclusive range [<paramref name="low"/>, <paramref name="high"/>].
    /// Navigates to the leaf containing <paramref name="low"/> and scans forward through the leaf chain.
    /// </summary>
    internal IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high)
    {
        BPlusTreeLeafPage leaf = NavigateToLeaf(low);
        int startPosition = leaf.BinarySearchFirstGreaterOrEqual(low);

        List<ValueIndexEntry> results = new();

        // Scan from startPosition through remaining entries in this leaf.
        CollectRangeEntries(leaf, startPosition, high, results);

        // Continue into subsequent leaves.
        while (leaf.NextLeafPageIndex != BPlusTreeConstants.NoLinkedPage)
        {
            leaf = LoadLeafPage(leaf.NextLeafPageIndex);

            if (leaf.EntryCount == 0 || CompareKeys(leaf.GetKey(0), high) > 0)
            {
                break;
            }

            CollectRangeEntries(leaf, 0, high, results);
        }

        return results;
    }

    /// <summary>
    /// Enumerates all entries in ascending key order by walking the leaf chain
    /// from the first leaf to the last.
    /// </summary>
    internal IEnumerable<ValueIndexEntry> TraverseForward()
    {
        uint leafIndex = FindFirstLeafPageIndex();

        while (leafIndex != BPlusTreeConstants.NoLinkedPage)
        {
            BPlusTreeLeafPage leaf = LoadLeafPage(leafIndex);

            for (int index = 0; index < leaf.EntryCount; index++)
            {
                yield return leaf.GetEntry(index);
            }

            leafIndex = leaf.NextLeafPageIndex;
        }
    }

    /// <summary>
    /// Enumerates all entries in descending key order by walking the leaf chain
    /// backward from the last leaf to the first.
    /// </summary>
    internal IEnumerable<ValueIndexEntry> TraverseBackward()
    {
        uint leafIndex = FindLastLeafPageIndex();

        while (leafIndex != BPlusTreeConstants.NoLinkedPage)
        {
            BPlusTreeLeafPage leaf = LoadLeafPage(leafIndex);

            for (int index = leaf.EntryCount - 1; index >= 0; index--)
            {
                yield return leaf.GetEntry(index);
            }

            leafIndex = leaf.PreviousLeafPageIndex;
        }
    }

    /// <summary>
    /// Navigates from the root to the leaf page that would contain <paramref name="key"/>.
    /// </summary>
    private BPlusTreeLeafPage NavigateToLeaf(DataValue key)
    {
        uint currentPageIndex = _header.RootPageIndex;

        // Walk internal pages down to the leaf level.
        for (int level = 0; level < _header.TreeHeight - 1; level++)
        {
            BPlusTreeInternalPage internalPage = LoadInternalPage(currentPageIndex);
            currentPageIndex = internalPage.FindChildPageIndex(key);
        }

        return LoadLeafPage(currentPageIndex);
    }

    /// <summary>
    /// Finds the first (leftmost) leaf page by following the leftmost child
    /// pointers from the root.
    /// </summary>
    private uint FindFirstLeafPageIndex()
    {
        uint currentPageIndex = _header.RootPageIndex;

        for (int level = 0; level < _header.TreeHeight - 1; level++)
        {
            BPlusTreeInternalPage internalPage = LoadInternalPage(currentPageIndex);
            currentPageIndex = internalPage.GetChildPageIndex(0);
        }

        return currentPageIndex;
    }

    /// <summary>
    /// Finds the last (rightmost) leaf page by following the rightmost child
    /// pointers from the root.
    /// </summary>
    private uint FindLastLeafPageIndex()
    {
        uint currentPageIndex = _header.RootPageIndex;

        for (int level = 0; level < _header.TreeHeight - 1; level++)
        {
            BPlusTreeInternalPage internalPage = LoadInternalPage(currentPageIndex);
            currentPageIndex = internalPage.GetChildPageIndex(internalPage.ChildCount - 1);
        }

        return currentPageIndex;
    }

    /// <summary>
    /// Loads and decodes a leaf page, using the page cache to avoid repeated decompression.
    /// </summary>
    private BPlusTreeLeafPage LoadLeafPage(uint pageIndex)
    {
        if (_cache.TryGetLeafPage(pageIndex, out BPlusTreeLeafPage? cached) && cached is not null)
        {
            return cached;
        }

        BPlusTreeLeafPage page = BPlusTreePageCodec.DecodeLeafPage(GetRawPageBytes(pageIndex), pageIndex);
        _cache.AddLeafPage(pageIndex, page);
        return page;
    }

    /// <summary>
    /// Loads and decodes an internal page, using the page cache to avoid repeated deserialization.
    /// </summary>
    private BPlusTreeInternalPage LoadInternalPage(uint pageIndex)
    {
        if (_cache.TryGetInternalPage(pageIndex, out BPlusTreeInternalPage? cached) && cached is not null)
        {
            return cached;
        }

        BPlusTreeInternalPage page = BPlusTreePageCodec.DecodeInternalPage(GetRawPageBytes(pageIndex), pageIndex);
        _cache.AddInternalPage(pageIndex, page);
        return page;
    }

    /// <summary>
    /// Returns the raw 8 KiB bytes for the given page index, either from
    /// the in-memory array or by reading from the memory-mapped accessor.
    /// </summary>
    private byte[] GetRawPageBytes(uint pageIndex)
    {
        if (_rawPages is not null)
        {
            return _rawPages[pageIndex];
        }

        byte[] buffer = new byte[BPlusTreeConstants.PageSize];
        _accessor!.ReadArray(
            _pagesBaseOffset + pageIndex * BPlusTreeConstants.PageSize,
            buffer.AsSpan());
        return buffer;
    }

    /// <summary>
    /// Collects entries whose key matches <paramref name="key"/> starting at <paramref name="startIndex"/>
    /// within a single leaf page.
    /// </summary>
    private static void CollectMatchingEntries(
        BPlusTreeLeafPage leaf,
        int startIndex,
        DataValue key,
        List<ValueIndexEntry> results)
    {
        for (int index = startIndex; index < leaf.EntryCount; index++)
        {
            if (CompareKeys(leaf.GetKey(index), key) != 0)
            {
                break;
            }

            results.Add(leaf.GetEntry(index));
        }
    }

    /// <summary>
    /// Collects entries whose key is ≤ <paramref name="high"/> starting at <paramref name="startIndex"/>
    /// within a single leaf page.
    /// </summary>
    private static void CollectRangeEntries(
        BPlusTreeLeafPage leaf,
        int startIndex,
        DataValue high,
        List<ValueIndexEntry> results)
    {
        for (int index = startIndex; index < leaf.EntryCount; index++)
        {
            if (CompareKeys(leaf.GetKey(index), high) > 0)
            {
                break;
            }

            results.Add(leaf.GetEntry(index));
        }
    }

    private static int CompareKeys(DataValue left, DataValue right)
    {
        return StatisticsPredicateEvaluator.CompareValues(left, right);
    }
}
