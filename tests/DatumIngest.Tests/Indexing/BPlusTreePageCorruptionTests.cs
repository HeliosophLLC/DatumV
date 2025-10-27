using System.Text;
using DatumIngest.Indexing.BTree;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Validates that <see cref="BPlusTreePageCodec"/> rejects corrupted page data
/// with clean exceptions rather than undefined behavior or crashes.
/// </summary>
public sealed class BPlusTreePageCorruptionTests : ServiceTestBase
{
    /// <summary>Standard B+Tree page size.</summary>
    private const int PageSize = 8192;

    [Fact]
    public void DecodeLeafPage_WrongPageType_ThrowsInvalidDataException()
    {
        // Mark as Internal (1) instead of Leaf (2).
        byte[] page = new byte[PageSize];
        page[0] = 1; // BPlusTreePageType.Internal

        Assert.Throws<InvalidDataException>(() => BPlusTreePageCodec.DecodeLeafPage(page, 0));
    }

    [Fact]
    public void DecodeInternalPage_WrongPageType_ThrowsInvalidDataException()
    {
        // Mark as Leaf (2) instead of Internal (1).
        byte[] page = new byte[PageSize];
        page[0] = 2; // BPlusTreePageType.Leaf

        Assert.Throws<InvalidDataException>(() => BPlusTreePageCodec.DecodeInternalPage(page, 0));
    }

    [Fact]
    public void DecodeLeafPage_UnknownPageType_ThrowsInvalidDataException()
    {
        byte[] page = new byte[PageSize];
        page[0] = 0xFF; // Unknown page type.

        Assert.Throws<InvalidDataException>(() => BPlusTreePageCodec.DecodeLeafPage(page, 0));
    }

    [Fact]
    public void DecodeInternalPage_UnknownPageType_ThrowsInvalidDataException()
    {
        byte[] page = new byte[PageSize];
        page[0] = 0xFF;

        Assert.Throws<InvalidDataException>(() => BPlusTreePageCodec.DecodeInternalPage(page, 0));
    }

    [Fact]
    public void DecodeLeafPage_CorruptedCompressedPayload_Throws()
    {
        // Valid leaf header but the compressed payload is garbage.
        byte[] page = new byte[PageSize];
        page[0] = 1; // BPlusTreePageType.Leaf
        // keyCount = 1
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), 1);
        // Skip prev/next leaf pointers (bytes 4-11).
        // uncompressedSize = 100
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(12), 100);
        // compressedSize = 50
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(16), 50);
        // Fill the "compressed" region with random garbage.
        new Random(42).NextBytes(page.AsSpan(20, 50));

        Assert.ThrowsAny<Exception>(() => BPlusTreePageCodec.DecodeLeafPage(page, 0));
    }

    [Fact]
    public void DecodeLeafPage_ZeroEntryPage_DoesNotCrash()
    {
        // A leaf page claiming zero entries with zero-length compressed payload.
        byte[] page = new byte[PageSize];
        page[0] = 1; // Leaf
        // keyCount = 0, uncompressedSize = 0, compressedSize = 0 — all zeros from the array init.

        // This should either succeed with zero entries or throw cleanly.
        // We only assert it does not crash.
        try
        {
            BPlusTreePageCodec.DecodeLeafPage(page, 0);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            // Clean exception — acceptable.
        }
    }

    [Fact]
    public void DecodeLeafPage_UndersizedArray_Throws()
    {
        // A page buffer shorter than 8192 bytes.
        byte[] page = new byte[100];
        page[0] = 1; // Leaf

        // Should throw because ReadBytes for compressed payload goes past the end.
        Assert.ThrowsAny<Exception>(() => BPlusTreePageCodec.DecodeLeafPage(page, 0));
    }

    [Fact]
    public void DecodeInternalPage_CorruptedKeyData_Throws()
    {
        // Valid internal header claiming keys exist, but the key data is garbage.
        byte[] page = new byte[PageSize];
        page[0] = 2; // Internal
        // keyCount = 5
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), 5);
        // Fill key region with garbage bytes that won't decode as valid DataValues.
        new Random(42).NextBytes(page.AsSpan(4, 200));

        Assert.ThrowsAny<Exception>(() => BPlusTreePageCodec.DecodeInternalPage(page, 0));
    }
}
