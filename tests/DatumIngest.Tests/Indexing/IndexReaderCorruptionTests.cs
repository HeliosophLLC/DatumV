using DatumIngest.Indexing;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Validates that <see cref="IndexReader"/> rejects corrupted <c>.datum-index</c> streams
/// with clean exceptions rather than undefined behavior or crashes.
/// </summary>
public sealed class IndexReaderCorruptionTests
{
    [Fact]
    public void Read_EmptyStream_ThrowsInvalidDataException()
    {
        using MemoryStream stream = new([]);

        IndexReader reader = new();
        Assert.Throws<InvalidDataException>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_TooShortForHeader_ThrowsInvalidDataException()
    {
        // Index header is 16 bytes; give it 8.
        using MemoryStream stream = new(new byte[8]);

        IndexReader reader = new();
        Assert.Throws<InvalidDataException>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_InvalidMagic_ThrowsInvalidDataException()
    {
        using MemoryStream stream = new(new byte[16]);

        IndexReader reader = new();
        Assert.Throws<InvalidDataException>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_UnsupportedVersion_ThrowsInvalidDataException()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write("DTIX"u8);
        writer.Write((ushort)99);
        writer.Write((ushort)0); // flags
        writer.Write(0L); // TOC offset
        writer.Flush();

        stream.Position = 0;
        IndexReader reader = new();
        Assert.Throws<InvalidDataException>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_TocOffsetBeyondStream_Throws()
    {
        // Valid header with a TOC offset pointing past the end of the stream.
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write("DTIX"u8);
        writer.Write((ushort)3); // Current version.
        writer.Write((ushort)0);
        writer.Write(999999L); // TOC offset way beyond.
        writer.Flush();

        stream.Position = 0;
        IndexReader reader = new();
        Assert.ThrowsAny<Exception>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_NegativeSectionCount_Throws()
    {
        // Valid header, TOC offset points to position 16, then writes a negative section count.
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write("DTIX"u8);
        writer.Write((ushort)3);
        writer.Write((ushort)0);
        writer.Write(16L); // TOC starts right after header.

        // Write a very large section count (interpreted as int32 by ReadInt32).
        writer.Write(int.MaxValue);
        writer.Flush();

        stream.Position = 0;
        IndexReader reader = new();
        Assert.ThrowsAny<Exception>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_TruncatedTocEntries_Throws()
    {
        // Valid header, TOC says 10 sections but the stream ends after 0 entries.
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write("DTIX"u8);
        writer.Write((ushort)3);
        writer.Write((ushort)0);
        writer.Write(16L); // TOC at offset 16.

        // sectionCount = 10, but no section data follows.
        writer.Write(10);
        writer.Flush();

        stream.Position = 0;
        IndexReader reader = new();
        Assert.ThrowsAny<Exception>(() => reader.Read(stream));
    }

    [Fact]
    public void Read_AllOnesStream_ThrowsInvalidDataException()
    {
        using MemoryStream stream = new(Enumerable.Repeat((byte)0xFF, 64).ToArray());

        IndexReader reader = new();
        Assert.Throws<InvalidDataException>(() => reader.Read(stream));
    }
}
