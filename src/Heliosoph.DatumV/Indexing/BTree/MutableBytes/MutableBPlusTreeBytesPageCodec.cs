using System.Buffers.Binary;
using Heliosoph.DatumV.Indexing.BTree.Mutable;

namespace Heliosoph.DatumV.Indexing.BTree.MutableBytes;

/// <summary>
/// Encodes and decodes bytes-keyed B+Tree pages. Page size is per-tree
/// (configured by the caller's <see cref="PageGeometry"/>); the difference
/// from the typed tree is the key encoding inside leaf entries and internal
/// separators — variable-length byte arrays with a 4-byte length prefix
/// instead of typed <c>DataValue</c> serialization.
/// </summary>
/// <remarks>
/// <para>Leaf page layout (PageSize bytes):</para>
/// <code>
/// [PageType: 1B] [EntryCount: 2B] [Reserved: 1B]              ← common header (4B)
/// [PrevLeaf: 4B] [NextLeaf: 4B] [PayloadLength: 4B]           ← leaf header (12B)
/// [Entries: each [KeyLen: 4B][KeyBytes][ChunkIdx: 4B][RowOff: 8B]]
/// [Zero padding to PageSize]
/// </code>
/// <para>Internal page layout (PageSize bytes):</para>
/// <code>
/// [PageType: 1B] [SepCount: 2B] [Reserved: 1B]                ← common header (4B)
/// [SepCount × [KeyLen: 4B][KeyBytes][ChunkIdx: 4B][RowOff: 8B]] ← composite separators
/// [SepCount + 1 × uint32]                                     ← child page ids
/// [Zero padding to PageSize]
/// </code>
/// <para>Each separator is a full composite (Key, ChunkIndex, RowOffsetInChunk) — the
/// first composite of the right-child leaf at the time of split. See
/// <see cref="MutableBytesInternalPage"/> for the rationale.</para>
/// </remarks>
internal static class MutableBPlusTreeBytesPageCodec
{
    /// <summary>Encoded byte overhead for a single leaf entry (excluding key length).</summary>
    private const int LeafEntryFixedOverhead = 4 /* keyLen */ + 4 /* chunkIdx */ + 8 /* rowOff */;

    /// <summary>Encoded byte overhead for a single internal-page composite separator (excluding key length).</summary>
    private const int InternalSeparatorFixedOverhead = 4 /* keyLen */ + 4 /* chunkIdx */ + 8 /* rowOff */;

