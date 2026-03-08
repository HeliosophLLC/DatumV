using DatumIngest.Indexing.BTree.Mutable;

namespace DatumIngest.Indexing.BTree.MutableBytes;

/// <summary>
/// A crash-safe, single-writer, append-mostly B+Tree backed by a dedicated
/// file. Pages are 8 KiB; commits use a dual-slot header with CRC32 on each
/// slot. Sibling to <see cref="MutableBPlusTree"/> — same architecture, but
/// keys are opaque <c>byte[]</c> sequences (no <c>DataValue</c> wrapping),
/// compared via <see cref="MemoryExtensions.SequenceCompareTo{T}(System.ReadOnlySpan{T}, System.ReadOnlySpan{T})"/>.
/// </summary>
/// <remarks>
/// <para>Key shape is the caller's concern — typically the output of
/// <see cref="DatumIngest.Indexing.CompositeKeyEncoder"/> which converts
/// a tuple of <c>DataValue</c>s into a memcmp-orderable byte string.
/// The tree itself knows nothing about column kinds.</para>
///
/// <para><b>Allocation:</b> bump-only. The header reserves a free-list head
/// field, but recycling is deferred to a follow-up (matches the typed tree).</para>
///
/// <para><b>COW commit:</b> every insert allocates fresh page ids for the
/// entire root-to-leaf path. Old pages stay on disk; the active slot still
/// references the previous tree if a crash interrupts mid-write.</para>
///
/// <para><b>Single-writer model:</b> instances are not thread-safe. Callers
/// hold a mutation lock that serializes access.</para>
/// </remarks>
internal sealed class MutableBPlusTreeBytes : IDisposable
{
    private readonly FileStream _file;
    private readonly PageGeometry _geometry;
    private MutableBPlusTreeBytesHeader _activeHeader;
    private int _activeSlotIndex;

    private MutableBPlusTreeBytes(FileStream file, MutableBPlusTreeBytesHeader activeHeader, int activeSlotIndex)
    {
        _file = file;
        _activeHeader = activeHeader;
        _activeSlotIndex = activeSlotIndex;
        _geometry = new PageGeometry(activeHeader.PageSize);
        _geometry.Validate();
    }

    /// <summary>Total number of entries currently in the tree.</summary>
    internal long EntryCount => _activeHeader.EntryCount;

    /// <summary>Tree height: 0 for empty, 1 if root is a leaf, etc.</summary>
    internal int TreeHeight => _activeHeader.TreeHeight;

    /// <summary>Total number of pages allocated (including leaked COW pages).</summary>
    internal uint PageCount => _activeHeader.PageCount;

    /// <summary>The latest commit generation reflected by the active header.</summary>
    internal long CommitGen => _activeHeader.CommitGen;

    /// <summary>
    /// Whether the tree allows duplicate keys. When false, <see cref="Insert"/>
    /// throws <see cref="DuplicateKeyException"/> on an existing key.
    /// </summary>
    internal bool AllowDuplicates => _activeHeader.AllowDuplicates;

