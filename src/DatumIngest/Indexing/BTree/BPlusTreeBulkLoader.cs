using System.Runtime.InteropServices;
using System.Text;
using DatumIngest.Model;
using DatumIngest.Indexing.Sorted;
using DatumIngest.IO;

namespace DatumIngest.Indexing.BTree;

/// <summary>
/// Builds an immutable B+Tree by consuming sorted entries and writing pages
/// sequentially to an output stream. Uses a bottom-up bulk-load algorithm:
/// leaf pages are written first (in order), then internal levels are built
/// from the separator keys collected during leaf construction.
/// </summary>
/// <remarks>
/// <para>
/// Memory discipline: the bulk loader holds at most one leaf page worth of
/// entries plus the separator key list for internal node construction. For 32M
/// rows with ~22K leaves, the separator list is a few MB — negligible compared
/// to the hundreds of MB a <see cref="SortedIndex"/> would consume.
/// </para>
/// <para>
/// The on-disk section layout produced by this builder:
/// <code>
/// [BTreeSectionHeader]
/// [Leaf₀] [Leaf₁] ... [Leafₙ]
/// [Internal₀] [Internal₁] ... [Internalₘ]
/// [Root page]
/// </code>
/// Pages are written leaves-first, then internal levels bottom-up. The root
/// page index in the section header points to the last page written.
/// </para>
/// </remarks>
internal sealed class BPlusTreeBulkLoader
{
    /// <summary>
    /// Initial estimate for maximum entries per leaf page. Adjusted dynamically
    /// based on actual compression results.
    /// </summary>
    private const int InitialTargetEntriesPerLeaf = 500;

    /// <summary>
    /// Minimum entries per leaf page. If a leaf page can't hold at least this many
    /// entries after compression, the key size is too large for the page format.
    /// </summary>
    private const int MinimumEntriesPerLeaf = 2;

    /// <summary>
    /// Builds a B+Tree from sorted entries and writes the complete column section
    /// (header + pages) to the output stream. The section header is written first
    /// with placeholder values, pages follow, then the header is patched with the
    /// actual root page index, entry count, tree height, and page count.
    /// </summary>
    /// <param name="sortedEntries">
    /// Entries in ascending key order. The caller must ensure sort order.
    /// </param>
    /// <param name="columnName">Name of the indexed column.</param>
    /// <param name="keyKind">The <see cref="DataKind"/> of the key column.</param>
    /// <param name="output">Writable, seekable output stream.</param>
    /// <param name="estimatedEntryCount">
    /// Optional hint for total entry count, used to pre-size internal lists.
    /// </param>
    /// <returns>
    /// The section header describing the resulting tree. Returns <c>null</c> if
    /// <paramref name="sortedEntries"/> is empty.
    /// </returns>
    internal static BPlusTreeSectionHeader? Build(
        IEnumerable<ValueIndexEntry> sortedEntries,
        string columnName,
        DataKind keyKind,
        BinaryWriter output,
        long estimatedEntryCount = 0)
    {
        output.Flush();
        using BufferedWriter writer = new(output.BaseStream);
        BPlusTreeSectionHeader? result = Build(sortedEntries, columnName, keyKind, writer, estimatedEntryCount);
        writer.Flush();
        return result;
    }