    /// <summary>
    /// Encodes a leaf page into a fresh page-size buffer. Callers own the
    /// buffer and write it to disk at the appropriate page offset.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the entries don't fit in the leaf payload capacity.
    /// Callers should split before reaching this codec.
    /// </exception>
    internal static byte[] EncodeLeafPage(
        PageGeometry geom,
        ReadOnlySpan<BytesIndexEntry> entries,
        uint previousLeafPageId,
        uint nextLeafPageId)
    {
        byte[] page = new byte[geom.PageSize];
        Span<byte> payload = page.AsSpan(MutableBPlusTreeConstants.LeafHeaderSize);

        int written = SerializeLeafEntries(entries, payload);

        page[0] = (byte)MutableBPlusTreePageType.Leaf;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1, 2), (ushort)entries.Length);
        page[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4, 4), previousLeafPageId);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8, 4), nextLeafPageId);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(12, 4), written);

        return page;
    }

    /// <summary>
    /// Returns the encoded byte size for a list of leaf entries. Used by
    /// the writer to test split boundaries without committing a buffer.
    /// </summary>
    internal static int MeasureLeafEntries(ReadOnlySpan<BytesIndexEntry> entries)
    {
        int total = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            total += LeafEntryFixedOverhead + entries[i].Key.Length;
        }
        return total;
    }

    private static int SerializeLeafEntries(ReadOnlySpan<BytesIndexEntry> entries, Span<byte> destination)
    {
        int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            BytesIndexEntry entry = entries[i];
            int total = LeafEntryFixedOverhead + entry.Key.Length;
            if (offset + total > destination.Length)
            {
                throw new InvalidOperationException(
                    $"Leaf payload ({offset + total} bytes) exceeds page capacity ({destination.Length} bytes).");
            }

            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), entry.Key.Length);
            offset += 4;
            entry.Key.CopyTo(destination[offset..]);
            offset += entry.Key.Length;
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), entry.ChunkIndex);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, 8), entry.RowOffsetInChunk);
            offset += 8;
        }
        return offset;
    }

    /// <summary>Decodes a leaf page from raw page-size bytes.</summary>
    internal static MutableBytesLeafPage DecodeLeafPage(PageGeometry geom, byte[] pageBytes, uint pageId)
    {
        if (pageBytes.Length != geom.PageSize)
        {
            throw new InvalidDataException(
                $"Page must be exactly {geom.PageSize} bytes; got {pageBytes.Length}.");
        }

        if ((MutableBPlusTreePageType)pageBytes[0] != MutableBPlusTreePageType.Leaf)
        {
            throw new InvalidDataException(
                $"Expected leaf page type ({MutableBPlusTreePageType.Leaf}), got {(MutableBPlusTreePageType)pageBytes[0]}.");
        }

        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(pageBytes.AsSpan(1, 2));
        uint prevLeaf = BinaryPrimitives.ReadUInt32LittleEndian(pageBytes.AsSpan(4, 4));
        uint nextLeaf = BinaryPrimitives.ReadUInt32LittleEndian(pageBytes.AsSpan(8, 4));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(pageBytes.AsSpan(12, 4));

        if (payloadLength < 0 || payloadLength > geom.LeafPayloadCapacity)
        {
            throw new InvalidDataException(
                $"Invalid leaf payload length {payloadLength}; must be in [0, {geom.LeafPayloadCapacity}].");
        }

        BytesIndexEntry[] entries = new BytesIndexEntry[entryCount];
        ReadOnlySpan<byte> payload = pageBytes.AsSpan(MutableBPlusTreeConstants.LeafHeaderSize, payloadLength);
        int offset = 0;
        for (int i = 0; i < entryCount; i++)
        {
            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            byte[] key = payload.Slice(offset, keyLen).ToArray();
            offset += keyLen;
            int chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            long rowOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, 8));
            offset += 8;
            entries[i] = new BytesIndexEntry(key, chunkIndex, rowOffset);
        }

        return new MutableBytesLeafPage(pageId, entries, prevLeaf, nextLeaf);
    }

    /// <summary>Encodes an internal page (composite separators + child page ids).</summary>
    internal static byte[] EncodeInternalPage(
        PageGeometry geom,
        ReadOnlySpan<BytesIndexEntry> separators,
        ReadOnlySpan<uint> childPageIds)
    {
        if (childPageIds.Length != separators.Length + 1)
        {
            throw new ArgumentException(
                $"Internal page child count ({childPageIds.Length}) must equal separator count + 1 ({separators.Length + 1}).",
                nameof(childPageIds));
        }

        byte[] page = new byte[geom.PageSize];

        page[0] = (byte)MutableBPlusTreePageType.Internal;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1, 2), (ushort)separators.Length);
        page[3] = 0;

        int offset = MutableBPlusTreeConstants.CommonPageHeaderSize;

        for (int i = 0; i < separators.Length; i++)
        {
            BytesIndexEntry sep = separators[i];
            int needed = InternalSeparatorFixedOverhead + sep.Key.Length;
            if (offset + needed > geom.PageSize)
            {
                throw new InvalidOperationException(
                    $"Internal page payload ({offset + needed} bytes) exceeds page size ({geom.PageSize} bytes).");
            }
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(offset, 4), sep.Key.Length);
            offset += 4;
            sep.Key.CopyTo(page.AsSpan(offset));
            offset += sep.Key.Length;
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(offset, 4), sep.ChunkIndex);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(offset, 8), sep.RowOffsetInChunk);
            offset += 8;
        }

        int childrenBytes = childPageIds.Length * 4;
        if (offset + childrenBytes > geom.PageSize)
        {
            throw new InvalidOperationException(
                $"Internal page payload ({offset + childrenBytes} bytes) exceeds page size ({geom.PageSize} bytes).");
        }

        for (int i = 0; i < childPageIds.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset, 4), childPageIds[i]);
            offset += 4;
        }

        return page;
    }

    /// <summary>
    /// Returns the encoded byte size for an internal page with the given
    /// separator list (separator count + 1 child pointers). Includes the
    /// common header.
    /// </summary>
    internal static int MeasureInternalPage(ReadOnlySpan<BytesIndexEntry> separators)
    {
        int total = MutableBPlusTreeConstants.CommonPageHeaderSize;
        for (int i = 0; i < separators.Length; i++)
        {
            total += InternalSeparatorFixedOverhead + separators[i].Key.Length;
        }
        total += (separators.Length + 1) * 4; // child ids
        return total;
    }

    /// <summary>Decodes an internal page from raw page-size bytes.</summary>
    internal static MutableBytesInternalPage DecodeInternalPage(PageGeometry geom, byte[] pageBytes, uint pageId)
    {
        if (pageBytes.Length != geom.PageSize)
        {
            throw new InvalidDataException(
                $"Page must be exactly {geom.PageSize} bytes; got {pageBytes.Length}.");
        }

        if ((MutableBPlusTreePageType)pageBytes[0] != MutableBPlusTreePageType.Internal)
        {
            throw new InvalidDataException(
                $"Expected internal page type ({MutableBPlusTreePageType.Internal}), got {(MutableBPlusTreePageType)pageBytes[0]}.");
        }

        ushort sepCount = BinaryPrimitives.ReadUInt16LittleEndian(pageBytes.AsSpan(1, 2));

        BytesIndexEntry[] separators = new BytesIndexEntry[sepCount];
        uint[] childPageIds = new uint[sepCount + 1];

        int offset = MutableBPlusTreeConstants.CommonPageHeaderSize;
        for (int i = 0; i < sepCount; i++)
        {
            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(pageBytes.AsSpan(offset, 4));
            offset += 4;
            byte[] key = pageBytes.AsSpan(offset, keyLen).ToArray();
            offset += keyLen;
            int chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(pageBytes.AsSpan(offset, 4));
            offset += 4;
            long rowOffset = BinaryPrimitives.ReadInt64LittleEndian(pageBytes.AsSpan(offset, 8));
            offset += 8;
            separators[i] = new BytesIndexEntry(key, chunkIndex, rowOffset);
        }

        for (int i = 0; i <= sepCount; i++)
        {
            childPageIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(pageBytes.AsSpan(offset, 4));
            offset += 4;
        }

        return new MutableBytesInternalPage(pageId, separators, childPageIds);
    }
}
