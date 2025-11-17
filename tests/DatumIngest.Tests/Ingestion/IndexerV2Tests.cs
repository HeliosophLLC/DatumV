using DatumIngest.Indexing;
using DatumIngest.Ingestion;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

namespace DatumIngest.Tests.Ingestion;

/// <summary>
/// Verifies the <see cref="Indexer"/> works end-to-end against v2
/// <c>.datum</c> files. The indexer's only v2-specific behavior is
/// (1) opening the file via the version-aware factory and (2) skipping
/// sidecar-bound values at the bloom-filter layer; both are covered here.
/// </summary>
public sealed class IndexerV2Tests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"indexer_v2_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task IndexV2Datum_TabularOnly_BuildsBloomAndStats()
    {
        // CSV with all-inline columns (Int32 + short String) — every value
        // stays in the column page, no sidecar materialized. Indexer
        // exercises the version-aware factory and runs the standard
        // bloom + chunk-stats path.
        const string csv =
            "id,color\n" +
            "1,red\n" +
            "2,green\n" +
            "3,blue\n" +
            "4,red\n";

        string datumPath = await IngestCsvAsync(csv, "tabular.datum");

        DatumFileDescriptor source = new(datumPath);
        OutputDescriptor indexDest = new(Path.ChangeExtension(datumPath, ".datum-index"));

        Indexer indexer = new(new Pool(new PoolBacking()));
        IndexResult result = await indexer.IndexAsync(source, indexDest);

        Assert.Equal(4, result.RowCount);
        Assert.True(result.ChunkCount >= 1);
        Assert.True(result.BytesWritten > 0);
        Assert.Contains("id", result.BloomColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("color", result.BloomColumns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexV2Datum_WithSidecarStrings_DoesNotCrash()
    {
        // CSV with a long string column that ingests to the sidecar.
        // Indexer should silently skip those rows at the bloom layer
        // and finish without crashing on the sidecar coordinates.
        const string csv =
            "id,description\n" +
            "1,short\n" +
            "2,this string is definitely longer than fifteen bytes\n" +
            "3,another medium-length string value example\n" +
            "4,tiny\n";

        string datumPath = await IngestCsvAsync(csv, "sidecar.datum");
        Assert.True(File.Exists(Path.ChangeExtension(datumPath, ".datum-blob")),
            "Long strings should have materialized the sidecar.");

        DatumFileDescriptor source = new(datumPath);
        OutputDescriptor indexDest = new(Path.ChangeExtension(datumPath, ".datum-index"));

        Indexer indexer = new(new Pool(new PoolBacking()));
        IndexResult result = await indexer.IndexAsync(source, indexDest);

        Assert.Equal(4, result.RowCount);
        Assert.True(result.BytesWritten > 0);
        // Even the sidecar-heavy "description" column appears in the
        // bloom set: short rows still added inline; sidecar rows skipped.
        Assert.Contains("id", result.BloomColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("description", result.BloomColumns, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> IngestCsvAsync(string csv, string fileName)
    {
        string datumPath = Path.Combine(_tempDir, fileName);
        MemoryFileDescriptor source = new(csv, fileName: "test.csv");
        OutputDescriptor destination = new(datumPath);

        FormatRegistry registry = new([new CsvFileFormat()]);
        Pool pool = new(new PoolBacking());
        Ingester ingester = new(registry, pool);
        await ingester.IngestV2Async(source, destination);
        return datumPath;
    }
}