    /// <summary>
    /// Builds a B+Tree from sorted entries using a <see cref="BufferedWriter"/>
    /// for high-throughput page serialization.
    /// </summary>
    /// <param name="sortedEntries">Entries in ascending key order.</param>
    /// <param name="columnName">Name of the indexed column.</param>
    /// <param name="keyKind">The <see cref="DataKind"/> of the key column.</param>
    /// <param name="output">Buffered output stream.</param>
    /// <param name="estimatedEntryCount">
    /// Optional hint for total entry count. When positive, used to pre-size internal
    /// lists (separator keys, child page indexes) and avoid geometric-doubling allocations.
    /// </param>
    internal static BPlusTreeSectionHeader? Build(
        IEnumerable<ValueIndexEntry> sortedEntries,
        string columnName,
        DataKind keyKind,
        BufferedWriter output,
        long estimatedEntryCount = 0)
    {
        // Write placeholder section header (same column name ensures identical byte length).
        long headerPosition = output.Position;
        BPlusTreeSectionHeader placeholder = new(
            columnName, keyKind, 0, 0, 0, (ushort)BPlusTreeConstants.PageSize, 0);
        WriteSectionHeader(output, placeholder);

        // Phase 1: Write leaf pages and collect separator keys.
        LeafBuildResult leafResult = WriteLeafPages(sortedEntries, output, estimatedEntryCount);

        if (leafResult.LeafCount == 0)
        {
            // Seek back to erase the placeholder header.
            output.Flush();
            output.BaseStream.Position = headerPosition;
            return null;
        }

        // Phase 2: Build internal levels bottom-up.
        InternalBuildResult internalResult = WriteInternalLevels(
            leafResult.SeparatorKeys,
            leafResult.ChildPageIndexes,
            leafResult.LeafCount,
            output);

        ushort treeHeight = (ushort)(1 + internalResult.InternalLevelCount);
        uint totalPageCount = leafResult.LeafCount + internalResult.TotalInternalPages;

        BPlusTreeSectionHeader actualHeader = new(
            columnName,
            keyKind,
            internalResult.RootPageIndex,
            leafResult.TotalEntryCount,
            treeHeight,
            (ushort)BPlusTreeConstants.PageSize,
            totalPageCount);

        // Patch the placeholder header with actual values.
        output.Flush();
        long savedPosition = output.BaseStream.Position;
        output.BaseStream.Position = headerPosition;
        WriteSectionHeader(output, actualHeader);
        output.Flush();
        output.BaseStream.Position = savedPosition;

        return actualHeader;
    }

    /// <summary>
    /// Writes the B+Tree section header to the output stream.
    /// </summary>
    internal static void WriteSectionHeader(BinaryWriter writer, BPlusTreeSectionHeader header)
    {
        writer.Write(header.ColumnName);
        writer.Write((byte)header.KeyKind);
        writer.Write(header.RootPageIndex);
        writer.Write(header.EntryCount);
        writer.Write(header.TreeHeight);
        writer.Write(header.PageSize);
        writer.Write(header.PageCount);
    }

    /// <summary>
    /// Writes the B+Tree section header using a <see cref="BufferedWriter"/>.
    /// </summary>
    internal static void WriteSectionHeader(BufferedWriter writer, BPlusTreeSectionHeader header)
    {
        writer.Write(header.ColumnName);
        writer.Write((byte)header.KeyKind);
        writer.Write(header.RootPageIndex);
        writer.Write(header.EntryCount);
        writer.Write(header.TreeHeight);
        writer.Write(header.PageSize);
        writer.Write(header.PageCount);
    }

    /// <summary>
    /// Reads a B+Tree section header from the input stream.
    /// </summary>
    internal static BPlusTreeSectionHeader ReadSectionHeader(BinaryReader reader)
    {
        string columnName = reader.ReadString();
        DataKind keyKind = (DataKind)reader.ReadByte();
        uint rootPageIndex = reader.ReadUInt32();
        long entryCount = reader.ReadInt64();
        ushort treeHeight = reader.ReadUInt16();
        ushort pageSize = reader.ReadUInt16();
        uint pageCount = reader.ReadUInt32();

        return new BPlusTreeSectionHeader(
            columnName, keyKind, rootPageIndex, entryCount, treeHeight, pageSize, pageCount);
    }

    // ───────────────────────── Leaf page construction ─────────────────────────

