using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Compression;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree;

/// <summary>
/// Encodes and decodes B+Tree pages to and from their on-disk 8 KiB representation.
/// Leaf pages use Zstd compression for the entry payload. Internal pages store
/// keys and child pointers uncompressed.
/// </summary>
/// <remarks>
/// <para>
/// Leaf page on-disk layout (8192 bytes total):
/// <code>
/// [PageType: 1B] [KeyCount: 2B] [Reserved: 1B]                  ← common header (4B)
/// [PrevLeaf: 4B] [NextLeaf: 4B] [UncompressedSize: 4B] [CompressedSize: 4B]  ← leaf header (16B)
/// [CompressedPayload: up to LeafPayloadCapacity bytes]           ← Zstd-compressed entries
/// [Zero padding to 8192 bytes]
/// </code>
/// </para>
/// <para>
/// Internal page on-disk layout (8192 bytes total):
/// <code>
/// [PageType: 1B] [KeyCount: 2B] [Reserved: 1B]                  ← common header (4B)
/// [Key₀..Keyₙ₋₁: WriteDataValue format]                         ← separator keys
/// [Child₀..Childₙ: uint32]                                      ← child page indexes
/// [Zero padding to 8192 bytes]
/// </code>
/// </para>
/// </remarks>
internal static class BPlusTreePageCodec
{
    /// <summary>
    /// Encodes leaf entries into a fixed-size page byte array.
    /// The entries are serialized to binary, Zstd-compressed, and packed into
    /// an 8 KiB page with the common and leaf headers.
    /// </summary>
    /// <param name="entries">Sorted entries to encode.</param>
    /// <param name="previousLeafPageIndex">Page index of the previous leaf.</param>
    /// <param name="nextLeafPageIndex">Page index of the next leaf.</param>
    /// <returns>An 8 KiB byte array containing the encoded leaf page.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the compressed payload exceeds the page capacity.
    /// </exception>
    internal static byte[] EncodeLeafPage(
        ReadOnlySpan<ValueIndexEntry> entries,
        uint previousLeafPageIndex,
        uint nextLeafPageIndex)
    {
        // Serialize entries into a rented buffer via BufferedIndexWriter.
        int estimatedSize = entries.Length * 17; // ~17 bytes per Int32 entry (kind + key + chunk + offset).
        byte[] rentedEntryBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(estimatedSize, 256));
        int uncompressedLength;

