using DatumIngest.Catalog;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="FileFormatDetector.DetectProvider"/>.
/// Covers extension mapping, MNIST-style filename patterns,
/// magic byte detection, and the null fallback.
/// </summary>
public sealed class FileFormatDetectorTests : IDisposable
{
    private readonly string _fixtureDirectory =
        Path.Combine(Path.GetTempPath(), $"FileFormatDetectorTests-{Guid.NewGuid():N}");

    public FileFormatDetectorTests()
    {
        Directory.CreateDirectory(_fixtureDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_fixtureDirectory))
        {
            Directory.Delete(_fixtureDirectory, recursive: true);
        }
    }

    private string FixturePath(string fileName)
    {
        return Path.Combine(_fixtureDirectory, fileName);
    }

    private string WriteFile(string fileName, byte[] content)
    {
        string path = FixturePath(fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ──────────────────────────────────────────────
    //  Tier 1: Extension mapping
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("data.csv", "csv")]
    [InlineData("data.tsv", "csv")]
    [InlineData("data.json", "json")]
    [InlineData("data.jsonl", "jsonl")]
    [InlineData("data.ndjson", "jsonl")]
    [InlineData("data.parquet", "parquet")]
    [InlineData("data.pq", "parquet")]
    [InlineData("data.hdf5", "hdf5")]
    [InlineData("data.h5", "hdf5")]
    [InlineData("data.hdf", "hdf5")]
    [InlineData("data.zip", "zip")]
    [InlineData("data.idx", "idx")]
    public void DetectProvider_KnownExtension_ReturnsProvider(string fileName, string expectedProvider)
    {
        string? result = FileFormatDetector.DetectProvider(fileName);

        Assert.Equal(expectedProvider, result);
    }

    [Theory]
    [InlineData("DATA.CSV", "csv")]
    [InlineData("ARCHIVE.ZIP", "zip")]
    [InlineData("File.Parquet", "parquet")]
    public void DetectProvider_CaseInsensitiveExtension_ReturnsProvider(string fileName, string expectedProvider)
    {
        string? result = FileFormatDetector.DetectProvider(fileName);

        Assert.Equal(expectedProvider, result);
    }

    // ──────────────────────────────────────────────
    //  Tier 2: MNIST-style IDX filename patterns
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("train-images-idx3-ubyte")]
    [InlineData("train-labels-idx1-ubyte")]
    [InlineData("t10k-images-idx3-ubyte")]
    [InlineData("t10k-labels-idx1-ubyte")]
    [InlineData("data.idx2-ubyte")]
    [InlineData("tensors.idx4-ubyte")]
    public void DetectProvider_IdxFilenamePattern_ReturnsIdx(string fileName)
    {
        string? result = FileFormatDetector.DetectProvider(fileName);

        Assert.Equal("idx", result);
    }

    [Theory]
    [InlineData("train-images.IDX3-UBYTE")]
    [InlineData("labels.Idx1-Ubyte")]
    public void DetectProvider_IdxFilenameCaseInsensitive_ReturnsIdx(string fileName)
    {
        string? result = FileFormatDetector.DetectProvider(fileName);

        Assert.Equal("idx", result);
    }

    // ──────────────────────────────────────────────
    //  Tier 3: Magic byte detection
    // ──────────────────────────────────────────────

    [Fact]
    public void DetectProvider_ParquetMagicBytes_ReturnsParquet()
    {
        string path = WriteFile("unknown_format", "PAR1"u8.ToArray());

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("parquet", result);
    }

    [Fact]
    public void DetectProvider_Hdf5MagicBytes_ReturnsHdf5()
    {
        byte[] header = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];
        string path = WriteFile("unknown_format", header);

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("hdf5", result);
    }

    [Fact]
    public void DetectProvider_ZipMagicBytes_ReturnsZip()
    {
        byte[] header = [(byte)'P', (byte)'K', 0x03, 0x04];
        string path = WriteFile("unknown_format", header);

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("zip", result);
    }

    [Fact]
    public void DetectProvider_IdxMagicBytes_ReturnsIdx()
    {
        // Valid IDX header: 0x00, 0x00, type=0x08 (uint8), dims=1
        byte[] header = [0x00, 0x00, 0x08, 0x01];
        string path = WriteFile("unknown_format", header);

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("idx", result);
    }

    [Fact]
    public void DetectProvider_JsonObjectMagicBytes_ReturnsJson()
    {
        string path = WriteFile("unknown_format", "{ \"key\": 1 }"u8.ToArray());

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("json", result);
    }

    [Fact]
    public void DetectProvider_JsonArrayMagicBytes_ReturnsJson()
    {
        string path = WriteFile("unknown_format", "[{\"a\":1}]"u8.ToArray());

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("json", result);
    }

    [Fact]
    public void DetectProvider_JsonWithLeadingWhitespace_ReturnsJson()
    {
        string path = WriteFile("unknown_format", "  \n { \"a\": 1 }"u8.ToArray());

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("json", result);
    }

    // ──────────────────────────────────────────────
    //  Null fallback
    // ──────────────────────────────────────────────

    [Fact]
    public void DetectProvider_UnknownExtensionNoFile_ReturnsNull()
    {
        string? result = FileFormatDetector.DetectProvider("/nonexistent/path/data.xyz");

        Assert.Null(result);
    }

    [Fact]
    public void DetectProvider_NoExtensionNoPatternNoFile_ReturnsNull()
    {
        string? result = FileFormatDetector.DetectProvider("/nonexistent/path/data");

        Assert.Null(result);
    }

    [Fact]
    public void DetectProvider_UnrecognizedMagicBytes_ReturnsNull()
    {
        byte[] garbage = [0xFF, 0xFE, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x11];
        string path = WriteFile("unknown_format", garbage);

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Null(result);
    }

    [Fact]
    public void DetectProvider_TooShortFile_ReturnsNull()
    {
        string path = WriteFile("tiny", [0x01, 0x02]);

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Null(result);
    }

    // ──────────────────────────────────────────────
    //  Extension takes priority over magic bytes
    // ──────────────────────────────────────────────

    [Fact]
    public void DetectProvider_ExtensionOverridesMagicBytes()
    {
        // File has .csv extension but contains Parquet magic bytes.
        // Extension should win.
        string path = WriteFile("data.csv", "PAR1"u8.ToArray());

        string? result = FileFormatDetector.DetectProvider(path);

        Assert.Equal("csv", result);
    }

    // ──────────────────────────────────────────────
    //  Stream overload
    // ──────────────────────────────────────────────

    [Fact]
    public void DetectProviderStream_ParquetMagicBytes_ReturnsParquet()
    {
        using MemoryStream stream = new("PAR1"u8.ToArray());

        string? result = FileFormatDetector.DetectProvider(stream);

        Assert.Equal("parquet", result);
    }

    [Fact]
    public void DetectProviderStream_Hdf5MagicBytes_ReturnsHdf5()
    {
        byte[] header = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];
        using MemoryStream stream = new(header);

        string? result = FileFormatDetector.DetectProvider(stream);

        Assert.Equal("hdf5", result);
    }

    [Fact]
    public void DetectProviderStream_ZipMagicBytes_ReturnsZip()
    {
        byte[] header = [(byte)'P', (byte)'K', 0x03, 0x04];
        using MemoryStream stream = new(header);

        string? result = FileFormatDetector.DetectProvider(stream);

        Assert.Equal("zip", result);
    }

    [Fact]
    public void DetectProviderStream_IdxMagicBytes_ReturnsIdx()
    {
        byte[] header = [0x00, 0x00, 0x08, 0x01];
        using MemoryStream stream = new(header);

        string? result = FileFormatDetector.DetectProvider(stream);

        Assert.Equal("idx", result);
    }

    [Fact]
    public void DetectProviderStream_JsonObjectMagicBytes_ReturnsJson()
    {
        using MemoryStream stream = new("{ \"key\": 1 }"u8.ToArray());

        string? result = FileFormatDetector.DetectProvider(stream);

        Assert.Equal("json", result);
    }

    [Fact]
    public void DetectProviderStream_UnrecognizedBytes_ReturnsNull()
    {
        byte[] garbage = [0xFF, 0xFE, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x11];
        using MemoryStream stream = new(garbage);

        string? result = FileFormatDetector.DetectProvider(stream);

        Assert.Null(result);
    }

    [Fact]
    public void DetectProviderStream_TooShort_ReturnsNull()
    {
        using MemoryStream stream = new([0x01, 0x02]);

        string? result = FileFormatDetector.DetectProvider(stream);

        Assert.Null(result);
    }

    [Fact]
    public void DetectProviderStream_RestoresPosition()
    {
        using MemoryStream stream = new("PAR1extra"u8.ToArray());

        FileFormatDetector.DetectProvider(stream);

        Assert.Equal(0, stream.Position);
    }
}
