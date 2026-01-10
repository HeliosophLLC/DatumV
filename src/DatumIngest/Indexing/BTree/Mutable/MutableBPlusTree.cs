using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree.Mutable;

/// <summary>
/// A crash-safe, single-writer, append-mostly B+Tree backed by a dedicated
/// <c>.datum-pkindex</c> file. Designed for primary-key indexes that must
/// survive process kills mid-write. Pages are 8 KiB, leaves uncompressed,
/// commits use a dual-slot header with a CRC32 checksum on each slot.
/// </summary>
/// <remarks>
/// <para><b>Allocation:</b> bump-only in PR10g. The header reserves a free-list
/// head field, but recycling is deferred to a follow-up PR (planned alongside
/// UPDATE/DELETE — PR10g's only consumer is PK INSERT, which never frees).</para>
///
/// <para><b>COW commit:</b> every insert allocates fresh page ids for the entire
/// root-to-leaf path being rewritten. Old pages stay on disk (currently leaked;
/// will be free-listed in a follow-up). Crash mid-write can only orphan unwritten
/// new pages — the active slot still references the previous tree.</para>
///
/// <para><b>Single-writer model:</b> instances are not thread-safe. Callers
/// (the table provider) hold a mutation lock that serializes all access.</para>
/// </remarks>
internal sealed class MutableBPlusTree : IDisposable
{
    private readonly FileStream _file;
    private MutableBPlusTreeHeader _activeHeader;
    private int _activeSlotIndex; // 0 or 1; next commit writes the OTHER slot.

    private MutableBPlusTree(FileStream file, MutableBPlusTreeHeader activeHeader, int activeSlotIndex)
    {
        _file = file;
        _activeHeader = activeHeader;
        _activeSlotIndex = activeSlotIndex;
    }

    /// <summary>Total number of entries currently in the tree.</summary>
    internal long EntryCount => _activeHeader.EntryCount;

    /// <summary>Tree height: 0 for empty, 1 if root is a leaf, etc.</summary>
    internal int TreeHeight => _activeHeader.TreeHeight;

    /// <summary>Total number of pages allocated in the file (including leaked COW pages).</summary>
    internal uint PageCount => _activeHeader.PageCount;

    /// <summary>The latest commit generation reflected by the active header.</summary>
    internal long CommitGen => _activeHeader.CommitGen;

    /// <summary>Key kind stored in this index, captured at file creation.</summary>
    internal DataKind KeyKind => _activeHeader.KeyKind;

