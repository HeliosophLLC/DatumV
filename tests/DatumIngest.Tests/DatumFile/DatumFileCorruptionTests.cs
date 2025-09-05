using DatumIngest.DatumFile;

namespace DatumIngest.Tests.DatumFile;

/// <summary>
/// Validates that <see cref="DatumFileReader"/> rejects corrupted <c>.datum</c> files
/// with clean exceptions rather than undefined behavior or crashes.
/// </summary>
public sealed class DatumFileCorruptionTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>Creates a temporary directory for corrupt test files.</summary>
    public DatumFileCorruptionTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(), "DatumFileCorruptionTests_" + Guid.NewGuid().ToString("N"));
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
        string path = WriteTempFile("empty.datum", []);

        Assert.ThrowsAny<Exception>(() => DatumFileReader.Open(path));
    }

    [Fact]
    public void Open_TooShortForHeader_Throws()
    {
        // Header is 28 bytes; give it 10.
        string path = WriteTempFile("short.datum", new byte[10]);

        Assert.ThrowsAny<Exception>(() => DatumFileReader.Open(path));
    }

    [Fact]
    public void Open_InvalidHeaderMagic_ThrowsInvalidDataException()
    {
        // 64 bytes of zeros — magic won't match "DTMF".
        string path = WriteTempFile("bad_magic.datum", new byte[64]);

        Assert.Throws<InvalidDataException>(() => DatumFileReader.Open(path));
    }

    [Fact]
    public void Open_InvalidTailMagic_ThrowsInvalidDataException()
    {
        // Valid header magic + version 1, but garbage tail.
        byte[] data = new byte[64];
        "DTMF"u8.CopyTo(data);
        data[4] = 1; // version = 1 (little-endian)

        string path = WriteTempFile("bad_tail.datum", data);

        Assert.Throws<InvalidDataException>(() => DatumFileReader.Open(path));
    }

    [Fact]
    public void Open_UnsupportedVersion_ThrowsNotSupportedException()
    {
        // Valid "DTMF" magic, version = 255, and valid "FMTD" tail at the end.
        byte[] data = new byte[64];
        "DTMF"u8.CopyTo(data);
        data[4] = 0xFF; // version = 255 (little-endian)
        data[5] = 0x00;

        // Write a valid tail: 4-byte footer length + "FMTD" at the last 8 bytes.
        "FMTD"u8.CopyTo(data.AsSpan(60));

        string path = WriteTempFile("bad_version.datum", data);

        Assert.Throws<NotSupportedException>(() => DatumFileReader.Open(path));
    }

    [Fact]
    public void Open_TruncatedFooter_Throws()
    {
        // Valid header and valid tail magic, but the footer length points beyond the file.
        byte[] data = new byte[64];
        "DTMF"u8.CopyTo(data);
        data[4] = 1; // version = 1

        // Tail at last 8 bytes: footerByteLength=999999 + "FMTD"
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 999999);
        "FMTD"u8.CopyTo(data.AsSpan(60));

        string path = WriteTempFile("truncated_footer.datum", data);

        // The footer offset calculation will be negative or the seek will fail.
        Assert.ThrowsAny<Exception>(() => DatumFileReader.Open(path));
    }

    [Fact]
    public void Open_AllOnesFile_ThrowsInvalidDataException()
    {
        // Stress test: a file full of 0xFF bytes.
        string path = WriteTempFile("all_ones.datum", Enumerable.Repeat((byte)0xFF, 256).ToArray());

        Assert.Throws<InvalidDataException>(() => DatumFileReader.Open(path));
    }

    private string WriteTempFile(string name, byte[] content)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