    /// <summary>
    /// Creates a new bytes-keyed B+Tree file. Both header slots are initialized
    /// to an empty tree with consecutive commit-gens so reader open is
    /// deterministic regardless of which slot is sampled first.
    /// </summary>
    /// <param name="path">Target file path.</param>
    /// <param name="allowDuplicates">
    /// <see langword="true"/> for multi-value acceleration trees; <see langword="false"/>
    /// for PK uniqueness (Insert throws on duplicate key).
    /// </param>
    /// <param name="pageSize">
    /// Page size in bytes for this tree. Defaults to 8 KiB. Persisted in the header
    /// so reopens use the same geometry; contract tests can pick smaller values
    /// (e.g. 512) to exercise split logic at legible workloads.
    /// </param>
    /// <exception cref="IOException">If the file already exists.</exception>
    internal static MutableBPlusTreeBytes Create(string path, bool allowDuplicates = false, int pageSize = 8192)
    {
        PageGeometry geometry = new(pageSize);
        geometry.Validate();

        FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            MutableBPlusTreeBytesHeader emptyA = MutableBPlusTreeBytesHeader.Empty(commitGen: 0, allowDuplicates: allowDuplicates, pageSize: pageSize);
            MutableBPlusTreeBytesHeader emptyB = MutableBPlusTreeBytesHeader.Empty(commitGen: 1, allowDuplicates: allowDuplicates, pageSize: pageSize);

            byte[] slot = new byte[MutableBPlusTreeConstants.HeaderSlotSize];

            emptyA.WriteTo(slot);
            file.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
            file.Write(slot);

            emptyB.WriteTo(slot);
            file.Position = MutableBPlusTreeConstants.HeaderSlotBOffset;
            file.Write(slot);

            file.Flush(flushToDisk: true);

            // Slot B has the higher gen so it's "active" right after Create.
            return new MutableBPlusTreeBytes(file, emptyB, activeSlotIndex: 1);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing bytes-keyed B+Tree file. Picks the slot whose CRC
    /// validates and whose commit-gen is higher; torn writes leave the
    /// previous slot intact.
    /// </summary>
    /// <exception cref="InvalidDataException">If neither header slot validates.</exception>
    internal static MutableBPlusTreeBytes Open(string path)
    {
        FileStream file = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            byte[] slot = new byte[MutableBPlusTreeConstants.HeaderSlotSize];

            file.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
            file.ReadExactly(slot);
            bool aValid = MutableBPlusTreeBytesHeader.TryReadFrom(slot, out MutableBPlusTreeBytesHeader headerA);

            file.Position = MutableBPlusTreeConstants.HeaderSlotBOffset;
            file.ReadExactly(slot);
            bool bValid = MutableBPlusTreeBytesHeader.TryReadFrom(slot, out MutableBPlusTreeBytesHeader headerB);

            if (!aValid && !bValid)
            {
                throw new InvalidDataException(
                    $"Both header slots in '{path}' are invalid. The file is corrupt or not a bytes-keyed B+Tree.");
            }

            int activeSlot;
            MutableBPlusTreeBytesHeader active;

            if (aValid && bValid)
            {
                if (headerA.CommitGen >= headerB.CommitGen)
                {
                    active = headerA;
                    activeSlot = 0;
                }
                else
                {
                    active = headerB;
                    activeSlot = 1;
                }
            }
            else if (aValid)
            {
                active = headerA;
                activeSlot = 0;
            }
            else
            {
                active = headerB;
                activeSlot = 1;
            }

            return new MutableBPlusTreeBytes(file, active, activeSlot);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Inserts an entry. When the tree was created with <c>allowDuplicates: false</c>
    /// (PK mode), throws <see cref="DuplicateKeyException"/> if the key already
    /// exists. When <c>allowDuplicates: true</c> (acceleration mode), entries
    /// with the same key are sorted by composite (Key, ChunkIndex, RowOffsetInChunk)
    /// and Insert never throws on equal keys.
    /// </summary>
    internal void Insert(BytesIndexEntry entry)
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            InsertIntoEmptyTree(entry);
            return;
        }

        // Walk root → leaf, recording the path so we can COW-rewrite up.
        List<PathStep> path = new(_activeHeader.TreeHeight);
        uint pageId = _activeHeader.RootPageId;

        for (int level = 0; level < _activeHeader.TreeHeight - 1; level++)
        {
            MutableBytesInternalPage internalPage = ReadInternalPage(pageId);
            int slot = internalPage.FindChildSlot(entry.Key);
            path.Add(new PathStep(internalPage, slot));
            pageId = internalPage.GetChildPageId(slot);
        }

        MutableBytesLeafPage leaf = ReadLeafPage(pageId);

        if (!_activeHeader.AllowDuplicates && leaf.BinarySearchFirst(entry.Key) >= 0)
        {
            throw new DuplicateKeyException(entry.Key);
        }

        InsertIntoLeafAndPropagate(leaf, entry, path);
    }

