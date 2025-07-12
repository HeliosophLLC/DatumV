using DatumIngest.Indexing;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="ZipDirectoryCache"/> and <see cref="ZipDirectoryEntry"/>.
/// </summary>
public sealed class ZipDirectoryCacheTests
{
    [Fact]
    public void Constructor_PreservesEntries()
    {
        ZipDirectoryEntry[] entries =
        [
            new("file1.csv", CompressedSize: 100, UncompressedSize: 500, LocalHeaderOffset: 0, Crc32: 0x12345678),
            new("file2.csv", CompressedSize: 200, UncompressedSize: 1000, LocalHeaderOffset: 500, Crc32: 0xDEADBEEF),
        ];

        ZipDirectoryCache cache = new(entries);

        Assert.Equal(2, cache.Count);
        Assert.Equal("file1.csv", cache.Entries[0].FileName);
        Assert.Equal(100, cache.Entries[0].CompressedSize);
        Assert.Equal(500, cache.Entries[0].UncompressedSize);
        Assert.Equal(0, cache.Entries[0].LocalHeaderOffset);
        Assert.Equal(0x12345678u, cache.Entries[0].Crc32);
    }

    [Fact]
    public void EmptyCache_HasZeroCount()
    {
        ZipDirectoryCache cache = new(Array.Empty<ZipDirectoryEntry>());

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ZipDirectoryEntry_RecordEquality()
    {
        ZipDirectoryEntry entry1 = new("test.csv", 100, 500, 0, 0x12345678);
        ZipDirectoryEntry entry2 = new("test.csv", 100, 500, 0, 0x12345678);
        ZipDirectoryEntry entry3 = new("other.csv", 100, 500, 0, 0x12345678);

        Assert.Equal(entry1, entry2);
        Assert.NotEqual(entry1, entry3);
    }

    [Fact]
    public void BuildFromArchive_CreatesCache()
    {
        // Create a minimal ZIP archive in memory.
        using System.IO.MemoryStream stream = new();
        using (System.IO.Compression.ZipArchive archive =
            new(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            System.IO.Compression.ZipArchiveEntry entry = archive.CreateEntry("data/test.txt");
            using System.IO.StreamWriter writer = new(entry.Open());
            writer.Write("hello world");
        }

        stream.Position = 0;
        using System.IO.Compression.ZipArchive readArchive =
            new(stream, System.IO.Compression.ZipArchiveMode.Read);

        ZipDirectoryCache cache = ZipDirectoryCache.BuildFromArchive(readArchive);

        Assert.Equal(1, cache.Count);
        Assert.Equal("data/test.txt", cache.Entries[0].FileName);
        Assert.True(cache.Entries[0].UncompressedSize > 0);
    }
}
