using DatumIngest.Indexing;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Validates that <see cref="MappedSortedIndexReader"/> rejects corrupted
/// <c>.datum-mapped-index</c> files with clean exceptions rather than
/// undefined behavior or crashes.
/// </summary>
public sealed class MappedSortedIndexCorruptionTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>Creates a temporary directory for corrupt test files.</summary>
    public MappedSortedIndexCorruptionTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(), "MappedSortedIndexCorruptionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Open_EmptyFile_Throws()
    {
        string path = WriteTempFile("empty.idx4", []);

        // MemoryMappedFile.CreateFromFile on a zero-length file throws.
        Assert.ThrowsAny<Exception>(() => MappedSortedIndexReader.Open(path));
    }

    [Fact]
    public void Open_TooShortForHeader_Throws()
    {
        // Header needs at least 12 bytes (magic 4 + version 4 + column count 4).
        string path = WriteTempFile("short.idx4", new byte[8]);

        Assert.ThrowsAny<Exception>(() => MappedSortedIndexReader.Open(path));
    }

    [Fact]
    public void Open_InvalidMagic_ThrowsInvalidDataException()
    {
        string path = WriteTempFile("bad_magic.idx4", new byte[64]);

        Assert.Throws<InvalidDataException>(() => MappedSortedIndexReader.Open(path));
    }

    [Fact]
    public void Open_UnsupportedVersion_ThrowsInvalidDataException()
    {
        byte[] data = new byte[64];
        "DXIX"u8.CopyTo(data);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 99); // version 99

        string path = WriteTempFile("bad_version.idx4", data);

        Assert.Throws<InvalidDataException>(() => MappedSortedIndexReader.Open(path));
    }

    [Fact]
    public void Open_NegativeColumnCount_Throws()
    {
        byte[] data = new byte[64];
        "DXIX"u8.CopyTo(data);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 4); // version 4
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1); // column count = -1

        string path = WriteTempFile("negative_count.idx4", data);

        Assert.ThrowsAny<Exception>(() => MappedSortedIndexReader.Open(path));
    }

    [Fact]
    public void Open_TruncatedColumnDirectory_Throws()
    {
        // Valid header claiming 100 columns but file ends immediately after.
        byte[] data = new byte[16];
        "DXIX"u8.CopyTo(data);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 100); // 100 columns
        // Only 4 extra bytes — not enough for even one column directory entry.

        string path = WriteTempFile("truncated_dir.idx4", data);

        Assert.ThrowsAny<Exception>(() => MappedSortedIndexReader.Open(path));
    }

    [Fact]
    public void Open_AllOnesFile_ThrowsInvalidDataException()
    {
        string path = WriteTempFile("all_ones.idx4", Enumerable.Repeat((byte)0xFF, 128).ToArray());

        Assert.Throws<InvalidDataException>(() => MappedSortedIndexReader.Open(path));
    }

    private string WriteTempFile(string name, byte[] content)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