    /// <summary>
    /// Consumes sorted entries, packs them into leaf pages with Zstd compression,
    /// and writes each page to the output stream. Collects separator keys (first key
    /// of each new leaf after the first) for internal node construction.
    /// </summary>
    private static LeafBuildResult WriteLeafPages(
        IEnumerable<ValueIndexEntry> sortedEntries,
        BufferedWriter output,
        long estimatedEntryCount = 0)
    {
        // Pre-size lists when the caller provides an entry count estimate.
        // This avoids geometric-doubling allocations that promote to Gen2.
        int estimatedLeafCount = estimatedEntryCount > 0
            ? (int)(estimatedEntryCount / InitialTargetEntriesPerLeaf) + 1
            : 0;
        List<DataValue> separatorKeys = new(estimatedLeafCount);
        List<uint> childPageIndexes = new(estimatedLeafCount);
        List<ValueIndexEntry> currentLeafEntries = new(InitialTargetEntriesPerLeaf);
        int targetEntriesPerLeaf = InitialTargetEntriesPerLeaf;
        uint pageIndex = 0;
        long totalEntryCount = 0;

        // Track previous leaf page position for patching prev/next pointers.
        // We write leaf pages with a placeholder next-leaf pointer, then the leaf
        // chain is implicitly correct because we write pages sequentially:
        // leaf 0 → next=1, leaf 1 → next=2, ..., leaf N → next=NoLinkedPage.
        // Previous links: leaf 0 → prev=NoLinkedPage, leaf 1 → prev=0, etc.
        long previousLeafStreamPosition = -1;

        using IEnumerator<ValueIndexEntry> enumerator = sortedEntries.GetEnumerator();
        bool hasMore = enumerator.MoveNext();

        while (hasMore || currentLeafEntries.Count > 0)
        {
            // Note: don't clear currentLeafEntries at the top of the
            // loop. The back-off path (acceptedCount < count) leaves
            // unwritten entries here; they must survive the iteration
            // boundary and slot into the next leaf. Clear runs at the
            // end of the no-back-off branch instead.

            // Fill the current leaf batch.
            while (hasMore && currentLeafEntries.Count < targetEntriesPerLeaf)
            {
                currentLeafEntries.Add(enumerator.Current);
                hasMore = enumerator.MoveNext();
            }

            // Try to encode the leaf page. If the compressed payload is too large,
            // reduce the batch size until it fits.
            int acceptedCount = FindMaxFittingEntries(currentLeafEntries);

            if (acceptedCount < currentLeafEntries.Count)
            {
                // Return excess entries to be processed in the next iteration.
                // We can't "unread" from the enumerator, so we keep them in the list
                // and slice below.
            }

            // Record separator key for this leaf (the first key of every leaf after the first).
            if (pageIndex > 0)
            {
                separatorKeys.Add(currentLeafEntries[0].Key);
            }

            childPageIndexes.Add(pageIndex);

            // Encode and write the leaf page.
            uint previousLeafPageIndex = pageIndex == 0
                ? BPlusTreeConstants.NoLinkedPage
                : pageIndex - 1;

            // We don't know the next leaf page index yet. We'll use pageIndex + 1
            // optimistically; the last leaf will be patched below.
            ReadOnlySpan<ValueIndexEntry> acceptedEntries =
                CollectionsMarshal.AsSpan(currentLeafEntries)[..acceptedCount];

            byte[] pageBytes = BPlusTreePageCodec.EncodeLeafPage(
                acceptedEntries,
                previousLeafPageIndex,
                pageIndex + 1); // Optimistic next pointer; patched for last leaf.

            long currentStreamPosition = output.Position;
            output.Write(pageBytes);

            previousLeafStreamPosition = currentStreamPosition;
            totalEntryCount += acceptedCount;

            // Update target based on what actually fit.
            targetEntriesPerLeaf = Math.Max(acceptedCount, MinimumEntriesPerLeaf);

            // If we had excess entries, put them at the front of the next batch.
            if (acceptedCount < currentLeafEntries.Count)
            {
                currentLeafEntries.RemoveRange(0, acceptedCount);

                // Re-add buffered entries to fill the next batch.
                while (hasMore && currentLeafEntries.Count < targetEntriesPerLeaf)
                {
                    currentLeafEntries.Add(enumerator.Current);
                    hasMore = enumerator.MoveNext();
                }

                // Don't increment pageIndex yet — continue to the next iteration
                // where we'll write these entries.
                pageIndex++;
                continue;
            }

            pageIndex++;
            currentLeafEntries.Clear();
        }

        // Patch the last leaf's next pointer to NoLinkedPage.
        if (pageIndex > 0 && previousLeafStreamPosition >= 0 && output.BaseStream.CanSeek)
        {
            output.Flush();
            long savedPosition = output.BaseStream.Position;
            PatchLastLeafNextPointer(output, previousLeafStreamPosition);
            output.BaseStream.Position = savedPosition;
        }

        return new LeafBuildResult(pageIndex, totalEntryCount, separatorKeys, childPageIndexes);
    }