    /// <summary>
    /// Creates a new <c>.datum-pkindex</c> file with both header slots initialized
    /// to an empty tree. Both slots get the same content with consecutive commit-gens
    /// so reader-open is deterministic regardless of which slot is read first.
    /// </summary>
    /// <exception cref="IOException">If the file already exists.</exception>
    internal static MutableBPlusTree Create(string path, DataKind keyKind)
    {
        FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            MutableBPlusTreeHeader empty = MutableBPlusTreeHeader.Empty(keyKind, commitGen: 0);
            MutableBPlusTreeHeader emptyB = MutableBPlusTreeHeader.Empty(keyKind, commitGen: 1);

            byte[] slot = new byte[MutableBPlusTreeConstants.HeaderSlotSize];

            empty.WriteTo(slot);
            file.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
            file.Write(slot);

            emptyB.WriteTo(slot);
            file.Position = MutableBPlusTreeConstants.HeaderSlotBOffset;
            file.Write(slot);

            file.Flush(flushToDisk: true);

            // Slot B has the higher gen so it's "active" right after Create. Next
            // commit writes to slot A, alternating from there.
            return new MutableBPlusTree(file, emptyB, activeSlotIndex: 1);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing <c>.datum-pkindex</c> file. The reader picks the slot whose
    /// CRC validates and whose commit gen is higher; torn writes leave the previous
    /// slot intact, so a crashed mid-commit file still opens cleanly to the previous state.
    /// </summary>
    /// <exception cref="InvalidDataException">If neither header slot validates.</exception>
    internal static MutableBPlusTree Open(string path)
    {
        FileStream file = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            byte[] slot = new byte[MutableBPlusTreeConstants.HeaderSlotSize];

            file.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
            file.ReadExactly(slot);
            bool aValid = MutableBPlusTreeHeader.TryReadFrom(slot, out MutableBPlusTreeHeader headerA);

            file.Position = MutableBPlusTreeConstants.HeaderSlotBOffset;
            file.ReadExactly(slot);
            bool bValid = MutableBPlusTreeHeader.TryReadFrom(slot, out MutableBPlusTreeHeader headerB);

            if (!aValid && !bValid)
            {
                throw new InvalidDataException(
                    $"Both header slots in '{path}' are invalid. The file is corrupt or not a mutable B+Tree.");
            }

            int activeSlot;
            MutableBPlusTreeHeader active;

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

            return new MutableBPlusTree(file, active, activeSlot);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Inserts an entry. Throws <see cref="DuplicatePrimaryKeyException"/> if the
    /// key already exists — caller can pattern-match to surface a user-friendly
    /// PK-violation error.
    /// </summary>
    internal void Insert(ValueIndexEntry entry)
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            InsertIntoEmptyTree(entry);
            return;
        }

        // Walk root → leaf, recording the path so we can COW-rewrite on the way back up.
        List<PathStep> path = new(_activeHeader.TreeHeight);
        uint pageId = _activeHeader.RootPageId;

        for (int level = 0; level < _activeHeader.TreeHeight - 1; level++)
        {
            MutableInternalPage internalPage = ReadInternalPage(pageId);
            int slot = internalPage.FindChildSlot(entry.Key);
            path.Add(new PathStep(internalPage, slot));
            pageId = internalPage.GetChildPageId(slot);
        }

        MutableLeafPage leaf = ReadLeafPage(pageId);

        // Uniqueness check.
        if (leaf.BinarySearchFirst(entry.Key) >= 0)
        {
            throw new DuplicatePrimaryKeyException(entry.Key);
        }

        InsertIntoLeafAndPropagate(leaf, entry, path);
    }

