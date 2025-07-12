using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="JsonlTableProvider.MeasureChunkByteRangesAsync"/>,
/// verifying byte-level chunk boundaries for JSONL files with blank lines,
/// non-object lines, and varying chunk sizes.
/// </summary>
public sealed class JsonlChunkMeasurementTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonlChunkMeasurementTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"datum-jsonl-measure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string WriteTempFile(string fileName, string content)
    {
        string path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static TableDescriptor CreateDescriptor(string filePath)
    {
        return new TableDescriptor("jsonl", "test", filePath, new Dictionary<string, string>());
    }

    // ───────────────────── Basic measurement ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_SimpleFile_ProducesSingleChunk()
    {
        string content = "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n";
        string path = WriteTempFile("simple.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(3, ranges[0].RowCount);
        Assert.Equal(0, ranges[0].ByteOffset);
        Assert.Equal(content.Length, ranges[0].ByteLength);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_MultipleChunks_SplitsCorrectly()
    {
        string content = "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n{\"a\":4}\n{\"a\":5}\n";
        string path = WriteTempFile("multi.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 2, CancellationToken.None);

        Assert.Equal(3, ranges.Count);
        Assert.Equal(2, ranges[0].RowCount);
        Assert.Equal(2, ranges[1].RowCount);
        Assert.Equal(1, ranges[2].RowCount);

        // First chunk starts at 0.
        Assert.Equal(0, ranges[0].ByteOffset);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_NoTrailingNewline_CountsLastRow()
    {
        string content = "{\"a\":1}\n{\"a\":2}";
        string path = WriteTempFile("no-newline.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(2, ranges[0].RowCount);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_EmptyFile_ProducesNoChunks()
    {
        string path = WriteTempFile("empty.jsonl", "");
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Empty(ranges);
    }

    // ───────────────────── Blank line handling ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_BlankLines_SkippedInRowCount()
    {
        string content = "{\"a\":1}\n\n{\"a\":2}\n\n{\"a\":3}\n";
        string path = WriteTempFile("blanks.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(3, ranges[0].RowCount);
        // Byte range covers the entire file including blank lines.
        Assert.Equal(content.Length, ranges[0].ByteLength);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_BlankLinesOnly_ProducesNoChunks()
    {
        string content = "\n\n\n";
        string path = WriteTempFile("blanks-only.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Empty(ranges);
    }

    // ───────────────────── Non-object lines ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_NonObjectLines_SkippedInRowCount()
    {
        // Array and string lines should not be counted as data rows.
        string content = "[1,2,3]\n{\"a\":1}\n\"hello\"\n{\"a\":2}\n";
        string path = WriteTempFile("mixed.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(2, ranges[0].RowCount);
    }

    // ───────────────────── CRLF handling ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_CrLfLineEndings_CountsCorrectly()
    {
        string content = "{\"a\":1}\r\n{\"a\":2}\r\n";
        string path = WriteTempFile("crlf.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(2, ranges[0].RowCount);
    }

    // ───────────────────── Alignment with provider ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_RowCountMatchesProvider()
    {
        string content = "{\"id\":1}\n{\"id\":2}\n\n{\"id\":3}\n{\"id\":4}\n{\"id\":5}\n";
        string path = WriteTempFile("alignment.jsonl", content);
        JsonlTableProvider provider = new();
        TableDescriptor descriptor = CreateDescriptor(path);

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            descriptor, chunkSize: 2, CancellationToken.None);

        int providerRowCount = 0;
        await foreach (Row _ in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            providerRowCount++;
        }

        long measuredTotalRows = ranges.Sum(r => r.RowCount);
        Assert.Equal(providerRowCount, measuredTotalRows);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_ByteRangesCoverEntireFile()
    {
        string content = "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n{\"a\":4}\n";
        string path = WriteTempFile("coverage.jsonl", content);
        JsonlTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 2, CancellationToken.None);

        long totalCoveredBytes = ranges.Sum(r => r.ByteLength);
        Assert.Equal(content.Length, totalCoveredBytes);
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReportsSupportsSeekTrue()
    {
        string path = WriteTempFile("cap.jsonl", "{\"a\":1}\n");
        JsonlTableProvider provider = new();

        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            CreateDescriptor(path), CancellationToken.None);

        Assert.True(capabilities.SupportsSeek);
    }
}
