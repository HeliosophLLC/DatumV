using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="CsvTableProvider.MeasureChunkByteRangesAsync"/>,
/// verifying byte-level chunk boundaries for CSV files with various
/// structural features (multi-line quoted fields, custom delimiters,
/// trailing newlines, etc.).
/// </summary>
public sealed class CsvChunkMeasurementTests : IDisposable
{
    private readonly string _tempDirectory;

    public CsvChunkMeasurementTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"datum-csv-measure-{Guid.NewGuid():N}");
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
        return new TableDescriptor("csv", "test", filePath, new Dictionary<string, string>
        {
            ["header"] = "true"
        });
    }

    // ───────────────────── Basic measurement ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_SimpleFile_ProducesSingleChunk()
    {
        string content = "name,age\nAlice,30\nBob,25\nCharlie,35\n";
        string path = WriteTempFile("simple.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(3, ranges[0].RowCount);
        // Data starts after "name,age\n" (9 bytes).
        Assert.Equal(9, ranges[0].ByteOffset);
        Assert.Equal(content.Length - 9, ranges[0].ByteLength);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_MultipleChunks_SplitsAtCorrectBoundaries()
    {
        string content = "x\na\nb\nc\nd\ne\n";
        string path = WriteTempFile("multi.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 2, CancellationToken.None);

        Assert.Equal(3, ranges.Count);

        // Chunk 0: rows "a" and "b", after header "x\n" (2 bytes).
        Assert.Equal(2, ranges[0].ByteOffset);
        Assert.Equal(2, ranges[0].RowCount);
        Assert.Equal(4, ranges[0].ByteLength); // "a\nb\n"

        // Chunk 1: rows "c" and "d".
        Assert.Equal(6, ranges[1].ByteOffset);
        Assert.Equal(2, ranges[1].RowCount);
        Assert.Equal(4, ranges[1].ByteLength); // "c\nd\n"

        // Chunk 2: row "e" (partial last chunk).
        Assert.Equal(10, ranges[2].ByteOffset);
        Assert.Equal(1, ranges[2].RowCount);
        Assert.Equal(2, ranges[2].ByteLength); // "e\n"
    }

    [Fact]
    public async Task MeasureChunkByteRanges_NoTrailingNewline_CountsLastRow()
    {
        string content = "h\nrow1\nrow2";
        string path = WriteTempFile("no-newline.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(2, ranges[0].RowCount);
        Assert.Equal(2, ranges[0].ByteOffset); // after "h\n"
    }

    [Fact]
    public async Task MeasureChunkByteRanges_HeaderOnly_ProducesNoChunks()
    {
        string content = "name,age\n";
        string path = WriteTempFile("header-only.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Empty(ranges);
    }

    // ───────────────────── Quoted fields ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_QuotedFieldWithNewline_CountsAsOneRow()
    {
        // The quoted field contains an embedded newline — it should count as one logical row.
        string content = "name,desc\n\"Alice\",\"Line\nbreak\"\nBob,Simple\n";
        string path = WriteTempFile("quoted-newline.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(2, ranges[0].RowCount);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_EscapedQuotes_HandledCorrectly()
    {
        // Field with escaped quotes ("") should not confuse the quote state.
        string content = "v\n\"has \"\"escaped\"\" quotes\"\nplain\n";
        string path = WriteTempFile("escaped-quotes.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(2, ranges[0].RowCount);
    }

    [Fact]
    public async Task MeasureChunkByteRanges_MultiLineQuotedField_ChunkBoundaryCorrect()
    {
        // Two rows with multi-line quoted fields, chunk size 1 — each logical row is a separate chunk.
        string content = "v\n\"line\none\"\n\"line\ntwo\"\n";
        string path = WriteTempFile("multi-line-chunks.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 1, CancellationToken.None);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(1, ranges[0].RowCount);
        Assert.Equal(1, ranges[1].RowCount);

        // First data row: "line\none"\n — starts after "v\n" (2 bytes).
        Assert.Equal(2, ranges[0].ByteOffset);
    }

    // ───────────────────── CRLF handling ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_CrLfLineEndings_CountsCorrectly()
    {
        string content = "h\r\nrow1\r\nrow2\r\n";
        string path = WriteTempFile("crlf.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 100, CancellationToken.None);

        Assert.Single(ranges);
        Assert.Equal(2, ranges[0].RowCount);
        // Header "h\r\n" is 3 bytes. Data starts at offset 3.
        Assert.Equal(3, ranges[0].ByteOffset);
    }

    // ───────────────────── Alignment with provider ─────────────────────

    [Fact]
    public async Task MeasureChunkByteRanges_RowCountMatchesProvider()
    {
        // Verify that the measurer's row count matches the number of rows
        // the provider actually yields, ensuring chunk alignment.
        string content = "name,value\nAlice,1\nBob,2\nCharlie,3\nDiana,4\nEve,5\n";
        string path = WriteTempFile("alignment.csv", content);
        CsvTableProvider provider = new();
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
    public async Task MeasureChunkByteRanges_ByteRangesCoverEntireDataRegion()
    {
        string content = "h\nA\nB\nC\nD\n";
        string path = WriteTempFile("coverage.csv", content);
        CsvTableProvider provider = new();

        IReadOnlyList<ChunkByteRange> ranges = await provider.MeasureChunkByteRangesAsync(
            CreateDescriptor(path), chunkSize: 2, CancellationToken.None);

        // The first chunk should start right after the header.
        Assert.Equal(2, ranges[0].ByteOffset);

        // The last chunk should extend to the end of the file (minus header).
        long totalCoveredBytes = ranges.Sum(r => r.ByteLength);
        long dataBytes = content.Length - 2; // minus "h\n"
        Assert.Equal(dataBytes, totalCoveredBytes);
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReportsSupportsSeekTrue()
    {
        string path = WriteTempFile("cap.csv", "h\nrow\n");
        CsvTableProvider provider = new();

        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            CreateDescriptor(path), CancellationToken.None);

        Assert.True(capabilities.SupportsSeek);
    }
}