    /// <summary>
    /// Returns true and the matching entry if <paramref name="key"/> exists in the tree.
    /// </summary>
    internal bool TryFind(DataValue key, out ValueIndexEntry entry)
    {
        if (_activeHeader.RootPageId == MutableBPlusTreeConstants.NoLinkedPage)
        {
            entry = default;
            return false;
        }

        uint pageId = _activeHeader.RootPageId;

        for (int level = 0; level < _activeHeader.TreeHeight - 1; level++)
        {
            MutableInternalPage internalPage = ReadInternalPage(pageId);
            int slot = internalPage.FindChildSlot(key);
            pageId = internalPage.GetChildPageId(slot);
        }

        MutableLeafPage leaf = ReadLeafPage(pageId);
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

    // --- Insert internals ---

    private void InsertIntoEmptyTree(ValueIndexEntry entry)
    {
        ValueIndexEntry[] entries = new[] { entry };
        byte[] leafBytes = MutablePageCodec.EncodeLeafPage(
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

    private void InsertIntoLeafAndPropagate(MutableLeafPage leaf, ValueIndexEntry entry, List<PathStep> path)
    {
        // Build the merged entry list.
        int insertPos = leaf.BinarySearchInsertPosition(entry.Key);
        ValueIndexEntry[] merged = new ValueIndexEntry[leaf.EntryCount + 1];
        leaf.Entries[..insertPos].CopyTo(merged.AsSpan(0, insertPos));
        merged[insertPos] = entry;
        leaf.Entries[insertPos..].CopyTo(merged.AsSpan(insertPos + 1));

        uint newPageCount = _activeHeader.PageCount;
        ushort newTreeHeight = _activeHeader.TreeHeight;

        // Try to fit in one leaf.
        int payloadBytes = MutablePageCodec.MeasureLeafEntries(merged);

        if (payloadBytes <= MutableBPlusTreeConstants.LeafPayloadCapacity)
        {
            // No split needed. Rewrite leaf to new page id, then walk up rewriting parents.
            uint newLeafId = AllocatePage(ref newPageCount);
            byte[] leafBytes = MutablePageCodec.EncodeLeafPage(merged, leaf.PreviousLeafPageId, leaf.NextLeafPageId);
            WritePage(newLeafId, leafBytes);

            // If this leaf has a "previous" sibling, we need to update its NextLeafPageId
            // because the leaf got a new page id. Same for "next" sibling's prev pointer.
            // For PR10g we don't fix sibling pointers (the linked list is unused — readers
            // walk via internal pages). The pointers are stored for forward compat.

            uint newRootId = PropagateChildReplacement(path, newLeafId, ref newPageCount);

            CommitNewHeader(
                rootPageId: newRootId,
                pageCount: newPageCount,
                treeHeight: newTreeHeight,
                entryCount: _activeHeader.EntryCount + 1);
            return;
        }

        // Leaf split. Pick a midpoint; left and right each become new leaf pages.
        int splitIndex = FindLeafSplitIndex(merged);
        ValueIndexEntry[] leftEntries = merged[..splitIndex];
        ValueIndexEntry[] rightEntries = merged[splitIndex..];

        uint leftId = AllocatePage(ref newPageCount);
        uint rightId = AllocatePage(ref newPageCount);

        byte[] leftBytes = MutablePageCodec.EncodeLeafPage(leftEntries, leaf.PreviousLeafPageId, rightId);
        byte[] rightBytes = MutablePageCodec.EncodeLeafPage(rightEntries, leftId, leaf.NextLeafPageId);
        WritePage(leftId, leftBytes);
        WritePage(rightId, rightBytes);

        // Promote the first key of the right half to the parent.
        DataValue separatorKey = rightEntries[0].Key;
        uint newRootIdSplit = PropagateSplit(path, leftId, rightId, separatorKey, ref newPageCount, ref newTreeHeight);

        CommitNewHeader(
            rootPageId: newRootIdSplit,
            pageCount: newPageCount,
            treeHeight: newTreeHeight,
            entryCount: _activeHeader.EntryCount + 1);
    }

    /// <summary>
    /// Walks the recorded path from leaf-parent up to root, COW-rewriting each parent
    /// to point at the new child page id. Returns the new root page id.
    /// </summary>
    private uint PropagateChildReplacement(List<PathStep> path, uint newChildId, ref uint newPageCount)
    {
        uint currentChildId = newChildId;

        for (int i = path.Count - 1; i >= 0; i--)
        {
            PathStep step = path[i];
            uint[] newChildIds = step.Page.ChildPageIds.ToArray();
            newChildIds[step.SlotIndex] = currentChildId;

            DataValue[] keys = step.Page.Keys.ToArray();
            byte[] pageBytes = MutablePageCodec.EncodeInternalPage(keys, newChildIds);

            uint newId = AllocatePage(ref newPageCount);
            WritePage(newId, pageBytes);
            currentChildId = newId;
        }

        return currentChildId;
    }

    /// <summary>
    /// Walks the recorded path from leaf-parent up to root, propagating a leaf split.
    /// Each parent has the affected child slot replaced with the new left page id, and
    /// the new right page id + separator key inserted just after. If the parent itself
    /// overflows, it splits. If the root splits, a new root is created (tree height + 1).
    /// </summary>
    private uint PropagateSplit(
        List<PathStep> path,
        uint leftChildId,
        uint rightChildId,
        DataValue separatorKey,
        ref uint newPageCount,
        ref ushort newTreeHeight)
    {
        uint currentLeftId = leftChildId;
        uint currentRightId = rightChildId;
        DataValue currentSeparator = separatorKey;

        for (int i = path.Count - 1; i >= 0; i--)
        {
            PathStep step = path[i];

            // Build the merged keys/children for this internal page.
            DataValue[] mergedKeys = new DataValue[step.Page.KeyCount + 1];
            uint[] mergedChildren = new uint[step.Page.ChildCount + 1];

            // Copy keys[..slot] verbatim.
            step.Page.Keys[..step.SlotIndex].CopyTo(mergedKeys.AsSpan(0, step.SlotIndex));
            // Insert the new separator at slot.
            mergedKeys[step.SlotIndex] = currentSeparator;
            // Copy keys[slot..] shifted by 1.
            step.Page.Keys[step.SlotIndex..].CopyTo(mergedKeys.AsSpan(step.SlotIndex + 1));

            // Children: [..slot] verbatim, slot replaced with leftId, slot+1 = rightId, then [slot+1..] shifted.
            step.Page.ChildPageIds[..step.SlotIndex].CopyTo(mergedChildren.AsSpan(0, step.SlotIndex));
            mergedChildren[step.SlotIndex] = currentLeftId;
            mergedChildren[step.SlotIndex + 1] = currentRightId;
            step.Page.ChildPageIds[(step.SlotIndex + 1)..].CopyTo(mergedChildren.AsSpan(step.SlotIndex + 2));

            // Try to fit.
            int encodedSize = MutablePageCodec.MeasureInternalPage(mergedKeys);

            if (encodedSize <= MutableBPlusTreeConstants.PageSize)
            {
                byte[] pageBytes = MutablePageCodec.EncodeInternalPage(mergedKeys, mergedChildren);
                uint newId = AllocatePage(ref newPageCount);
                WritePage(newId, pageBytes);

                // Continue propagating the new id up as a simple replacement (no further split).
                return PropagateChildReplacement(path[..i], newId, ref newPageCount);
            }

            // Internal split.
            int splitIndex = FindInternalSplitIndex(mergedKeys);
            DataValue[] leftKeys = mergedKeys[..splitIndex];
            uint[] leftChildren = mergedChildren[..(splitIndex + 1)];
            DataValue promotedKey = mergedKeys[splitIndex];
            DataValue[] rightKeys = mergedKeys[(splitIndex + 1)..];
            uint[] rightChildren = mergedChildren[(splitIndex + 1)..];

            uint newLeftId = AllocatePage(ref newPageCount);
            uint newRightId = AllocatePage(ref newPageCount);

            byte[] leftBytes = MutablePageCodec.EncodeInternalPage(leftKeys, leftChildren);
            byte[] rightBytes = MutablePageCodec.EncodeInternalPage(rightKeys, rightChildren);
            WritePage(newLeftId, leftBytes);
            WritePage(newRightId, rightBytes);

            currentLeftId = newLeftId;
            currentRightId = newRightId;
            currentSeparator = promotedKey;
        }

        // Path exhausted — root split. Create a new root.
        DataValue[] rootKeys = { currentSeparator };
        uint[] rootChildren = { currentLeftId, currentRightId };
        byte[] rootBytes = MutablePageCodec.EncodeInternalPage(rootKeys, rootChildren);
        uint newRootId = AllocatePage(ref newPageCount);
        WritePage(newRootId, rootBytes);
        newTreeHeight++;

        return newRootId;
    }

    private static int FindLeafSplitIndex(ReadOnlySpan<ValueIndexEntry> merged)
    {
        // Pick the index whose left half is closest to half the payload capacity.
        // Iterate from the middle outward — usually the natural midpoint works.
        int target = MutableBPlusTreeConstants.LeafPayloadCapacity / 2;
        int currentBytes = 0;
        int lastIndex = 1;

        for (int i = 0; i < merged.Length; i++)
        {
            int entrySize = MutablePageCodec.MeasureLeafEntries(merged.Slice(i, 1));

            if (currentBytes + entrySize > target && i > 0)
            {
                lastIndex = i;
                break;
            }

            currentBytes += entrySize;
            lastIndex = i + 1;
        }

        // Validate both halves fit in a single leaf each.
        int leftBytes = MutablePageCodec.MeasureLeafEntries(merged[..lastIndex]);
        int rightBytes = MutablePageCodec.MeasureLeafEntries(merged[lastIndex..]);

        if (leftBytes > MutableBPlusTreeConstants.LeafPayloadCapacity ||
            rightBytes > MutableBPlusTreeConstants.LeafPayloadCapacity)
        {
            // Single oversized entry — would be a bug since PK keys are bounded ≤ 16 bytes,
            // so two entries always fit. Surface clearly.
            throw new InvalidOperationException(
                $"Cannot split leaf: left={leftBytes}, right={rightBytes} both must fit in {MutableBPlusTreeConstants.LeafPayloadCapacity} bytes. " +
                "PK leaf keys must be ≤ 16 bytes (validated by catalog).");
        }

        return lastIndex;
    }

    private static int FindInternalSplitIndex(ReadOnlySpan<DataValue> mergedKeys)
    {
        // For internal split, we promote one key and split the rest. Try the midpoint
        // first, then walk down/up to find a split that fits both halves in a page.
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

                ReadOnlySpan<DataValue> left = mergedKeys[..candidate];
                ReadOnlySpan<DataValue> right = mergedKeys[(candidate + 1)..];

                // Encoded-size estimate; if either half overflows, skip.
                int leftBytes = MutablePageCodec.MeasureInternalPage(left);
                int rightBytes = MutablePageCodec.MeasureInternalPage(right);

                if (leftBytes <= MutableBPlusTreeConstants.PageSize &&
                    rightBytes <= MutableBPlusTreeConstants.PageSize)
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            $"Cannot split internal page with {mergedKeys.Length} keys; no balanced split fits in {MutableBPlusTreeConstants.PageSize} bytes.");
    }

    private uint AllocatePage(ref uint pageCount)
    {
        // Bump-only in PR10g; free-list reuse deferred.
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

    private MutableLeafPage ReadLeafPage(uint pageId)
    {
        return MutablePageCodec.DecodeLeafPage(ReadPageBytes(pageId), pageId);
    }

    private MutableInternalPage ReadInternalPage(uint pageId)
    {
        return MutablePageCodec.DecodeInternalPage(ReadPageBytes(pageId), pageId);
    }

    private void CommitNewHeader(uint rootPageId, uint pageCount, ushort treeHeight, long entryCount)
    {
        // Pages are already on disk (caller flushed before this). Now flip the header slot.
        _file.Flush(flushToDisk: true);

        MutableBPlusTreeHeader newHeader = new(
            CommitGen: _activeHeader.CommitGen + 1,
            RootPageId: rootPageId,
            FreeListHead: MutableBPlusTreeConstants.NoLinkedPage, // PR10g: no recycling.
            PageCount: pageCount,
            TreeHeight: treeHeight,
            EntryCount: entryCount,
            KeyKind: _activeHeader.KeyKind);

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

    private readonly record struct PathStep(MutableInternalPage Page, int SlotIndex);
}

/// <summary>
/// Thrown by <see cref="MutableBPlusTree.Insert(ValueIndexEntry)"/> when the
/// inserted key already exists in the tree. The caller (PK enforcement layer)
/// catches this and surfaces a user-facing PrimaryKeyViolationException.
/// </summary>
internal sealed class DuplicatePrimaryKeyException : Exception
{
    internal DataValue Key { get; }

    internal DuplicatePrimaryKeyException(DataValue key)
        : base($"Primary key value already exists: {key}")
    {
        Key = key;
    }
}
