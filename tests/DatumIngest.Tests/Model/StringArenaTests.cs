using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="Arena"/> string operations — append, retrieve, materialise, copy, and disposal.
/// </summary>
public class StringArenaTests : ServiceTestBase
{
    [Fact]
    public void AppendAndRetrieveUtf8Bytes()
    {
        using Arena arena = new();
        byte[] utf8 = Encoding.UTF8.GetBytes("hello");
        (long offset, int length) = arena.AppendUtf8(utf8);

        ReadOnlySpan<byte> span = arena.GetSpan(offset, length);

        Assert.Equal(utf8, span.ToArray());
    }

    [Fact]
    public void AppendStringAndMaterialise()
    {
        using Arena arena = new();
        (long offset, int length) = arena.AppendString("world");

        string result = arena.GetString(offset, length);

        Assert.Equal("world", result);
    }

    [Fact]
    public void MultipleAppendsAreContiguous()
    {
        using Arena arena = new();
        (long offset1, int length1) = arena.AppendString("first");
        (long offset2, int length2) = arena.AppendString("second");

        Assert.Equal(0, offset1);
        Assert.Equal(length1, offset2);
        Assert.Equal("first", arena.GetString(offset1, length1));
        Assert.Equal("second", arena.GetString(offset2, length2));
    }

    [Fact]
    public void BytesWrittenTracksTotal()
    {
        using Arena arena = new();
        Assert.Equal(0, arena.BytesWritten);

        arena.AppendString("abc");
        Assert.Equal(3, arena.BytesWritten);

        arena.AppendString("defgh");
        Assert.Equal(8, arena.BytesWritten);
    }

    [Fact]
    public void GrowsBeyondInitialCapacity()
    {
        using Arena arena = new(initialCapacity: 8);
        string longString = new('x', 1000);
        (long offset, int length) = arena.AppendString(longString);

        Assert.Equal(longString, arena.GetString(offset, length));
    }

    [Fact]
    public void CopyFromTransfersAllBytes()
    {
        using Arena source = new();
        (long sourceOffset, int sourceLength) = source.AppendString("transferred");

        using Arena target = new();
        target.AppendString("prefix");
        long baseOffset = target.CopyFrom(source);

        string result = target.GetString(baseOffset + sourceOffset, sourceLength);
        Assert.Equal("transferred", result);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        Arena arena = new();
        arena.AppendString("test");
        arena.Dispose();
        arena.Dispose();
    }

    [Fact]
    public void HandlesMultibyteUtf8()
    {
        using Arena arena = new();
        string emoji = "Hello \U0001F600 World";
        (long offset, int length) = arena.AppendString(emoji);

        Assert.Equal(emoji, arena.GetString(offset, length));
    }
}