        try
        {
            using (MemoryStream entryStream = new(rentedEntryBuffer, 0, rentedEntryBuffer.Length, writable: true))
            {
                // MemoryStream over a fixed array throws NotSupportedException if
                // the data exceeds the buffer. Fall back to the growable path if that happens.
                try
                {
                    using BufferedIndexWriter entryWriter = new(entryStream);

                    foreach (ValueIndexEntry entry in entries)
                    {
                        IndexWriter.WriteDataValue(entryWriter, entry.Key);
                        entryWriter.Write(entry.ChunkIndex);
                        entryWriter.Write(entry.RowOffsetInChunk);
                    }

                    entryWriter.Flush();
                    uncompressedLength = (int)entryStream.Position;
                }
                catch (NotSupportedException)
                {
                    // Buffer was too small — fall back to growable serialization.
                    // Do not return rentedEntryBuffer here; the outer finally block
                    // always returns it. Returning here would double-return the buffer
                    // and poison the ArrayPool (two callers sharing the same array).
                    return EncodeLeafPageFallback(entries, previousLeafPageIndex, nextLeafPageIndex);
                }
            }

            // Compress directly into the page array at the payload offset.
            byte[] page = new byte[BPlusTreeConstants.PageSize];
            int payloadOffset = BPlusTreeConstants.PageHeaderSize + LeafHeaderSize;

            int compressedLength = DatumCompressor.CompressZstdInto(
                rentedEntryBuffer.AsSpan(0, uncompressedLength),
                page,
                payloadOffset);

            if (compressedLength > BPlusTreeConstants.LeafPayloadCapacity)
            {
                throw new InvalidOperationException(
                    $"Compressed leaf payload ({compressedLength} bytes) exceeds page capacity " +
                    $"({BPlusTreeConstants.LeafPayloadCapacity} bytes) for {entries.Length} entries.");
            }

            // Write headers directly via BinaryPrimitives — no MemoryStream/BinaryWriter needed.
            // Common header: [PageType: 1B] [KeyCount: 2B] [Reserved: 1B]
            page[0] = (byte)BPlusTreePageType.Leaf;
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), (ushort)entries.Length);
            page[3] = 0; // Reserved.

            // Leaf header: [PrevLeaf: 4B] [NextLeaf: 4B] [UncompressedSize: 4B] [CompressedSize: 4B]
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), previousLeafPageIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), nextLeafPageIndex);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(12), uncompressedLength);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(16), compressedLength);

            return page;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedEntryBuffer);
        }
    }

    /// <summary>Leaf header size in bytes: PrevLeaf (4) + NextLeaf (4) + UncompressedSize (4) + CompressedSize (4).</summary>
    private const int LeafHeaderSize = 16;

    /// <summary>
    /// Fallback path for pages where the rented entry buffer was too small.
    /// Uses a growable MemoryStream. Rarely hit in practice — only for pages
    /// with very large string or variable-length keys.
    /// </summary>
    private static byte[] EncodeLeafPageFallback(
        ReadOnlySpan<ValueIndexEntry> entries,
        uint previousLeafPageIndex,
        uint nextLeafPageIndex)
    {
        byte[] uncompressed;

        using (MemoryStream buffer = new())
        {
            using BufferedIndexWriter writer = new(buffer);

            foreach (ValueIndexEntry entry in entries)
            {
                IndexWriter.WriteDataValue(writer, entry.Key);
                writer.Write(entry.ChunkIndex);
                writer.Write(entry.RowOffsetInChunk);
            }

            writer.Flush();
            uncompressed = buffer.ToArray();
        }

        byte[] page = new byte[BPlusTreeConstants.PageSize];
        int payloadOffset = BPlusTreeConstants.PageHeaderSize + LeafHeaderSize;

        int compressedLength = DatumCompressor.CompressZstdInto(
            uncompressed,
            page,
            payloadOffset);

        if (compressedLength > BPlusTreeConstants.LeafPayloadCapacity)
        {
            throw new InvalidOperationException(
                $"Compressed leaf payload ({compressedLength} bytes) exceeds page capacity " +
                $"({BPlusTreeConstants.LeafPayloadCapacity} bytes) for {entries.Length} entries.");
        }

        page[0] = (byte)BPlusTreePageType.Leaf;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), (ushort)entries.Length);
        page[3] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), previousLeafPageIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), nextLeafPageIndex);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(12), uncompressed.Length);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(16), compressedLength);

        return page;
    }

    /// <summary>
    /// Decodes a leaf page from its on-disk byte representation.
    /// </summary>
    /// <param name="pageData">Raw 8 KiB page bytes.</param>
    /// <param name="pageIndex">Zero-based page index (used for constructing the page object).</param>
    /// <returns>The deserialized leaf page with decompressed entries.</returns>
    internal static BPlusTreeLeafPage DecodeLeafPage(byte[] pageData, uint pageIndex)
    {
        using MemoryStream stream = new(pageData);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        // Common header.
        BPlusTreePageType pageType = (BPlusTreePageType)reader.ReadByte();

        if (pageType != BPlusTreePageType.Leaf)
        {
            throw new InvalidDataException(
                $"Expected leaf page type ({BPlusTreePageType.Leaf}), got {pageType}.");
        }

        ushort entryCount = reader.ReadUInt16();
        _ = reader.ReadByte(); // Reserved.

        // Leaf header.
        uint previousLeaf = reader.ReadUInt32();
        uint nextLeaf = reader.ReadUInt32();
        int uncompressedSize = reader.ReadInt32();
        int compressedSize = reader.ReadInt32();

        // Read compressed payload.
        byte[] compressed = reader.ReadBytes(compressedSize);
        byte[] decompressed = DatumCompressor.Decompress(compressed, uncompressedSize, DatumCompression.Zstd);

        // Deserialize entries from decompressed buffer.
        ValueIndexEntry[] entries = DeserializeLeafEntries(decompressed, entryCount);

        return new BPlusTreeLeafPage(pageIndex, entries, previousLeaf, nextLeaf);
    }

    /// <summary>
    /// Encodes an internal page (separator keys + child pointers) into a fixed-size page byte array.
    /// Internal pages are not compressed.
    /// </summary>
    /// <param name="keys">Separator keys in ascending order.</param>
    /// <param name="childPageIndexes">Child page indexes (<c>keys.Length + 1</c> elements).</param>
    /// <returns>An 8 KiB byte array containing the encoded internal page.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the serialized keys and child pointers exceed the page capacity.
    /// </exception>
    internal static byte[] EncodeInternalPage(
        ReadOnlySpan<DataValue> keys,
        ReadOnlySpan<uint> childPageIndexes)
    {
        // Use a growable stream so that oversized payloads are detected by the
        // size check below rather than causing a NotSupportedException from a
        // fixed-capacity MemoryStream. FindMaxInternalKeys relies on catching
        // InvalidOperationException to probe the maximum key count per page.
        using MemoryStream stream = new();
        using BufferedIndexWriter writer = new(stream);

        // Common header.
        writer.Write((byte)BPlusTreePageType.Internal);
        writer.Write((ushort)keys.Length);
        writer.Write((byte)0); // Reserved.

        // Keys (using the same serialization as IndexWriter).
        foreach (DataValue key in keys)
        {
            IndexWriter.WriteDataValue(writer, key);
        }

        // Child page indexes.
        foreach (uint childIndex in childPageIndexes)
        {
            writer.Write(childIndex);
        }

        writer.Flush();

        long bytesWritten = stream.Position;

        if (bytesWritten > BPlusTreeConstants.PageSize)
        {
            throw new InvalidOperationException(
                $"Internal page payload ({bytesWritten} bytes) exceeds page size " +
                $"({BPlusTreeConstants.PageSize} bytes) for {keys.Length} keys.");
        }

        byte[] page = new byte[BPlusTreeConstants.PageSize];
        stream.Position = 0;
        stream.ReadExactly(page, 0, (int)bytesWritten);

        return page;
    }

    /// <summary>
    /// Decodes an internal page from its on-disk byte representation.
    /// </summary>
    /// <param name="pageData">Raw 8 KiB page bytes.</param>
    /// <param name="pageIndex">Zero-based page index (used for constructing the page object).</param>
    /// <returns>The deserialized internal page.</returns>
    internal static BPlusTreeInternalPage DecodeInternalPage(byte[] pageData, uint pageIndex)
    {
        using MemoryStream stream = new(pageData);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        // Common header.
        BPlusTreePageType pageType = (BPlusTreePageType)reader.ReadByte();

        if (pageType != BPlusTreePageType.Internal)
        {
            throw new InvalidDataException(
                $"Expected internal page type ({BPlusTreePageType.Internal}), got {pageType}.");
        }

        ushort keyCount = reader.ReadUInt16();
        _ = reader.ReadByte(); // Reserved.

        // Keys.
        DataValue[] keys = new DataValue[keyCount];

        for (int index = 0; index < keyCount; index++)
        {
            keys[index] = IndexReader.ReadDataValue(reader);
        }

        // Child page indexes (keyCount + 1).
        uint[] childPageIndexes = new uint[keyCount + 1];

        for (int index = 0; index <= keyCount; index++)
        {
            childPageIndexes[index] = reader.ReadUInt32();
        }

        return new BPlusTreeInternalPage(pageIndex, keys, childPageIndexes);
    }

    /// <summary>
    /// Reads only the page type byte from raw page data without fully decoding the page.
    /// </summary>
    internal static BPlusTreePageType ReadPageType(byte[] pageData)
    {
        return (BPlusTreePageType)pageData[0];
    }

    /// <summary>
    /// Deserializes leaf entries from a decompressed byte buffer.
    /// </summary>
    private static ValueIndexEntry[] DeserializeLeafEntries(byte[] buffer, int entryCount)
    {
        using MemoryStream stream = new(buffer);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        ValueIndexEntry[] entries = new ValueIndexEntry[entryCount];

        for (int index = 0; index < entryCount; index++)
        {
            DataValue key = IndexReader.ReadDataValue(reader);
            int chunkIndex = reader.ReadInt32();
            long rowOffsetInChunk = reader.ReadInt64();
            entries[index] = new ValueIndexEntry(key, chunkIndex, rowOffsetInChunk);
        }

        return entries;
    }
}