    /// <summary>
    /// Returns <see langword="true"/> and the matching entry if
    /// <paramref name="key"/> exists in the tree.
    /// </summary>
    internal bool TryFind(ReadOnlySpan<byte> key, out BytesIndexEntry entry)
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            entry = default;
            return false;
        }

        uint pageId = _activeHeader.RootPageId;

        for (int level = 0; level < _activeHeader.TreeHeight - 1; level++)
        {
            MutableBytesInternalPage internalPage = ReadInternalPage(pageId);
            int slot = internalPage.FindChildSlot(key);
            pageId = internalPage.GetChildPageId(slot);
        }

        MutableBytesLeafPage leaf = ReadLeafPage(pageId);
        int index = leaf.BinarySearchFirst(key);

        if (index < 0)
        {
            entry = default;
            return false;
        }

        entry = leaf.Entries[index];
        return true;
    }

    /// <summary>
    /// Returns all entries whose key falls in the inclusive range
    /// [<paramref name="low"/>, <paramref name="high"/>]. Walks via internal-page
    /// navigation (no leaf-chain pointers — those become stale under page-COW
    /// splits; the standard COW B+Tree approach is parent-page navigation).
    /// </summary>
    internal IReadOnlyList<BytesIndexEntry> FindRange(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high)
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            return Array.Empty<BytesIndexEntry>();
        }

        // ROS arguments can't be captured by the descent helpers; copy once.
        byte[] lowKey = low.ToArray();
        byte[] highKey = high.ToArray();

        List<BytesIndexEntry> results = new();
        DescentPath path = DescendForKey(lowKey);

        // FindChildSlot uses ≤ semantics — for `low` matching a separator, we land on
        // the right child. The previous leaf might still contain entries with key=low.
        while (true)
        {
            DescentPath? prevPath = path.TryStepLeft();
            if (prevPath is null) break;
            MutableBytesLeafPage prevLeaf = ReadLeafPage(prevPath.Value.LeafPageId);
            if (prevLeaf.EntryCount == 0)
            {
                path = prevPath.Value;
                continue;
            }
            if (((ReadOnlySpan<byte>)prevLeaf.Entries[prevLeaf.EntryCount - 1].Key)
                    .SequenceCompareTo(lowKey) < 0)
            {
                break;
            }
            path = prevPath.Value;
        }

        // Scan forward.
        while (true)
        {
            MutableBytesLeafPage leaf = ReadLeafPage(path.LeafPageId);

            for (int i = 0; i < leaf.EntryCount; i++)
            {
                BytesIndexEntry e = leaf.Entries[i];
                ReadOnlySpan<byte> keySpan = e.Key;
                if (keySpan.SequenceCompareTo(highKey) > 0) return results;

                if (keySpan.SequenceCompareTo(lowKey) >= 0)
                {
                    results.Add(e);
                }
            }

            DescentPath? nextPath = path.TryStepRight();
            if (nextPath is null) break;
            path = nextPath.Value;
        }

        return results;
    }

    /// <summary>
    /// Returns all entries whose key equals <paramref name="key"/>. In
    /// acceleration (multi-value) mode, multiple entries can share a key —
    /// they may even straddle adjacent leaves after a split.
    /// <see cref="FindRange"/> handles both cases.
    /// </summary>
    internal IReadOnlyList<BytesIndexEntry> FindAll(ReadOnlySpan<byte> key) => FindRange(key, key);

    /// <summary>
    /// Returns all entries whose key starts with <paramref name="prefix"/>.
    /// Used by the planner to satisfy leftmost-prefix composite-index queries
    /// — <c>WHERE a = X</c> on a <c>(a, b)</c> index encodes just the <c>a</c>
    /// component as the prefix and collects every key whose first bytes match.
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>FindRange(prefix, lex_next(prefix))</c> with an
    /// exclusive upper bound, but skips the work of computing the lex-next
    /// sequence — we just walk forward and check <c>StartsWith</c> on each
    /// entry. The byte-level <c>SequenceCompareTo</c> ordering and our
    /// length-prefixed component encoding guarantee that, once we hit an
    /// entry whose key doesn't start with the prefix, every subsequent
    /// entry will also not start with it. Empty prefix returns every entry.
    /// </remarks>
    internal IReadOnlyList<BytesIndexEntry> FindPrefix(ReadOnlySpan<byte> prefix)
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            return Array.Empty<BytesIndexEntry>();
        }

        // Empty prefix is the trivial "everything" case; let TraverseForward
        // handle it for free instead of running the descent + match loop.
        if (prefix.Length == 0)
        {
            List<BytesIndexEntry> all = new();
            foreach (BytesIndexEntry e in TraverseForward()) all.Add(e);
            return all;
        }

        byte[] prefixCopy = prefix.ToArray();
        List<BytesIndexEntry> results = new();
        DescentPath path = DescendForKey(prefixCopy);

        // FindChildSlot uses ≤ semantics — for `prefix` matching a separator
        // we land on the right child, but the previous leaf might still
        // contain entries whose key starts with `prefix` (same start-walk as
        // FindRange).
        while (true)
        {
            DescentPath? prevPath = path.TryStepLeft();
            if (prevPath is null) break;
            MutableBytesLeafPage prevLeaf = ReadLeafPage(prevPath.Value.LeafPageId);
            if (prevLeaf.EntryCount == 0)
            {
                path = prevPath.Value;
                continue;
            }
            if (((ReadOnlySpan<byte>)prevLeaf.Entries[prevLeaf.EntryCount - 1].Key)
                    .SequenceCompareTo(prefixCopy) < 0)
            {
                break;
            }
            path = prevPath.Value;
        }

        // Scan forward. Once we see an entry whose key sorts > prefix and
        // doesn't start with it, all subsequent entries are also greater —
        // stop early.
        while (true)
        {
            MutableBytesLeafPage leaf = ReadLeafPage(path.LeafPageId);

            for (int i = 0; i < leaf.EntryCount; i++)
            {
                BytesIndexEntry e = leaf.Entries[i];
                ReadOnlySpan<byte> keySpan = e.Key;

                if (keySpan.StartsWith(prefixCopy))
                {
                    results.Add(e);
                    continue;
                }

                // Either keySpan < prefix (we're still walking up) or
                // keySpan > prefix without sharing it (we've passed the
                // prefix range). Distinguish via SequenceCompareTo.
                if (keySpan.SequenceCompareTo(prefixCopy) > 0)
                {
                    return results;
                }
                // else: keySpan < prefix — keep scanning.
            }

            DescentPath? nextPath = path.TryStepRight();
            if (nextPath is null) break;
            path = nextPath.Value;
        }

        return results;
    }

    /// <summary>
    /// Enumerates all entries in ascending key order via leftmost-leaf descent
    /// and then internal-navigation forward stepping.
    /// </summary>
    internal IEnumerable<BytesIndexEntry> TraverseForward()
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            yield break;
        }

        DescentPath? cursor = DescendLeftmost();
        while (cursor is not null)
        {
            MutableBytesLeafPage leaf = ReadLeafPage(cursor.Value.LeafPageId);
            for (int i = 0; i < leaf.EntryCount; i++)
            {
                yield return leaf.Entries[i];
            }
            cursor = cursor.Value.TryStepRight();
        }
    }

    /// <summary>
    /// Enumerates all entries in descending key order via rightmost-leaf
    /// descent and internal-navigation backward stepping.
    /// </summary>
    internal IEnumerable<BytesIndexEntry> TraverseBackward()
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            yield break;
        }

        DescentPath? cursor = DescendRightmost();
        while (cursor is not null)
        {
            MutableBytesLeafPage leaf = ReadLeafPage(cursor.Value.LeafPageId);
            for (int i = leaf.EntryCount - 1; i >= 0; i--)
            {
                yield return leaf.Entries[i];
            }
            cursor = cursor.Value.TryStepLeft();
        }
    }

    /// <summary>
    /// Removes the entry matching <paramref name="entry"/> by composite
    /// (Key, ChunkIndex, RowOffsetInChunk). Returns <see langword="true"/> if
    /// an entry was found and removed, <see langword="false"/> otherwise.
    /// Lazy deletion: the leaf may end up under-full or empty; rebalancing /
    /// merging is deferred. An empty single-leaf root collapses to height-0
    /// (no root); empty leaves under a multi-level tree are tolerated and
    /// skipped on traversal.
    /// </summary>
    internal bool Delete(BytesIndexEntry entry)
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            return false;
        }

        List<PathStep> path = new(_activeHeader.TreeHeight);
        uint pageId = _activeHeader.RootPageId;

        for (int level = 0; level < _activeHeader.TreeHeight - 1; level++)
        {
            MutableBytesInternalPage internalPage = ReadInternalPage(pageId);
            int slot = internalPage.FindChildSlot(entry.Key);
            path.Add(new PathStep(internalPage, slot));
            pageId = internalPage.GetChildPageId(slot);
        }

        MutableBytesLeafPage leaf = ReadLeafPage(pageId);
        int idx = FindEntryIndex(leaf, entry);

        if (idx < 0)
        {
            // With multi-value duplicates spanning leaves, the entry could live
            // in the previous leaf. Try one step left.
            DescentPath descent = new(this, path, pageId);
            DescentPath? prev = descent.TryStepLeft();
            if (prev is not null)
            {
                MutableBytesLeafPage prevLeaf = ReadLeafPage(prev.Value.LeafPageId);
                int prevIdx = FindEntryIndex(prevLeaf, entry);
                if (prevIdx >= 0)
                {
                    return DeleteFromLeafAndPropagate(prevLeaf, prevIdx, prev.Value.PathSteps);
                }
            }
            return false;
        }

        return DeleteFromLeafAndPropagate(leaf, idx, path);
    }

    private bool DeleteFromLeafAndPropagate(MutableBytesLeafPage leaf, int entryIndex, IReadOnlyList<PathStep> path)
    {
        BytesIndexEntry[] newEntries = new BytesIndexEntry[leaf.EntryCount - 1];
        leaf.Entries[..entryIndex].CopyTo(newEntries.AsSpan(0, entryIndex));
        leaf.Entries[(entryIndex + 1)..].CopyTo(newEntries.AsSpan(entryIndex));

        uint newPageCount = _activeHeader.PageCount;

        if (newEntries.Length == 0 && _activeHeader.TreeHeight == 1)
        {
            CommitNewHeader(
                rootPageId: MutableBPlusTreeConstants.NoLinkedPage,
                pageCount: newPageCount,
                treeHeight: 0,
                entryCount: 0);
            return true;
        }

        uint newLeafId = AllocatePage(ref newPageCount);
        byte[] leafBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(
            _geometry, newEntries, leaf.PreviousLeafPageId, leaf.NextLeafPageId);
        WritePage(newLeafId, leafBytes);

        List<PathStep> mutablePath = new(path);
        uint newRootId = PropagateChildReplacement(mutablePath, newLeafId, ref newPageCount);

        CommitNewHeader(
            rootPageId: newRootId,
            pageCount: newPageCount,
            treeHeight: _activeHeader.TreeHeight,
            entryCount: _activeHeader.EntryCount - 1);
        return true;
    }

    private static int FindEntryIndex(MutableBytesLeafPage leaf, BytesIndexEntry target)
    {
        int firstKeyMatch = leaf.BinarySearchFirst(target.Key);
        if (firstKeyMatch < 0) return -1;

        ReadOnlySpan<byte> targetKey = target.Key;
        for (int i = firstKeyMatch; i < leaf.EntryCount; i++)
        {
            BytesIndexEntry e = leaf.Entries[i];
            if (((ReadOnlySpan<byte>)e.Key).SequenceCompareTo(targetKey) != 0) break;
            if (e.ChunkIndex == target.ChunkIndex && e.RowOffsetInChunk == target.RowOffsetInChunk)
            {
                return i;
            }
        }
        return -1;
    }

    public void Dispose()
    {
        _file.Dispose();
    }

    // ───── Descent / navigation helpers ─────

    /// <summary>
    /// Descent state: the path from root to a leaf, plus the leaf's page id.
    /// Supports stepping left or right via internal-page navigation.
    /// </summary>
    private readonly struct DescentPath
    {
        internal readonly MutableBPlusTreeBytes Tree;
        internal readonly List<PathStep> PathSteps;
        internal readonly uint LeafPageId;

        internal DescentPath(MutableBPlusTreeBytes tree, List<PathStep> pathSteps, uint leafPageId)
        {
            Tree = tree;
            PathSteps = pathSteps;
            LeafPageId = leafPageId;
        }

        internal DescentPath? TryStepRight()
        {
            int level = PathSteps.Count - 1;
            while (level >= 0)
            {
                PathStep step = PathSteps[level];
                if (step.SlotIndex + 1 < step.Page.ChildCount)
                {
                    List<PathStep> newPath = new(PathSteps.Count);
                    for (int i = 0; i < level; i++) newPath.Add(PathSteps[i]);
                    newPath.Add(new PathStep(step.Page, step.SlotIndex + 1));
                    uint childId = step.Page.GetChildPageId(step.SlotIndex + 1);
                    return Tree.DescendLeftmostFrom(childId, newPath);
                }
                level--;
            }
            return null;
        }

        internal DescentPath? TryStepLeft()
        {
            int level = PathSteps.Count - 1;
            while (level >= 0)
            {
                PathStep step = PathSteps[level];
                if (step.SlotIndex > 0)
                {
                    List<PathStep> newPath = new(PathSteps.Count);
                    for (int i = 0; i < level; i++) newPath.Add(PathSteps[i]);
                    newPath.Add(new PathStep(step.Page, step.SlotIndex - 1));
                    uint childId = step.Page.GetChildPageId(step.SlotIndex - 1);
                    return Tree.DescendRightmostFrom(childId, newPath);
                }
                level--;
            }
            return null;
        }
    }

    private DescentPath DescendForKey(ReadOnlySpan<byte> key)
    {
        List<PathStep> path = new(_activeHeader.TreeHeight);
        uint pageId = _activeHeader.RootPageId;

        for (int level = 0; level < _activeHeader.TreeHeight - 1; level++)
        {
            MutableBytesInternalPage internalPage = ReadInternalPage(pageId);
            int slot = internalPage.FindChildSlot(key);
            path.Add(new PathStep(internalPage, slot));
            pageId = internalPage.GetChildPageId(slot);
        }

        return new DescentPath(this, path, pageId);
    }

    private DescentPath DescendLeftmost() =>
        DescendLeftmostFrom(_activeHeader.RootPageId, new List<PathStep>(_activeHeader.TreeHeight));

    private DescentPath DescendRightmost() =>
        DescendRightmostFrom(_activeHeader.RootPageId, new List<PathStep>(_activeHeader.TreeHeight));

    private DescentPath DescendLeftmostFrom(uint subtreeRoot, List<PathStep> pathPrefix)
    {
        int remainingInternalLevels = _activeHeader.TreeHeight - 1 - pathPrefix.Count;
        uint pageId = subtreeRoot;

        for (int i = 0; i < remainingInternalLevels; i++)
        {
            MutableBytesInternalPage internalPage = ReadInternalPage(pageId);
            pathPrefix.Add(new PathStep(internalPage, 0));
            pageId = internalPage.GetChildPageId(0);
        }

        return new DescentPath(this, pathPrefix, pageId);
    }

    private DescentPath DescendRightmostFrom(uint subtreeRoot, List<PathStep> pathPrefix)
    {
        int remainingInternalLevels = _activeHeader.TreeHeight - 1 - pathPrefix.Count;
        uint pageId = subtreeRoot;

        for (int i = 0; i < remainingInternalLevels; i++)
        {
            MutableBytesInternalPage internalPage = ReadInternalPage(pageId);
            int slot = internalPage.ChildCount - 1;
            pathPrefix.Add(new PathStep(internalPage, slot));
            pageId = internalPage.GetChildPageId(slot);
        }

        return new DescentPath(this, pathPrefix, pageId);
    }

    // ───── Insert internals ─────

    private void InsertIntoEmptyTree(BytesIndexEntry entry)
    {
        BytesIndexEntry[] entries = new[] { entry };
        byte[] leafBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(
            _geometry,
            entries,
            previousLeafPageId: MutableBPlusTreeConstants.NoLinkedPage,
            nextLeafPageId: MutableBPlusTreeConstants.NoLinkedPage);

        uint newPageCount = _activeHeader.PageCount;
        uint rootId = AllocatePage(ref newPageCount);
        WritePage(rootId, leafBytes);
        _file.Flush(flushToDisk: true);

        CommitNewHeader(
            rootPageId: rootId,
            pageCount: newPageCount,
            treeHeight: 1,
            entryCount: 1);
    }

    private void InsertIntoLeafAndPropagate(MutableBytesLeafPage leaf, BytesIndexEntry entry, List<PathStep> path)
    {
        int insertPos = leaf.BinarySearchInsertPosition(entry);
        BytesIndexEntry[] merged = new BytesIndexEntry[leaf.EntryCount + 1];
        leaf.Entries[..insertPos].CopyTo(merged.AsSpan(0, insertPos));
        merged[insertPos] = entry;
        leaf.Entries[insertPos..].CopyTo(merged.AsSpan(insertPos + 1));

        uint newPageCount = _activeHeader.PageCount;
        ushort newTreeHeight = _activeHeader.TreeHeight;

        int payloadBytes = MutableBPlusTreeBytesPageCodec.MeasureLeafEntries(merged);

        if (payloadBytes <= _geometry.LeafPayloadCapacity)
        {
            uint newLeafId = AllocatePage(ref newPageCount);
            byte[] leafBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(
                _geometry, merged, leaf.PreviousLeafPageId, leaf.NextLeafPageId);
            WritePage(newLeafId, leafBytes);

            uint newRootId = PropagateChildReplacement(path, newLeafId, ref newPageCount);

            CommitNewHeader(
                rootPageId: newRootId,
                pageCount: newPageCount,
                treeHeight: newTreeHeight,
                entryCount: _activeHeader.EntryCount + 1);
            return;
        }

        // Leaf split.
        int splitIndex = FindLeafSplitIndex(merged);
        BytesIndexEntry[] leftEntries = merged[..splitIndex];
        BytesIndexEntry[] rightEntries = merged[splitIndex..];

        uint leftId = AllocatePage(ref newPageCount);
        uint rightId = AllocatePage(ref newPageCount);

        byte[] leftBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(_geometry, leftEntries, leaf.PreviousLeafPageId, rightId);
        byte[] rightBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(_geometry, rightEntries, leftId, leaf.NextLeafPageId);
        WritePage(leftId, leftBytes);
        WritePage(rightId, rightBytes);

        byte[] separatorKey = rightEntries[0].Key;
        uint newRootIdSplit = PropagateSplit(path, leftId, rightId, separatorKey, ref newPageCount, ref newTreeHeight);

        CommitNewHeader(
            rootPageId: newRootIdSplit,
            pageCount: newPageCount,
            treeHeight: newTreeHeight,
            entryCount: _activeHeader.EntryCount + 1);
    }

    private uint PropagateChildReplacement(IReadOnlyList<PathStep> path, uint newChildId, ref uint newPageCount)
    {
        uint currentChildId = newChildId;

        for (int i = path.Count - 1; i >= 0; i--)
        {
            PathStep step = path[i];
            uint[] newChildIds = step.Page.ChildPageIds.ToArray();
            newChildIds[step.SlotIndex] = currentChildId;

            byte[][] keys = step.Page.Keys.ToArray();
            byte[] pageBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(_geometry, keys, newChildIds);

            uint newId = AllocatePage(ref newPageCount);
            WritePage(newId, pageBytes);
            currentChildId = newId;
        }

        return currentChildId;
    }

    private uint PropagateSplit(
        List<PathStep> path,
        uint leftChildId,
        uint rightChildId,
        byte[] separatorKey,
        ref uint newPageCount,
        ref ushort newTreeHeight)
    {
        uint currentLeftId = leftChildId;
        uint currentRightId = rightChildId;
        byte[] currentSeparator = separatorKey;

        for (int i = path.Count - 1; i >= 0; i--)
        {
            PathStep step = path[i];

            byte[][] mergedKeys = new byte[step.Page.KeyCount + 1][];
            uint[] mergedChildren = new uint[step.Page.ChildCount + 1];

            step.Page.Keys[..step.SlotIndex].CopyTo(mergedKeys.AsSpan(0, step.SlotIndex));
            mergedKeys[step.SlotIndex] = currentSeparator;
            step.Page.Keys[step.SlotIndex..].CopyTo(mergedKeys.AsSpan(step.SlotIndex + 1));

            step.Page.ChildPageIds[..step.SlotIndex].CopyTo(mergedChildren.AsSpan(0, step.SlotIndex));
            mergedChildren[step.SlotIndex] = currentLeftId;
            mergedChildren[step.SlotIndex + 1] = currentRightId;
            step.Page.ChildPageIds[(step.SlotIndex + 1)..].CopyTo(mergedChildren.AsSpan(step.SlotIndex + 2));

            int encodedSize = MutableBPlusTreeBytesPageCodec.MeasureInternalPage(mergedKeys);

            if (encodedSize <= _geometry.PageSize)
            {
                byte[] pageBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(_geometry, mergedKeys, mergedChildren);
                uint newId = AllocatePage(ref newPageCount);
                WritePage(newId, pageBytes);

                return PropagateChildReplacement(path.GetRange(0, i), newId, ref newPageCount);
            }

            // Internal split.
            int splitIndex = FindInternalSplitIndex(mergedKeys);
            byte[][] leftKeys = mergedKeys[..splitIndex];
            uint[] leftChildren = mergedChildren[..(splitIndex + 1)];
            byte[] promotedKey = mergedKeys[splitIndex];
            byte[][] rightKeys = mergedKeys[(splitIndex + 1)..];
            uint[] rightChildren = mergedChildren[(splitIndex + 1)..];

            uint newLeftId = AllocatePage(ref newPageCount);
            uint newRightId = AllocatePage(ref newPageCount);

            byte[] leftBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(_geometry, leftKeys, leftChildren);
            byte[] rightBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(_geometry, rightKeys, rightChildren);
            WritePage(newLeftId, leftBytes);
            WritePage(newRightId, rightBytes);

            currentLeftId = newLeftId;
            currentRightId = newRightId;
            currentSeparator = promotedKey;
        }

        // Root split — create a new root.
        byte[][] rootKeys = { currentSeparator };
        uint[] rootChildren = { currentLeftId, currentRightId };
        byte[] rootBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(_geometry, rootKeys, rootChildren);
        uint newRootId = AllocatePage(ref newPageCount);
        WritePage(newRootId, rootBytes);
        newTreeHeight++;

        return newRootId;
    }

    private int FindLeafSplitIndex(ReadOnlySpan<BytesIndexEntry> merged)
    {
        // Aim for a left half close to half the payload capacity.
        int target = _geometry.LeafPayloadCapacity / 2;
        int currentBytes = 0;
        int lastIndex = 1;

        for (int i = 0; i < merged.Length; i++)
        {
            int entrySize = MutableBPlusTreeBytesPageCodec.MeasureLeafEntries(merged.Slice(i, 1));

            if (currentBytes + entrySize > target && i > 0)
            {
                lastIndex = i;
                break;
            }

            currentBytes += entrySize;
            lastIndex = i + 1;
        }

        int leftBytes = MutableBPlusTreeBytesPageCodec.MeasureLeafEntries(merged[..lastIndex]);
        int rightBytes = MutableBPlusTreeBytesPageCodec.MeasureLeafEntries(merged[lastIndex..]);

        if (leftBytes > _geometry.LeafPayloadCapacity ||
            rightBytes > _geometry.LeafPayloadCapacity)
        {
            throw new InvalidOperationException(
                $"Cannot split leaf: left={leftBytes}, right={rightBytes} both must fit in " +
                $"{_geometry.LeafPayloadCapacity} bytes. A single oversized " +
                "entry can't be accommodated — bytes-keyed trees inherit Postgres-style " +
                "~1/3-page key-size limits.");
        }

        return lastIndex;
    }

    private int FindInternalSplitIndex(ReadOnlySpan<byte[]> mergedKeys)
    {
        int midpoint = mergedKeys.Length / 2;

        for (int delta = 0; delta <= mergedKeys.Length / 2; delta++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int candidate = midpoint + (delta * sign);

                if (candidate <= 0 || candidate >= mergedKeys.Length - 1)
                {
                    continue;
                }

                ReadOnlySpan<byte[]> left = mergedKeys[..candidate];
                ReadOnlySpan<byte[]> right = mergedKeys[(candidate + 1)..];

                int leftBytes = MutableBPlusTreeBytesPageCodec.MeasureInternalPage(left);
                int rightBytes = MutableBPlusTreeBytesPageCodec.MeasureInternalPage(right);

                if (leftBytes <= _geometry.PageSize &&
                    rightBytes <= _geometry.PageSize)
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            $"Cannot split internal page with {mergedKeys.Length} keys; no balanced split fits " +
            $"in {_geometry.PageSize} bytes.");
    }

    private static uint AllocatePage(ref uint pageCount)
    {
        uint id = pageCount;
        pageCount = checked(pageCount + 1);
        return id;
    }

    private void WritePage(uint pageId, byte[] pageBytes)
    {
        if (pageBytes.Length != _geometry.PageSize)
        {
            throw new InvalidOperationException(
                $"Encoded page must be exactly {_geometry.PageSize} bytes; got {pageBytes.Length}.");
        }

        long offset = MutableBPlusTreeConstants.PagesBaseOffset + ((long)pageId * _geometry.PageSize);
        _file.Position = offset;
        _file.Write(pageBytes);
    }

    private byte[] ReadPageBytes(uint pageId)
    {
        if (pageId >= _activeHeader.PageCount)
        {
            throw new InvalidDataException(
                $"Page id {pageId} out of range (page count = {_activeHeader.PageCount}).");
        }

        long offset = MutableBPlusTreeConstants.PagesBaseOffset + ((long)pageId * _geometry.PageSize);
        byte[] buffer = new byte[_geometry.PageSize];
        _file.Position = offset;
        _file.ReadExactly(buffer);
        return buffer;
    }

    private MutableBytesLeafPage ReadLeafPage(uint pageId)
        => MutableBPlusTreeBytesPageCodec.DecodeLeafPage(_geometry, ReadPageBytes(pageId), pageId);

    private MutableBytesInternalPage ReadInternalPage(uint pageId)
        => MutableBPlusTreeBytesPageCodec.DecodeInternalPage(_geometry, ReadPageBytes(pageId), pageId);

    private void CommitNewHeader(
        uint rootPageId,
        uint pageCount,
        ushort treeHeight,
        long entryCount,
        uint freeListHead = MutableBPlusTreeConstants.NoLinkedPage)
    {
        _file.Flush(flushToDisk: true);

        MutableBPlusTreeBytesHeader newHeader = new(
            CommitGen: _activeHeader.CommitGen + 1,
            RootPageId: rootPageId,
            FreeListHead: freeListHead,
            PageCount: pageCount,
            TreeHeight: treeHeight,
            EntryCount: entryCount,
            AllowDuplicates: _activeHeader.AllowDuplicates,
            PageSize: _activeHeader.PageSize);

        int targetSlot = 1 - _activeSlotIndex;
        long targetOffset = targetSlot == 0
            ? MutableBPlusTreeConstants.HeaderSlotAOffset
            : MutableBPlusTreeConstants.HeaderSlotBOffset;

        byte[] slot = new byte[MutableBPlusTreeConstants.HeaderSlotSize];
        newHeader.WriteTo(slot);
        _file.Position = targetOffset;
        _file.Write(slot);
        _file.Flush(flushToDisk: true);

        _activeHeader = newHeader;
        _activeSlotIndex = targetSlot;
    }

    private readonly record struct PathStep(MutableBytesInternalPage Page, int SlotIndex);
}

/// <summary>
/// Thrown by <see cref="MutableBPlusTreeBytes.Insert(BytesIndexEntry)"/>
/// when the inserted key already exists in the tree. Carries the offending
/// byte[] key; the caller (PK enforcement layer) wraps this in a
/// user-facing <c>PrimaryKeyViolationException</c> with the original
/// DataValue tuple context.
/// </summary>
internal sealed class DuplicateKeyException : Exception
{
    internal byte[] Key { get; }

    internal DuplicateKeyException(byte[] key)
        : base($"Bytes-keyed B+Tree: key already exists ({key.Length} bytes).")
    {
        Key = key;
    }
}
