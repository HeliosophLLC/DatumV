using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="StringArena"/> — append, retrieve, materialise, copy, and disposal.
/// </summary>
public class StringArenaTests
{
    [Fact]
    public void AppendAndRetrieveUtf8Bytes()
    {
        using StringArena arena = new();
        byte[] utf8 = Encoding.UTF8.GetBytes("hello");
        (int offset, int length) = arena.Append(utf8);

        ReadOnlySpan<byte> span = arena.GetSpan(offset, length);

        Assert.Equal(utf8, span.ToArray());
    }

    [Fact]
    public void AppendStringAndMaterialise()
    {
        using StringArena arena = new();
        (int offset, int length) = arena.Append("world");

        string result = arena.GetString(offset, length);

        Assert.Equal("world", result);
    }

    [Fact]
    public void MultipleAppendsAreContiguous()
    {
        using StringArena arena = new();
        (int offset1, int length1) = arena.Append("first");
        (int offset2, int length2) = arena.Append("second");

        Assert.Equal(0, offset1);
        Assert.Equal(length1, offset2);
        Assert.Equal("first", arena.GetString(offset1, length1));
        Assert.Equal("second", arena.GetString(offset2, length2));
    }

    [Fact]
    public void BytesWrittenTracksTotal()
    {
        using StringArena arena = new();
        Assert.Equal(0, arena.BytesWritten);

        arena.Append("abc");
        Assert.Equal(3, arena.BytesWritten);

        arena.Append("defgh");
        Assert.Equal(8, arena.BytesWritten);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        using StringArena arena = new(initialCapacity: 8);
        string longString = new('x', 1000);
        (int offset, int length) = arena.Append(longString);

        Assert.Equal(longString, arena.GetString(offset, length));
    }

    [Fact]
    public void CopyFromTransfersAllBytes()
    {
        using StringArena source = new();
        (int sourceOffset, int sourceLength) = source.Append("transferred");

        using StringArena target = new();
        target.Append("prefix");
        int baseOffset = target.CopyFrom(source);

        string result = target.GetString(baseOffset + sourceOffset, sourceLength);
        Assert.Equal("transferred", result);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        StringArena arena = new();
        arena.Append("test");
        arena.Dispose();
        arena.Dispose();
    }

    [Fact]
    public void HandlesMultibyteUtf8()
    {
        using StringArena arena = new();
        string emoji = "Hello \U0001F600 World";
        (int offset, int length) = arena.Append(emoji);

        Assert.Equal(emoji, arena.GetString(offset, length));
    }
}