    /// <summary>
    /// Finds the maximum number of entries from the start of <paramref name="entries"/>
    /// that fit into a single compressed leaf page.
    /// </summary>
    private static int FindMaxFittingEntries(List<ValueIndexEntry> entries)
    {
        if (entries.Count == 0)
        {
            return 0;
        }

        // Try the full batch first.
        ReadOnlySpan<ValueIndexEntry> span = CollectionsMarshal.AsSpan(entries);

        if (TryEncodeLeaf(span))
        {
            return entries.Count;
        }

        // Binary search for the maximum that fits.
        int low = MinimumEntriesPerLeaf;
        int high = entries.Count - 1;
        int bestFit = MinimumEntriesPerLeaf;

        // Verify the minimum fits.
        if (!TryEncodeLeaf(span[..low]))
        {
            throw new InvalidOperationException(
                $"Cannot fit even {MinimumEntriesPerLeaf} entries into a leaf page. " +
                "Key values may be too large for the B+Tree page format.");
        }

        while (low <= high)
        {
            int mid = low + (high - low) / 2;

            if (TryEncodeLeaf(span[..mid]))
            {
                bestFit = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return bestFit;
    }

    /// <summary>
    /// Tests whether the given entries fit into a single compressed leaf page.
    /// </summary>
    private static bool TryEncodeLeaf(ReadOnlySpan<ValueIndexEntry> entries)
    {
        try
        {
            BPlusTreePageCodec.EncodeLeafPage(
                entries,
                BPlusTreeConstants.NoLinkedPage,
                BPlusTreeConstants.NoLinkedPage);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Patches the next-leaf pointer in the last written leaf page from
    /// <c>pageIndex + 1</c> to <see cref="BPlusTreeConstants.NoLinkedPage"/>.
    /// </summary>
    private static void PatchLastLeafNextPointer(BufferedWriter writer, long leafStreamPosition)
    {
        // The next-leaf field is at offset: PageHeaderSize (4) + sizeof(uint) prev (4) = 8.
        long nextLeafFieldOffset = leafStreamPosition
            + BPlusTreeConstants.PageHeaderSize
            + sizeof(uint); // Previous leaf pointer.

        writer.BaseStream.Position = nextLeafFieldOffset;
        writer.Write(BPlusTreeConstants.NoLinkedPage);
        writer.Flush();
    }

    // ───────────────────────── Internal level construction ─────────────────────────

    /// <summary>
    /// Builds internal (branch) levels bottom-up from the separator keys collected
    /// during leaf construction. Each level's pages become the children of the next
    /// level up, until a single root page is produced.
    /// </summary>
    private static InternalBuildResult WriteInternalLevels(
        List<DataValue> separatorKeys,
        List<uint> childPageIndexes,
        uint nextPageIndex,
        BufferedWriter output)
    {
        if (childPageIndexes.Count <= 1)
        {
            // Single leaf page — it is the root. No internal pages needed.
            return new InternalBuildResult(
                RootPageIndex: childPageIndexes.Count > 0 ? childPageIndexes[0] : 0,
                InternalLevelCount: 0,
                TotalInternalPages: 0);
        }

        int internalLevelCount = 0;
        uint totalInternalPages = 0;

        // Current level's keys and child pointers to be packed into internal pages.
        List<DataValue> currentKeys = separatorKeys;
        List<uint> currentChildren = childPageIndexes;

        while (currentChildren.Count > 1)
        {
            List<DataValue> nextLevelKeys = new();
            List<uint> nextLevelChildren = new();

            int keyIndex = 0;

            while (keyIndex < currentKeys.Count)
            {
                // Determine how many keys fit in this internal page.
                int keysForThisPage = FindMaxInternalKeys(
                    currentKeys, keyIndex, currentChildren, keyIndex);

                // Build this internal page.
                DataValue[] pageKeys = new DataValue[keysForThisPage];
                uint[] pageChildren = new uint[keysForThisPage + 1];

                for (int i = 0; i < keysForThisPage; i++)
                {
                    pageKeys[i] = currentKeys[keyIndex + i];
                }

                for (int i = 0; i <= keysForThisPage; i++)
                {
                    pageChildren[i] = currentChildren[keyIndex + i];
                }

                byte[] pageBytes = BPlusTreePageCodec.EncodeInternalPage(pageKeys, pageChildren);
                output.Write(pageBytes);

                nextLevelChildren.Add(nextPageIndex);

                // The separator key promoted to the next level is the first key
                // of the NEXT group of children (i.e., the key after the last key
                // consumed by this page). If this is not the last page at this level,
                // promote that separator.
                keyIndex += keysForThisPage;

                if (keyIndex < currentKeys.Count)
                {
                    nextLevelKeys.Add(currentKeys[keyIndex]);
                    keyIndex++; // Skip the promoted key — it moves up, not into a page.
                }

                nextPageIndex++;
                totalInternalPages++;
            }

            currentKeys = nextLevelKeys;
            currentChildren = nextLevelChildren;
            internalLevelCount++;
        }

        // The last page written is the root.
        uint rootPageIndex = nextPageIndex - 1;

        return new InternalBuildResult(rootPageIndex, internalLevelCount, totalInternalPages);
    }

    /// <summary>
    /// Determines how many separator keys starting at <paramref name="startKeyIndex"/>
    /// can fit into a single internal page.
    /// </summary>
    private static int FindMaxInternalKeys(
        List<DataValue> allKeys,
        int startKeyIndex,
        List<uint> allChildren,
        int startChildIndex)
    {
        int availableKeys = allKeys.Count - startKeyIndex;

        // Try fitting all remaining keys first, then reduce if needed.
        for (int count = Math.Min(availableKeys, EstimateMaxInternalKeys()); count >= 1; count--)
        {
            DataValue[] testKeys = new DataValue[count];

            for (int i = 0; i < count; i++)
            {
                testKeys[i] = allKeys[startKeyIndex + i];
            }

            uint[] testChildren = new uint[count + 1];

            for (int i = 0; i <= count; i++)
            {
                int childIdx = startChildIndex + i;

                if (childIdx < allChildren.Count)
                {
                    testChildren[i] = allChildren[childIdx];
                }
            }

            try
            {
                BPlusTreePageCodec.EncodeInternalPage(testKeys, testChildren);
                return count;
            }
            catch (InvalidOperationException)
            {
                // Too many keys — try fewer.
            }
        }

        throw new InvalidOperationException(
            "Cannot fit even one separator key into an internal page. " +
            "Key values may be too large for the B+Tree page format.");
    }

    /// <summary>
    /// Returns a conservative upper bound on the number of separator keys
    /// that can fit in an internal page (assuming minimum-size keys).
    /// </summary>
    private static int EstimateMaxInternalKeys()
    {
        // Smallest possible key: 1 byte (kind) + 1 byte (UInt8 value) = 2 bytes.
        // Each key-child pair: 2 bytes key + 4 bytes child = 6 bytes.
        // Plus one extra child pointer: 4 bytes.
        // Capacity: (InternalPayloadCapacity - 4) / 6.
        return (BPlusTreeConstants.InternalPayloadCapacity - sizeof(uint)) / 6;
    }

    // ───────────────────────── Result types ─────────────────────────

    /// <summary>
    /// Result of the leaf page construction phase.
    /// </summary>
    private readonly record struct LeafBuildResult(
        uint LeafCount,
        long TotalEntryCount,
        List<DataValue> SeparatorKeys,
        List<uint> ChildPageIndexes);

    /// <summary>
    /// Result of the internal level construction phase.
    /// </summary>
    private readonly record struct InternalBuildResult(
        uint RootPageIndex,
        int InternalLevelCount,
        uint TotalInternalPages);
}
