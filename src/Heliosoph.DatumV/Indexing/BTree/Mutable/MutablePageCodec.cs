using System.Buffers.Binary;
using System.Text;
using Heliosoph.DatumV.DatumFile;
using Heliosoph.DatumV.IO;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing.BTree.Mutable;

/// <summary>
/// Encodes and decodes mutable B+Tree pages. Page size is per-tree (configured by the
/// caller's <see cref="PageGeometry"/>); leaves are uncompressed (splits stay simple),
/// leaves carry a payload-length field, and free pages encode a next-free-page-id link
/// inline.
/// </summary>
/// <remarks>
/// <para>Leaf page layout (PageSize bytes):</para>
/// <code>
/// [PageType: 1B] [EntryCount: 2B] [Reserved: 1B]            ← common header (4B)
/// [PrevLeaf: 4B] [NextLeaf: 4B] [PayloadLength: 4B]         ← leaf header (12B)
/// [Entries: PayloadLength bytes, DataValue + Int32 + Int64] ← uncompressed
/// [Zero padding to PageSize]
/// </code>
/// <para>Internal page layout (PageSize bytes):</para>
/// <code>
/// [PageType: 1B] [SepCount: 2B] [Reserved: 1B]              ← common header (4B)
/// [Sep₀..Sepₙ₋₁: WriteDataValue + Int32 ChunkIdx + Int64 RowOff]
/// [Child₀..Childₙ: uint32]                                  ← SepCount + 1 child page ids
/// [Zero padding to PageSize]
/// </code>
/// <para>Each separator is a full composite (Key, ChunkIndex, RowOffsetInChunk) — the
/// first composite of the right-child leaf at the time of split. Key-only separators
/// can't disambiguate duplicate-key inserts; see <see cref="MutableInternalPage"/>.</para>
/// <para>Free page layout (PageSize bytes):</para>
/// <code>
/// [PageType: 1B] [0: 2B] [Reserved: 1B]                     ← common header (4B)
/// [NextFreePageId: 4B]                                      ← linked-list pointer
/// [Zero padding to PageSize]
/// </code>
/// </remarks>
internal static class MutablePageCodec
{
    /// <summary>
    /// Encodes a leaf page into a buffer sized to <paramref name="geom"/>. The buffer
    /// is created fresh; callers own it and write it to disk at the appropriate page
    /// offset.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the entries don't fit in the leaf payload capacity. Callers should
    /// split before calling this.
    /// </exception>
    internal static byte[] EncodeLeafPage(
        PageGeometry geom,
        ReadOnlySpan<ValueIndexEntry> entries,
        uint previousLeafPageId,
        uint nextLeafPageId)
    {
        byte[] page = new byte[geom.PageSize];
        int payloadOffset = MutableBPlusTreeConstants.LeafHeaderSize;
        int written = SerializeLeafEntries(geom, entries, page.AsSpan(payloadOffset));

        page[0] = (byte)MutableBPlusTreePageType.Leaf;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1, 2), (ushort)entries.Length);
        page[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4, 4), previousLeafPageId);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8, 4), nextLeafPageId);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(12, 4), written);

        return page;
    }

    /// <summary>
    /// Returns the number of bytes a leaf entry list would occupy on-disk by
    /// serializing to a measurement stream. Used by the writer to test split
    /// boundaries without committing to a final page buffer.
    /// </summary>
    internal static int MeasureLeafEntries(PageGeometry geom, ReadOnlySpan<ValueIndexEntry> entries)
    {
        using MemoryStream stream = new(geom.LeafPayloadCapacity);
        using BufferedWriter writer = new(stream);

        foreach (ValueIndexEntry entry in entries)
        {
            DataValueWriter.WriteDataValue(writer, entry.Key);
            writer.Write(entry.ChunkIndex);
            writer.Write(entry.RowOffsetInChunk);
        }

        writer.Flush();

        return (int)stream.Position;
    }

    private static int SerializeLeafEntries(PageGeometry geom, ReadOnlySpan<ValueIndexEntry> entries, Span<byte> destination)
    {
        if (destination.Length < geom.LeafPayloadCapacity)
        {
            throw new ArgumentException(
                $"Leaf payload destination must be at least {geom.LeafPayloadCapacity} bytes; got {destination.Length}.",
                nameof(destination));
        }

        using MemoryStream stream = new(geom.LeafPayloadCapacity);
        using BufferedWriter writer = new(stream);

        foreach (ValueIndexEntry entry in entries)
        {
            DataValueWriter.WriteDataValue(writer, entry.Key);
            writer.Write(entry.ChunkIndex);
            writer.Write(entry.RowOffsetInChunk);
        }

        writer.Flush();
        long position = stream.Position;

        if (position > destination.Length)
        {
            throw new InvalidOperationException(
                $"Leaf payload ({position} bytes) exceeds page capacity ({destination.Length} bytes).");
        }

        stream.Position = 0;
        stream.ReadExactly(destination[..(int)position]);

        return (int)position;
    }

    /// <summary>
    /// Decodes a leaf page from raw page-size bytes.
    /// </summary>
    internal static MutableLeafPage DecodeLeafPage(PageGeometry geom, byte[] pageBytes, uint pageId)
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

        ValueIndexEntry[] entries = new ValueIndexEntry[entryCount];

        if (entryCount > 0)
        {
            using MemoryStream stream = new(pageBytes, MutableBPlusTreeConstants.LeafHeaderSize, payloadLength, writable: false);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

            for (int index = 0; index < entryCount; index++)
            {
                DataValue key = DataValueReader.ReadDataValue(reader);
                int chunkIndex = reader.ReadInt32();
                long rowOffset = reader.ReadInt64();
                entries[index] = new ValueIndexEntry(key, chunkIndex, rowOffset);
            }
        }

        return new MutableLeafPage(pageId, entries, prevLeaf, nextLeaf);
    }

    /// <summary>
    /// Encodes an internal page (composite separators + child page ids) into a
    /// page-size buffer. Each separator is encoded as
    /// <c>WriteDataValue(Key) + Int32 ChunkIndex + Int64 RowOffsetInChunk</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the encoded separators + child pointers exceed the page capacity.
    /// </exception>
    internal static byte[] EncodeInternalPage(
        PageGeometry geom,
        ReadOnlySpan<ValueIndexEntry> separators,
        ReadOnlySpan<uint> childPageIds)
    {
        if (childPageIds.Length != separators.Length + 1)
        {
            throw new ArgumentException(
                $"Internal page child count ({childPageIds.Length}) must equal separator count + 1 ({separators.Length + 1}).",
                nameof(childPageIds));
        }

        byte[] page = new byte[geom.PageSize];

        using MemoryStream stream = new(geom.PageSize);
        using BufferedWriter writer = new(stream);

        // Common header.
        writer.Write((byte)MutableBPlusTreePageType.Internal);
        writer.Write((ushort)separators.Length);
        writer.Write((byte)0);

        foreach (ValueIndexEntry sep in separators)
        {
            DataValueWriter.WriteDataValue(writer, sep.Key);
            writer.Write(sep.ChunkIndex);
            writer.Write(sep.RowOffsetInChunk);
        }

        foreach (uint childId in childPageIds)
        {
            writer.Write(childId);
        }

        writer.Flush();
        long bytesWritten = stream.Position;

        if (bytesWritten > geom.PageSize)
        {
            throw new InvalidOperationException(
                $"Internal page payload ({bytesWritten} bytes) exceeds page size " +
                $"({geom.PageSize} bytes) for {separators.Length} separators.");
        }

        stream.Position = 0;
        stream.ReadExactly(page, 0, (int)bytesWritten);

        return page;
    }

    /// <summary>
    /// Returns the encoded byte size for an internal page with the given separator
    /// list (separator count + 1 child pointers). Serializes to a measurement stream.
    /// </summary>
    internal static int MeasureInternalPage(PageGeometry geom, ReadOnlySpan<ValueIndexEntry> separators)
    {
        using MemoryStream stream = new(geom.PageSize);
        using BufferedWriter writer = new(stream);

        // Common header (always 4 bytes).
        writer.Write((byte)MutableBPlusTreePageType.Internal);
        writer.Write((ushort)separators.Length);
        writer.Write((byte)0);

        foreach (ValueIndexEntry sep in separators)
        {
            DataValueWriter.WriteDataValue(writer, sep.Key);
            writer.Write(sep.ChunkIndex);
            writer.Write(sep.RowOffsetInChunk);
        }

        // Child pointers: (SeparatorCount + 1) × uint32. We don't have actual ids
        // here — just account for the byte count.
        for (int i = 0; i <= separators.Length; i++)
        {
            writer.Write(0u);
        }

        writer.Flush();

        return (int)stream.Position;
    }

    /// <summary>
    /// Decodes an internal page from raw page-size bytes.
    /// </summary>
    internal static MutableInternalPage DecodeInternalPage(PageGeometry geom, byte[] pageBytes, uint pageId)
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

        ValueIndexEntry[] separators = new ValueIndexEntry[sepCount];
        uint[] childPageIds = new uint[sepCount + 1];

        using MemoryStream stream = new(
            pageBytes,
            MutableBPlusTreeConstants.CommonPageHeaderSize,
            pageBytes.Length - MutableBPlusTreeConstants.CommonPageHeaderSize,
            writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        for (int i = 0; i < sepCount; i++)
        {
            DataValue key = DataValueReader.ReadDataValue(reader);
            int chunkIndex = reader.ReadInt32();
            long rowOffset = reader.ReadInt64();
            separators[i] = new ValueIndexEntry(key, chunkIndex, rowOffset);
        }

        for (int i = 0; i <= sepCount; i++)
        {
            childPageIds[i] = reader.ReadUInt32();
        }

        return new MutableInternalPage(pageId, separators, childPageIds);
    }

    /// <summary>
    /// Encodes a free page (linked-list cell pointing at the next free page).
    /// </summary>
    internal static byte[] EncodeFreePage(PageGeometry geom, uint nextFreePageId)
    {
        byte[] page = new byte[geom.PageSize];
        page[0] = (byte)MutableBPlusTreePageType.Free;
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4, 4), nextFreePageId);

        return page;
    }

    /// <summary>
    /// Reads the next-free-page-id from a free page's raw bytes.
    /// </summary>
    internal static uint DecodeFreePageNext(byte[] pageBytes)
    {
        if ((MutableBPlusTreePageType)pageBytes[0] != MutableBPlusTreePageType.Free)
        {
            throw new InvalidDataException(
                $"Expected free page type ({MutableBPlusTreePageType.Free}), got {(MutableBPlusTreePageType)pageBytes[0]}.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(pageBytes.AsSpan(4, 4));
    }

    /// <summary>
    /// Returns the page type from the first byte without further parsing.
    /// </summary>
    internal static MutableBPlusTreePageType ReadPageType(byte[] pageBytes) =>
        (MutableBPlusTreePageType)pageBytes[0];
}
