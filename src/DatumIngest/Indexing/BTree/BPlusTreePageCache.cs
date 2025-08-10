namespace DatumIngest.Indexing.BTree;

/// <summary>
/// LRU cache for deserialized B+Tree pages. Stores decoded <see cref="BPlusTreeLeafPage"/>
/// and <see cref="BPlusTreeInternalPage"/> instances keyed by page index, evicting the
/// least recently used entry when the capacity is exceeded.
/// </summary>
/// <remarks>
/// The cache is bounded by entry count, not byte size. Each cache hit avoids
/// Zstd decompression for leaf pages (~2–5 µs per page at 8 KiB).
/// Internal pages are also cached since they are traversed on every lookup.
/// </remarks>
internal sealed class BPlusTreePageCache
{
    private readonly int _capacity;
    private readonly Dictionary<uint, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _evictionOrder;

    /// <summary>
    /// Creates a page cache with the specified maximum number of decoded pages.
    /// </summary>
    /// <param name="capacity">Maximum number of pages to cache. Must be at least 1.</param>
    internal BPlusTreePageCache(int capacity)
    {
        _capacity = capacity;
        _map = new Dictionary<uint, LinkedListNode<CacheEntry>>(capacity);
        _evictionOrder = new LinkedList<CacheEntry>();
    }

    /// <summary>
    /// Attempts to retrieve a cached leaf page by page index.
    /// </summary>
    /// <param name="pageIndex">The page index to look up.</param>
    /// <param name="page">The cached leaf page, if found.</param>
    /// <returns><c>true</c> if the page was found in the cache; otherwise <c>false</c>.</returns>
    internal bool TryGetLeafPage(uint pageIndex, out BPlusTreeLeafPage? page)
    {
        if (_map.TryGetValue(pageIndex, out LinkedListNode<CacheEntry>? node) &&
            node.Value.LeafPage is not null)
        {
            PromoteToFront(node);
            page = node.Value.LeafPage;
            return true;
        }

        page = null;
        return false;
    }

    /// <summary>
    /// Attempts to retrieve a cached internal page by page index.
    /// </summary>
    /// <param name="pageIndex">The page index to look up.</param>
    /// <param name="page">The cached internal page, if found.</param>
    /// <returns><c>true</c> if the page was found in the cache; otherwise <c>false</c>.</returns>
    internal bool TryGetInternalPage(uint pageIndex, out BPlusTreeInternalPage? page)
    {
        if (_map.TryGetValue(pageIndex, out LinkedListNode<CacheEntry>? node) &&
            node.Value.InternalPage is not null)
        {
            PromoteToFront(node);
            page = node.Value.InternalPage;
            return true;
        }

        page = null;
        return false;
    }

    /// <summary>
    /// Inserts a decoded leaf page into the cache, evicting the least recently used
    /// entry if the cache is at capacity.
    /// </summary>
    internal void AddLeafPage(uint pageIndex, BPlusTreeLeafPage page)
    {
        if (_map.ContainsKey(pageIndex))
        {
            return;
        }

        EvictIfNecessary();

        CacheEntry entry = new(pageIndex, LeafPage: page, InternalPage: null);
        LinkedListNode<CacheEntry> node = _evictionOrder.AddFirst(entry);
        _map[pageIndex] = node;
    }

    /// <summary>
    /// Inserts a decoded internal page into the cache, evicting the least recently used
    /// entry if the cache is at capacity.
    /// </summary>
    internal void AddInternalPage(uint pageIndex, BPlusTreeInternalPage page)
    {
        if (_map.ContainsKey(pageIndex))
        {
            return;
        }

        EvictIfNecessary();

        CacheEntry entry = new(pageIndex, LeafPage: null, InternalPage: page);
        LinkedListNode<CacheEntry> node = _evictionOrder.AddFirst(entry);
        _map[pageIndex] = node;
    }

    /// <summary>
    /// Returns the number of pages currently cached.
    /// </summary>
    internal int Count => _map.Count;

    private void PromoteToFront(LinkedListNode<CacheEntry> node)
    {
        _evictionOrder.Remove(node);
        _evictionOrder.AddFirst(node);
    }

    private void EvictIfNecessary()
    {
        while (_map.Count >= _capacity && _evictionOrder.Last is not null)
        {
            LinkedListNode<CacheEntry> victim = _evictionOrder.Last;
            _evictionOrder.RemoveLast();
            _map.Remove(victim.Value.PageIndex);
        }
    }

    /// <summary>
    /// A single cache entry holding either a leaf or internal page.
    /// </summary>
    private readonly record struct CacheEntry(
        uint PageIndex,
        BPlusTreeLeafPage? LeafPage,
        BPlusTreeInternalPage? InternalPage);
}
