using System.IO.Compression;
using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for transparent gzip decompression across CSV, JSONL, and JSON providers.
/// Verifies that gzip-compressed files produce the same schema and data as their
/// uncompressed equivalents when the descriptor carries <see cref="CompressionKind.Gzip"/>.
/// </summary>
public sealed class GzipProviderTests : IDisposable
{
    private readonly string _fixtureDirectory =
        Path.Combine(Path.GetTempPath(), $"GzipProviderTests-{Guid.NewGuid():N}");

    public GzipProviderTests()
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

    private string WriteGzipFile(string fileName, string content)
    {
        string path = Path.Combine(_fixtureDirectory, fileName);
        using FileStream fileStream = File.Create(path);
        using GZipStream gzipStream = new(fileStream, CompressionLevel.Fastest);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        gzipStream.Write(bytes);
        return path;
    }

    private string WriteTextFile(string fileName, string content)
    {
        string path = Path.Combine(_fixtureDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static TableDescriptor GzipDescriptor(string provider, string filePath)
    {
        return new TableDescriptor(provider, "test", filePath, new Dictionary<string, string>(), CompressionKind.Gzip);
    }

    private static async Task<List<Row>> ReadAllAsync(IAsyncEnumerable<RowBatch> source)
    {
        return await source.CollectRowsAsync();
    }

    // ──────────────────────────────────────────────
    //  CSV provider — gzip
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CsvProvider_GzipFile_InfersSchemaCorrectly()
    {
        string csvContent = "name,age,score\nAlice,30,95.5\nBob,25,87.3\n";
        string path = WriteGzipFile("data.csv.gz", csvContent);

        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(GzipDescriptor("csv", path), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[0].Name);
        Assert.Equal("age", schema.Columns[1].Name);
        Assert.Equal("score", schema.Columns[2].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal(DataKind.Int32, schema.Columns[1].Kind);
        Assert.Equal(DataKind.Float64, schema.Columns[2].Kind);
    }

    [Fact]
    public async Task CsvProvider_GzipFile_ReadsAllRows()
    {
        string csvContent = "name,age\nAlice,30\nBob,25\nCarol,35\n";
        string path = WriteGzipFile("data.csv.gz", csvContent);

        CsvTableProvider provider = new();
        TableDescriptor descriptor = GzipDescriptor("csv", path);
        List<Row> rows = await ReadAllAsync(provider.OpenAsync(descriptor, null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal("Bob", rows[1]["name"].AsString());
        Assert.Equal("Carol", rows[2]["name"].AsString());
    }

    [Fact]
    public async Task CsvProvider_GzipFile_CapabilitiesReportNoSeek()
    {
        string csvContent = "name,age\nAlice,30\n";
        string path = WriteGzipFile("data.csv.gz", csvContent);

        CsvTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            GzipDescriptor("csv", path), CancellationToken.None);

        Assert.False(capabilities.SupportsSeek);
        Assert.Null(capabilities.EstimatedRowCount);
    }

    [Fact]
    public async Task CsvProvider_GzipTsv_DetectsTabDelimiter()
    {
        string tsvContent = "name\tage\nAlice\t30\nBob\t25\n";
        string path = WriteGzipFile("data.tsv.gz", tsvContent);

        CsvTableProvider provider = new();
        // The .tsv extension is inside .gz — delimiter detection should
        // see the .tsv extension after stripping .gz and use tab.
        TableDescriptor descriptor = GzipDescriptor("csv", path);
        Schema schema = await provider.GetSchemaAsync(descriptor, CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[0].Name);
        Assert.Equal("age", schema.Columns[1].Name);
    }

    // ──────────────────────────────────────────────
    //  JSONL provider — gzip
    // ──────────────────────────────────────────────

    [Fact]
    public async Task JsonlProvider_GzipFile_InfersSchemaCorrectly()
    {
        string jsonlContent = "{\"name\":\"Alice\",\"age\":30}\n{\"name\":\"Bob\",\"age\":25}\n";
        string path = WriteGzipFile("data.jsonl.gz", jsonlContent);

        JsonlTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(GzipDescriptor("jsonl", path), CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Contains(schema.Columns, column => column.Name == "name");
        Assert.Contains(schema.Columns, column => column.Name == "age");
    }

    [Fact]
    public async Task JsonlProvider_GzipFile_ReadsAllRows()
    {
        string jsonlContent = "{\"x\":1}\n{\"x\":2}\n{\"x\":3}\n";
        string path = WriteGzipFile("data.jsonl.gz", jsonlContent);

        JsonlTableProvider provider = new();
        TableDescriptor descriptor = GzipDescriptor("jsonl", path);
        List<Row> rows = await ReadAllAsync(provider.OpenAsync(descriptor, null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task JsonlProvider_GzipFile_CapabilitiesReportNoSeek()
    {
        string jsonlContent = "{\"x\":1}\n";
        string path = WriteGzipFile("data.jsonl.gz", jsonlContent);

        JsonlTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            GzipDescriptor("jsonl", path), CancellationToken.None);

        Assert.False(capabilities.SupportsSeek);
    }

    // ──────────────────────────────────────────────
    //  JSON provider — gzip
    // ──────────────────────────────────────────────

    [Fact]
    public async Task JsonProvider_GzipFile_InfersSchemaCorrectly()
    {
        string jsonContent = "[{\"name\":\"Alice\",\"age\":30},{\"name\":\"Bob\",\"age\":25}]";
        string path = WriteGzipFile("data.json.gz", jsonContent);

        JsonTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(GzipDescriptor("json", path), CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Contains(schema.Columns, column => column.Name == "name");
        Assert.Contains(schema.Columns, column => column.Name == "age");
    }

    [Fact]
    public async Task JsonProvider_GzipFile_ReadsAllRows()
    {
        string jsonContent = "[{\"value\":1},{\"value\":2},{\"value\":3}]";
        string path = WriteGzipFile("data.json.gz", jsonContent);

        JsonTableProvider provider = new();
        TableDescriptor descriptor = GzipDescriptor("json", path);
        List<Row> rows = await ReadAllAsync(provider.OpenAsync(descriptor, null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    // ──────────────────────────────────────────────
    //  GzipFileDecompressor
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GzipFileDecompressor_DecompressesCorrectly()
    {
        string originalContent = "hello, gzip world!\nline two\n";
        string gzipPath = WriteGzipFile("test.csv.gz", originalContent);

        using GzipFileDecompressor decompressor = await GzipFileDecompressor.DecompressAsync(
            gzipPath, CancellationToken.None);

        string decompressedContent = await File.ReadAllTextAsync(decompressor.DecompressedFilePath);
        Assert.Equal(originalContent, decompressedContent);
    }

    [Fact]
    public async Task GzipFileDecompressor_DeletesTempFileOnDispose()
    {
        string content = "temporary data";
        string gzipPath = WriteGzipFile("disposable.csv.gz", content);

        string tempPath;
        using (GzipFileDecompressor decompressor = await GzipFileDecompressor.DecompressAsync(
            gzipPath, CancellationToken.None))
        {
            tempPath = decompressor.DecompressedFilePath;
            Assert.True(File.Exists(tempPath));
        }

        Assert.False(File.Exists(tempPath));
    }

    // ──────────────────────────────────────────────
    //  CompressionStreamFactory
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CompressionStreamFactory_OpenRead_DecompressesGzip()
    {
        string content = "stream factory test content\n";
        string gzipPath = WriteGzipFile("factory.csv.gz", content);

        TableDescriptor descriptor = GzipDescriptor("csv", gzipPath);
        using Stream stream = CompressionStreamFactory.OpenRead(descriptor);
        using StreamReader reader = new(stream);
        string result = await reader.ReadToEndAsync();

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task CompressionStreamFactory_OpenRead_ReturnsRawStreamWhenNoCompression()
    {
        string content = "plain text";
        string path = WriteTextFile("plain.csv", content);

        TableDescriptor descriptor = new("csv", "test", path, new Dictionary<string, string>());
        using Stream stream = CompressionStreamFactory.OpenRead(descriptor);
        using StreamReader reader = new(stream);
        string result = await reader.ReadToEndAsync();

        Assert.Equal(content, result);
    }

    // ──────────────────────────────────────────────
    //  TableCatalog — gzip integration
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TableCatalog_RegisterGzipCsv_ResolvesAndReadsData()
    {
        string csvContent = "id,value\n1,hello\n2,world\n";
        string path = WriteGzipFile("catalog_test.csv.gz", csvContent);

        using TableCatalog catalog = new();
        catalog.Register("test_table", path);

        TableDescriptor descriptor = catalog.Resolve("test_table");
        Assert.Equal("csv", descriptor.Provider);
        Assert.Equal(CompressionKind.Gzip, descriptor.Compression);

        Schema schema = await catalog.GetSchemaAsync("test_table", CancellationToken.None);
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal("value", schema.Columns[1].Name);
    }
}
