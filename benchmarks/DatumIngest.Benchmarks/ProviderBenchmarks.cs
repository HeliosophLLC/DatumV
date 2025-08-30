using BenchmarkDotNet.Attributes;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Benchmarks for reading data through providers at different dataset sizes.
/// </summary>
[MemoryDiagnoser]
public class ProviderBenchmarks
{
    private string _tempDirectory = null!;
    private string _csvPath1K = null!;
    private string _csvPath10K = null!;
    private string _jsonPath1K = null!;
    private string _jsonPath10K = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum_bench_providers");
        Directory.CreateDirectory(_tempDirectory);

        _csvPath1K = SyntheticDataGenerator.GenerateCsv(_tempDirectory, 1_000);
        _csvPath10K = SyntheticDataGenerator.GenerateCsv(_tempDirectory, 10_000);
        _jsonPath1K = SyntheticDataGenerator.GenerateJson(_tempDirectory, 1_000);
        _jsonPath10K = SyntheticDataGenerator.GenerateJson(_tempDirectory, 10_000);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Benchmark(Description = "CSV 1K rows")]
    public async Task ReadCsv1K()
    {
        CsvTableProvider provider = new();
        TableDescriptor descriptor = new("csv", "data", _csvPath1K, new Dictionary<string, string>());

        await foreach (RowBatch batch in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            batch.Return();
        }
    }

    [Benchmark(Description = "CSV 10K rows")]
    public async Task ReadCsv10K()
    {
        CsvTableProvider provider = new();
        TableDescriptor descriptor = new("csv", "data", _csvPath10K, new Dictionary<string, string>());

        await foreach (RowBatch batch in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            batch.Return();
        }
    }

    [Benchmark(Description = "CSV 1K with projection")]
    public async Task ReadCsv1KProjection()
    {
        CsvTableProvider provider = new();
        TableDescriptor descriptor = new("csv", "data", _csvPath1K, new Dictionary<string, string>());
        HashSet<string> requiredColumns = ["id", "value"];

        await foreach (RowBatch batch in provider.OpenAsync(descriptor, requiredColumns, CancellationToken.None))
        {
            batch.Return();
        }
    }

    [Benchmark(Description = "JSON 1K rows")]
    public async Task ReadJson1K()
    {
        JsonTableProvider provider = new();
        TableDescriptor descriptor = new("json", "data", _jsonPath1K, new Dictionary<string, string>());

        await foreach (RowBatch batch in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            batch.Return();
        }
    }

    [Benchmark(Description = "JSON 10K rows")]
    public async Task ReadJson10K()
    {
        JsonTableProvider provider = new();
        TableDescriptor descriptor = new("json", "data", _jsonPath10K, new Dictionary<string, string>());

        await foreach (RowBatch batch in provider.OpenAsync(descriptor, null, CancellationToken.None))
        {
            batch.Return();
        }
    }
}
