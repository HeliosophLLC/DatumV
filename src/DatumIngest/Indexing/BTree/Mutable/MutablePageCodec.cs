using System.Buffers.Binary;
using System.Text;
using DatumIngest.DatumFile;
using DatumIngest.IO;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree.Mutable;

/// <summary>
/// Encodes and decodes mutable B+Tree pages. Distinct from <see cref="BPlusTreePageCodec"/>
/// because mutable pages: (1) leaves are uncompressed (splits stay simple),
/// (2) leaves carry a payload-length field rather than uncompressed/compressed sizes,
/// (3) free pages encode a next-free-page-id link inline.
/// </summary>
/// <remarks>
/// <para>Leaf page layout (8192 bytes):</para>
/// <code>
/// [PageType: 1B] [EntryCount: 2B] [Reserved: 1B]            ← common header (4B)
/// [PrevLeaf: 4B] [NextLeaf: 4B] [PayloadLength: 4B]         ← leaf header (12B)
/// [Entries: PayloadLength bytes, DataValue + Int32 + Int64] ← uncompressed
/// [Zero padding to 8192]
/// </code>
/// <para>Internal page layout (8192 bytes):</para>
/// <code>
/// [PageType: 1B] [KeyCount: 2B] [Reserved: 1B]              ← common header (4B)
/// [Key₀..Keyₙ₋₁: WriteDataValue format]                     ← separator keys
/// [Child₀..Childₙ: uint32]                                  ← KeyCount + 1 child page ids
/// [Zero padding to 8192]
/// </code>
/// <para>Free page layout (8192 bytes):</para>
/// <code>
/// [PageType: 1B] [0: 2B] [Reserved: 1B]                     ← common header (4B)
/// [NextFreePageId: 4B]                                      ← linked-list pointer
/// [Zero padding to 8192]
/// </code>
/// </remarks>
internal static class MutablePageCodec
{
    /// <summary>
    /// Encodes a leaf page into an 8 KiB buffer. The buffer is created fresh; callers
    /// own it and write it to disk at the appropriate page offset.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the entries don't fit in the leaf payload capacity. Callers should
    /// split before calling this.
    /// </exception>
    internal static byte[] EncodeLeafPage(
        ReadOnlySpan<ValueIndexEntry> entries,
        uint previousLeafPageId,
        uint nextLeafPageId)
    {
        byte[] page = new byte[MutableBPlusTreeConstants.PageSize];
        int payloadOffset = MutableBPlusTreeConstants.LeafHeaderSize;
        int written = SerializeLeafEntries(entries, page.AsSpan(payloadOffset));

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
    internal static int MeasureLeafEntries(ReadOnlySpan<ValueIndexEntry> entries)
    {
        using MemoryStream stream = new(MutableBPlusTreeConstants.LeafPayloadCapacity);
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

    private static int SerializeLeafEntries(ReadOnlySpan<ValueIndexEntry> entries, Span<byte> destination)
    {
        if (destination.Length < MutableBPlusTreeConstants.LeafPayloadCapacity)
        {
            throw new ArgumentException(
                $"Leaf payload destination must be at least {MutableBPlusTreeConstants.LeafPayloadCapacity} bytes; got {destination.Length}.",
                nameof(destination));
        }

        using MemoryStream stream = new(MutableBPlusTreeConstants.LeafPayloadCapacity);
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
    /// Decodes a leaf page from raw 8 KiB bytes.
    /// </summary>
    internal static MutableLeafPage DecodeLeafPage(byte[] pageBytes, uint pageId)
    {
        if (pageBytes.Length != MutableBPlusTreeConstants.PageSize)
        {
            throw new InvalidDataException(
                $"Page must be exactly {MutableBPlusTreeConstants.PageSize} bytes; got {pageBytes.Length}.");
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

        if (payloadLength < 0 || payloadLength > MutableBPlusTreeConstants.LeafPayloadCapacity)
        {
            throw new InvalidDataException(
                $"Invalid leaf payload length {payloadLength}; must be in [0, {MutableBPlusTreeConstants.LeafPayloadCapacity}].");
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
    /// Encodes an internal page (separator keys + child page ids) into an 8 KiB buffer.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the encoded keys + child pointers exceed the page capacity.
    /// </exception>
    internal static byte[] EncodeInternalPage(
        ReadOnlySpan<DataValue> keys,
        ReadOnlySpan<uint> childPageIds)
    {
        if (childPageIds.Length != keys.Length + 1)
        {
            throw new ArgumentException(
                $"Internal page child count ({childPageIds.Length}) must equal key count + 1 ({keys.Length + 1}).",
                nameof(childPageIds));
        }

        byte[] page = new byte[MutableBPlusTreeConstants.PageSize];

        using MemoryStream stream = new(MutableBPlusTreeConstants.PageSize);
        using BufferedWriter writer = new(stream);

        // Common header.
        writer.Write((byte)MutableBPlusTreePageType.Internal);
        writer.Write((ushort)keys.Length);
        writer.Write((byte)0);

        foreach (DataValue key in keys)
        {
            DataValueWriter.WriteDataValue(writer, key);
        }

        foreach (uint childId in childPageIds)
        {
            writer.Write(childId);
        }

        writer.Flush();
        long bytesWritten = stream.Position;

        if (bytesWritten > MutableBPlusTreeConstants.PageSize)
        {
            throw new InvalidOperationException(
                $"Internal page payload ({bytesWritten} bytes) exceeds page size " +
                $"({MutableBPlusTreeConstants.PageSize} bytes) for {keys.Length} keys.");
        }

        stream.Position = 0;
        stream.ReadExactly(page, 0, (int)bytesWritten);

        return page;
    }

    /// <summary>
    /// Returns the encoded byte size for an internal page with the given key list
    /// (key count + 1 child pointers). Serializes to a measurement stream.
    /// </summary>
    internal static int MeasureInternalPage(ReadOnlySpan<DataValue> keys)
    {
        using MemoryStream stream = new(MutableBPlusTreeConstants.PageSize);
        using BufferedWriter writer = new(stream);

        // Common header (always 4 bytes).
        writer.Write((byte)MutableBPlusTreePageType.Internal);
        writer.Write((ushort)keys.Length);
        writer.Write((byte)0);

        foreach (DataValue key in keys)
        {
            DataValueWriter.WriteDataValue(writer, key);
        }

        // Child pointers: (KeyCount + 1) × uint32. We don't have actual ids here —
        // just account for the byte count.
        for (int i = 0; i <= keys.Length; i++)
        {
            writer.Write(0u);
        }

        writer.Flush();

        return (int)stream.Position;
    }

    /// <summary>
    /// Decodes an internal page from raw 8 KiB bytes.
    /// </summary>
    internal static MutableInternalPage DecodeInternalPage(byte[] pageBytes, uint pageId)
    {
        if (pageBytes.Length != MutableBPlusTreeConstants.PageSize)
        {
            throw new InvalidDataException(
                $"Page must be exactly {MutableBPlusTreeConstants.PageSize} bytes; got {pageBytes.Length}.");
        }

        if ((MutableBPlusTreePageType)pageBytes[0] != MutableBPlusTreePageType.Internal)
        {
            throw new InvalidDataException(
                $"Expected internal page type ({MutableBPlusTreePageType.Internal}), got {(MutableBPlusTreePageType)pageBytes[0]}.");
        }

        ushort keyCount = BinaryPrimitives.ReadUInt16LittleEndian(pageBytes.AsSpan(1, 2));

        DataValue[] keys = new DataValue[keyCount];
        uint[] childPageIds = new uint[keyCount + 1];

        using MemoryStream stream = new(
            pageBytes,
            MutableBPlusTreeConstants.CommonPageHeaderSize,
            pageBytes.Length - MutableBPlusTreeConstants.CommonPageHeaderSize,
            writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        for (int i = 0; i < keyCount; i++)
        {
            keys[i] = DataValueReader.ReadDataValue(reader);
        }

        for (int i = 0; i <= keyCount; i++)
        {
            childPageIds[i] = reader.ReadUInt32();
        }

        return new MutableInternalPage(pageId, keys, childPageIds);
    }

    /// <summary>
    /// Encodes a free page (linked-list cell pointing at the next free page).
    /// </summary>
    internal static byte[] EncodeFreePage(uint nextFreePageId)
    {
        byte[] page = new byte[MutableBPlusTreeConstants.PageSize];
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
