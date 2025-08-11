using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BPlusTreePageCodec"/> — leaf and internal page
/// encoding/decoding round-trips plus structural invariants.
/// </summary>
public sealed class BPlusTreePageCodecTests
{
    // ───────────────────────── Leaf page round-trips ─────────────────────────

    [Fact]
    public void LeafPage_SingleEntry_RoundTrips()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(42.0f), 0, 100L),
        ];

        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);

        Assert.Equal(BPlusTreeConstants.PageSize, encoded.Length);

        BPlusTreeLeafPage decoded = BPlusTreePageCodec.DecodeLeafPage(encoded, 7);

        Assert.Equal(7u, decoded.PageIndex);
        Assert.Equal(1, decoded.EntryCount);
        Assert.Equal(BPlusTreeConstants.NoLinkedPage, decoded.PreviousLeafPageIndex);
        Assert.Equal(BPlusTreeConstants.NoLinkedPage, decoded.NextLeafPageIndex);

        AssertEntryEqual(entries[0], decoded.GetEntry(0));
    }

    [Fact]
    public void LeafPage_MultipleEntries_PreservesOrder()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0L),
            new(DataValue.FromScalar(2.0f), 0, 10L),
            new(DataValue.FromScalar(3.0f), 1, 0L),
            new(DataValue.FromScalar(4.0f), 1, 10L),
            new(DataValue.FromScalar(5.0f), 2, 0L),
        ];

        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(entries, 0, 2);
        BPlusTreeLeafPage decoded = BPlusTreePageCodec.DecodeLeafPage(encoded, 1);

        Assert.Equal(5, decoded.EntryCount);
        Assert.Equal(0u, decoded.PreviousLeafPageIndex);
        Assert.Equal(2u, decoded.NextLeafPageIndex);

        for (int index = 0; index < entries.Length; index++)
        {
            AssertEntryEqual(entries[index], decoded.GetEntry(index));
        }
    }

    [Fact]
    public void LeafPage_StringKeys_RoundTrip()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromString("alpha"), 0, 0L),
            new(DataValue.FromString("beta"), 0, 1L),
            new(DataValue.FromString("gamma"), 1, 0L),
        ];

        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);
        BPlusTreeLeafPage decoded = BPlusTreePageCodec.DecodeLeafPage(encoded, 0);

        Assert.Equal(3, decoded.EntryCount);

        for (int index = 0; index < entries.Length; index++)
        {
            AssertEntryEqual(entries[index], decoded.GetEntry(index));
        }
    }

    [Fact]
    public void LeafPage_DateKeys_RoundTrip()
    {
        DateOnly date1 = new(2024, 1, 15);
        DateOnly date2 = new(2024, 6, 30);

        ValueIndexEntry[] entries =
        [
            new(DataValue.FromDate(date1), 0, 0L),
            new(DataValue.FromDate(date2), 1, 0L),
        ];

        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);
        BPlusTreeLeafPage decoded = BPlusTreePageCodec.DecodeLeafPage(encoded, 0);

        Assert.Equal(2, decoded.EntryCount);

        for (int index = 0; index < entries.Length; index++)
        {
            AssertEntryEqual(entries[index], decoded.GetEntry(index));
        }
    }

    [Fact]
    public void LeafPage_UInt8Keys_RoundTrip()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromUInt8(0), 0, 0L),
            new(DataValue.FromUInt8(127), 0, 1L),
            new(DataValue.FromUInt8(255), 1, 0L),
        ];

        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);
        BPlusTreeLeafPage decoded = BPlusTreePageCodec.DecodeLeafPage(encoded, 0);

        Assert.Equal(3, decoded.EntryCount);

        for (int index = 0; index < entries.Length; index++)
        {
            AssertEntryEqual(entries[index], decoded.GetEntry(index));
        }
    }

    [Fact]
    public void LeafPage_DuplicateKeys_AllPreserved()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(7.0f), 0, 0L),
            new(DataValue.FromScalar(7.0f), 0, 10L),
            new(DataValue.FromScalar(7.0f), 1, 0L),
        ];

        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);
        BPlusTreeLeafPage decoded = BPlusTreePageCodec.DecodeLeafPage(encoded, 0);

        Assert.Equal(3, decoded.EntryCount);

        for (int index = 0; index < entries.Length; index++)
        {
            AssertEntryEqual(entries[index], decoded.GetEntry(index));
        }
    }

    [Fact]
    public void LeafPage_PageSizeAlways8KiB()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0L),
        ];

        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);

        Assert.Equal(8192, encoded.Length);
    }

    [Fact]
    public void LeafPage_WrongPageType_Throws()
    {
        // Encode an internal page and try to decode it as a leaf.
        DataValue[] keys = [DataValue.FromScalar(5.0f)];
        uint[] children = [0, 1];

        byte[] internalPage = BPlusTreePageCodec.EncodeInternalPage(keys, children);

        Assert.Throws<InvalidDataException>(
            () => BPlusTreePageCodec.DecodeLeafPage(internalPage, 0));
    }

    // ───────────────────────── Internal page round-trips ─────────────────────────

    [Fact]
    public void InternalPage_SingleKey_RoundTrips()
    {
        DataValue[] keys = [DataValue.FromScalar(50.0f)];
        uint[] children = [0, 1];

        byte[] encoded = BPlusTreePageCodec.EncodeInternalPage(keys, children);
        BPlusTreeInternalPage decoded = BPlusTreePageCodec.DecodeInternalPage(encoded, 3);

        Assert.Equal(3u, decoded.PageIndex);
        Assert.Equal(1, decoded.KeyCount);
        Assert.Equal(2, decoded.ChildCount);

        Assert.Equal(DataValue.FromScalar(50.0f), decoded.GetKey(0));
        Assert.Equal(0u, decoded.GetChildPageIndex(0));
        Assert.Equal(1u, decoded.GetChildPageIndex(1));
    }

    [Fact]
    public void InternalPage_MultipleKeys_PreservesOrder()
    {
        DataValue[] keys =
        [
            DataValue.FromScalar(10.0f),
            DataValue.FromScalar(20.0f),
            DataValue.FromScalar(30.0f),
        ];
        uint[] children = [0, 1, 2, 3];

        byte[] encoded = BPlusTreePageCodec.EncodeInternalPage(keys, children);
        BPlusTreeInternalPage decoded = BPlusTreePageCodec.DecodeInternalPage(encoded, 0);

        Assert.Equal(3, decoded.KeyCount);
        Assert.Equal(4, decoded.ChildCount);

        for (int index = 0; index < keys.Length; index++)
        {
            Assert.Equal(keys[index], decoded.GetKey(index));
        }

        for (int index = 0; index < children.Length; index++)
        {
            Assert.Equal(children[index], decoded.GetChildPageIndex(index));
        }
    }

    [Fact]
    public void InternalPage_StringKeys_RoundTrip()
    {
        DataValue[] keys =
        [
            DataValue.FromString("delta"),
            DataValue.FromString("echo"),
        ];
        uint[] children = [10, 20, 30];

        byte[] encoded = BPlusTreePageCodec.EncodeInternalPage(keys, children);
        BPlusTreeInternalPage decoded = BPlusTreePageCodec.DecodeInternalPage(encoded, 0);

        Assert.Equal(2, decoded.KeyCount);

        for (int index = 0; index < keys.Length; index++)
        {
            Assert.Equal(keys[index], decoded.GetKey(index));
        }
    }

    [Fact]
    public void InternalPage_PageSizeAlways8KiB()
    {
        DataValue[] keys = [DataValue.FromScalar(1.0f)];
        uint[] children = [0, 1];

        byte[] encoded = BPlusTreePageCodec.EncodeInternalPage(keys, children);

        Assert.Equal(8192, encoded.Length);
    }

    [Fact]
    public void InternalPage_WrongPageType_Throws()
    {
        ValueIndexEntry[] entries = [new(DataValue.FromScalar(1.0f), 0, 0L)];

        byte[] leafPage = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);

        Assert.Throws<InvalidDataException>(
            () => BPlusTreePageCodec.DecodeInternalPage(leafPage, 0));
    }

    // ───────────────────────── ReadPageType ─────────────────────────

    [Fact]
    public void ReadPageType_Leaf_ReturnsLeaf()
    {
        ValueIndexEntry[] entries = [new(DataValue.FromScalar(1.0f), 0, 0L)];
        byte[] encoded = BPlusTreePageCodec.EncodeLeafPage(
            entries, BPlusTreeConstants.NoLinkedPage, BPlusTreeConstants.NoLinkedPage);

        Assert.Equal(BPlusTreePageType.Leaf, BPlusTreePageCodec.ReadPageType(encoded));
    }

    [Fact]
    public void ReadPageType_Internal_ReturnsInternal()
    {
        DataValue[] keys = [DataValue.FromScalar(1.0f)];
        uint[] children = [0, 1];
        byte[] encoded = BPlusTreePageCodec.EncodeInternalPage(keys, children);

        Assert.Equal(BPlusTreePageType.Internal, BPlusTreePageCodec.ReadPageType(encoded));
    }

    // ───────────────────────── Overflow regression ─────────────────────────

    /// <summary>
    /// Regression: <see cref="BPlusTreePageCodec.EncodeInternalPage"/> previously used a
    /// non-expandable <see cref="MemoryStream"/> backed by a fixed 8 KiB array. When keys
    /// were large enough to overflow the page, the stream threw
    /// <see cref="NotSupportedException"/> instead of <see cref="InvalidOperationException"/>,
    /// causing <c>FindMaxInternalKeys</c> to miss the catch and crash the index build.
    /// </summary>
    [Fact]
    public void EncodeInternalPage_OversizedKeys_ThrowsInvalidOperationException()
    {
        // A single string key larger than the 8 KiB page capacity guarantees overflow.
        string longKey = new('x', 8192);
        DataValue[] keys = [DataValue.FromString(longKey)];
        uint[] children = [0, 1];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => BPlusTreePageCodec.EncodeInternalPage(keys, children));

        Assert.Contains("exceeds page size", exception.Message);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static void AssertEntryEqual(ValueIndexEntry expected, ValueIndexEntry actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.ChunkIndex, actual.ChunkIndex);
        Assert.Equal(expected.RowOffsetInChunk, actual.RowOffsetInChunk);
    }
}
