using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="ByteArrayValueStore"/> — the in-memory <see cref="IValueStore"/>
/// implementation. Mirrors the coverage of <c>StringArenaTests</c> / <c>DataArenaTests</c>
/// but drives the store through the <see cref="IValueStore"/> surface so the same shape of
/// asserts also validates the interface contract.
/// </summary>
public class ByteArrayValueStoreTests
{
    [Fact]
    public void StoreAndRetrieveString()
    {
        IValueStore store = new ByteArrayValueStore();
        var (p0, p1) = store.StoreString("world");

        Assert.Equal("world", store.RetrieveString(p0, p1));
    }

    [Fact]
    public void StoreAndRetrieveUtf8Bytes()
    {
        IValueStore store = new ByteArrayValueStore();
        byte[] utf8 = Encoding.UTF8.GetBytes("hello");
        var (p0, p1) = store.StoreUtf8(utf8);

        Assert.Equal(utf8, store.RetrieveUtf8Span(p0, p1).ToArray());
    }

    [Fact]
    public void StoreCharsRoundTripsThroughString()
    {
        IValueStore store = new ByteArrayValueStore();
        var (p0, p1) = store.StoreChars("café".AsSpan());

        Assert.Equal("café", store.RetrieveString(p0, p1));
    }

    [Fact]
    public void HandlesMultibyteUtf8()
    {
        IValueStore store = new ByteArrayValueStore();
        string emoji = "Hello \U0001F600 World";
        var (p0, p1) = store.StoreString(emoji);

        Assert.Equal(emoji, store.RetrieveString(p0, p1));
    }

    [Fact]
    public void MultipleStringsAreContiguous()
    {
        var store = new ByteArrayValueStore();
        var (off1, len1) = store.StoreString("first");
        var (off2, _) = store.StoreString("second");

        Assert.Equal(0, off1.Value);
        Assert.Equal(len1.Value, off2.Value);
    }

    [Fact]
    public void StoreAndRetrieveBytes()
    {
        IValueStore store = new ByteArrayValueStore();
        byte[] data = [0xFF, 0x00, 0xAB, 0xCD];
        var (p0, p1) = store.StoreBytes(data);

        byte[] retrieved = store.RetrieveBytes(p0, p1);
        Assert.Equal(data, retrieved);
        Assert.NotSame(data, retrieved);
    }

    [Fact]
    public void StoreAndRetrieveFloats()
    {
        IValueStore store = new ByteArrayValueStore();
        float[] values = [1.0f, 2.5f, 3.75f, float.NaN, float.PositiveInfinity];
        var (p0, p1) = store.StoreFloats(values);

        float[] retrieved = store.RetrieveFloats(p0, p1);
        Assert.Equal(values, retrieved);
        Assert.NotSame(values, retrieved);
    }

    [Fact]
    public void StoreAndRetrieveTensor()
    {
        IValueStore store = new ByteArrayValueStore();
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        int[] shape = [2, 3];
        var (p0, p1) = store.StoreTensor(data, shape);

        float[] retrieved = store.RetrieveTensor(p0, p1, out int[] retrievedShape);

        Assert.Equal(shape, retrievedShape);
        Assert.Equal(data, retrieved);
    }

    [Fact]
    public void StoreAndRetrieveDataValues()
    {
        IValueStore store = new ByteArrayValueStore();
        DataValue[] values = [DataValue.FromInt32(7), DataValue.FromInt64(42), DataValue.FromInt8(-1)];
        var (p0, p1) = store.StoreDataValues(values);

        DataValue[] retrieved = store.RetrieveDataValues(p0, p1);
        Assert.Equal(values, retrieved);
    }

    [Fact]
    public void StoreAndRetrieveObject()
    {
        IValueStore store = new ByteArrayValueStore();
        var sentinel = new object();
        var (p0, p1) = store.StoreObject(sentinel);

        Assert.Same(sentinel, store.RetrieveObject(p0, p1));
        Assert.Equal(ArenaLength.Zero, p1);
    }

    [Fact]
    public void RetrieveObjectThrowsOnUnknownIndex()
    {
        IValueStore store = new ByteArrayValueStore();
        Assert.Throws<InvalidOperationException>(() => store.RetrieveObject(new ArenaOffset(0), ArenaLength.Zero));
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        var store = new ByteArrayValueStore(initialCapacity: 8);
        string longString = new('x', 1000);
        var (p0, p1) = store.StoreString(longString);

        Assert.True(store.Capacity >= 1000);
        Assert.Equal(longString, store.RetrieveString(p0, p1));
    }

    [Fact]
    public void GrowPreservesEarlierWrites()
    {
        var store = new ByteArrayValueStore(initialCapacity: 16);
        var (earlyP0, earlyP1) = store.StoreString("early");

        // Force at least one growth.
        store.StoreBytes(new byte[2048]);

        Assert.Equal("early", store.RetrieveString(earlyP0, earlyP1));
    }

    [Fact]
    public void BytesWrittenTracksTotal()
    {
        var store = new ByteArrayValueStore();
        Assert.Equal(0, store.BytesWritten);

        store.StoreFloats([1.0f, 2.0f]);
        Assert.Equal(8, store.BytesWritten);

        store.StoreBytes([0xFF, 0x00]);
        Assert.Equal(10, store.BytesWritten);
    }
}
