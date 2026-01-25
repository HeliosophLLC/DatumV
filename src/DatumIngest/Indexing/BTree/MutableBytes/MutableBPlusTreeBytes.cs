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
    private MutableBPlusTreeBytesHeader _activeHeader;
    private int _activeSlotIndex;

    private MutableBPlusTreeBytes(FileStream file, MutableBPlusTreeBytesHeader activeHeader, int activeSlotIndex)
    {
        _file = file;
        _activeHeader = activeHeader;
        _activeSlotIndex = activeSlotIndex;
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
    /// <exception cref="IOException">If the file already exists.</exception>
    internal static MutableBPlusTreeBytes Create(string path, bool allowDuplicates = false)
    {
        FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            MutableBPlusTreeBytesHeader emptyA = MutableBPlusTreeBytesHeader.Empty(commitGen: 0, allowDuplicates);
            MutableBPlusTreeBytesHeader emptyB = MutableBPlusTreeBytesHeader.Empty(commitGen: 1, allowDuplicates);

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

    public void Dispose()
    {
        _file.Dispose();
    }

    // ───── Insert internals ─────

    private void InsertIntoEmptyTree(BytesIndexEntry entry)
    {
        BytesIndexEntry[] entries = new[] { entry };
        byte[] leafBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(
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
        int insertPos = leaf.BinarySearchInsertPosition(entry.Key);
        BytesIndexEntry[] merged = new BytesIndexEntry[leaf.EntryCount + 1];
        leaf.Entries[..insertPos].CopyTo(merged.AsSpan(0, insertPos));
        merged[insertPos] = entry;
        leaf.Entries[insertPos..].CopyTo(merged.AsSpan(insertPos + 1));

        uint newPageCount = _activeHeader.PageCount;
        ushort newTreeHeight = _activeHeader.TreeHeight;

        int payloadBytes = MutableBPlusTreeBytesPageCodec.MeasureLeafEntries(merged);

        if (payloadBytes <= MutableBPlusTreeConstants.LeafPayloadCapacity)
        {
            uint newLeafId = AllocatePage(ref newPageCount);
            byte[] leafBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(
                merged, leaf.PreviousLeafPageId, leaf.NextLeafPageId);
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

        byte[] leftBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(leftEntries, leaf.PreviousLeafPageId, rightId);
        byte[] rightBytes = MutableBPlusTreeBytesPageCodec.EncodeLeafPage(rightEntries, leftId, leaf.NextLeafPageId);
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
            byte[] pageBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(keys, newChildIds);

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

            if (encodedSize <= MutableBPlusTreeConstants.PageSize)
            {
                byte[] pageBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(mergedKeys, mergedChildren);
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

            byte[] leftBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(leftKeys, leftChildren);
            byte[] rightBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(rightKeys, rightChildren);
            WritePage(newLeftId, leftBytes);
            WritePage(newRightId, rightBytes);

            currentLeftId = newLeftId;
            currentRightId = newRightId;
            currentSeparator = promotedKey;
        }

        // Root split — create a new root.
        byte[][] rootKeys = { currentSeparator };
        uint[] rootChildren = { currentLeftId, currentRightId };
        byte[] rootBytes = MutableBPlusTreeBytesPageCodec.EncodeInternalPage(rootKeys, rootChildren);
        uint newRootId = AllocatePage(ref newPageCount);
        WritePage(newRootId, rootBytes);
        newTreeHeight++;

        return newRootId;
    }

    private static int FindLeafSplitIndex(ReadOnlySpan<BytesIndexEntry> merged)
    {
        // Aim for a left half close to half the payload capacity.
        int target = MutableBPlusTreeConstants.LeafPayloadCapacity / 2;
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

        if (leftBytes > MutableBPlusTreeConstants.LeafPayloadCapacity ||
            rightBytes > MutableBPlusTreeConstants.LeafPayloadCapacity)
        {
            throw new InvalidOperationException(
                $"Cannot split leaf: left={leftBytes}, right={rightBytes} both must fit in " +
                $"{MutableBPlusTreeConstants.LeafPayloadCapacity} bytes. A single oversized " +
                "entry can't be accommodated — bytes-keyed trees inherit Postgres-style " +
                "~1/3-page key-size limits.");
        }

        return lastIndex;
    }

    private static int FindInternalSplitIndex(ReadOnlySpan<byte[]> mergedKeys)
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

                if (leftBytes <= MutableBPlusTreeConstants.PageSize &&
                    rightBytes <= MutableBPlusTreeConstants.PageSize)
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            $"Cannot split internal page with {mergedKeys.Length} keys; no balanced split fits " +
            $"in {MutableBPlusTreeConstants.PageSize} bytes.");
    }

    private static uint AllocatePage(ref uint pageCount)
    {
        uint id = pageCount;
        pageCount = checked(pageCount + 1);
        return id;
    }

    private void WritePage(uint pageId, byte[] pageBytes)
    {
        if (pageBytes.Length != MutableBPlusTreeConstants.PageSize)
        {
            throw new InvalidOperationException(
                $"Encoded page must be exactly {MutableBPlusTreeConstants.PageSize} bytes; got {pageBytes.Length}.");
        }

        long offset = MutableBPlusTreeConstants.PagesBaseOffset + ((long)pageId * MutableBPlusTreeConstants.PageSize);
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

        long offset = MutableBPlusTreeConstants.PagesBaseOffset + ((long)pageId * MutableBPlusTreeConstants.PageSize);
        byte[] buffer = new byte[MutableBPlusTreeConstants.PageSize];
        _file.Position = offset;
        _file.ReadExactly(buffer);
        return buffer;
    }

    private MutableBytesLeafPage ReadLeafPage(uint pageId)
        => MutableBPlusTreeBytesPageCodec.DecodeLeafPage(ReadPageBytes(pageId), pageId);

    private MutableBytesInternalPage ReadInternalPage(uint pageId)
        => MutableBPlusTreeBytesPageCodec.DecodeInternalPage(ReadPageBytes(pageId), pageId);

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
            AllowDuplicates: _activeHeader.AllowDuplicates);

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
