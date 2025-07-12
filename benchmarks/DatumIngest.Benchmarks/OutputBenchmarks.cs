using BenchmarkDotNet.Attributes;
using DatumIngest.Model;
using DatumIngest.Output;
using DatumIngest.Output.Writers;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Benchmarks for output writer throughput.
/// </summary>
[MemoryDiagnoser]
public class OutputBenchmarks
{
    private Row[] _rows1K = null!;
    private Row[] _rows10K = null!;
    private Schema _schema = null!;
    private string _tempDirectory = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rows1K = SyntheticDataGenerator.GenerateRows(1_000);
        _rows10K = SyntheticDataGenerator.GenerateRows(10_000);
        _schema = new Schema(new ColumnInfo[]
        {
            new("id", DataKind.Scalar, false),
            new("name", DataKind.String, false),
            new("value", DataKind.Scalar, false),
            new("category", DataKind.String, false),
            new("score", DataKind.Scalar, false)
        });
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum_bench_output");
        Directory.CreateDirectory(_tempDirectory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Benchmark(Description = "CSV write 1K rows")]
    public async Task CsvWrite1K()
    {
        string path = Path.Combine(_tempDirectory, $"bench_1k_{Guid.NewGuid():N}.csv");
        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(_schema);
        foreach (Row row in _rows1K)
        {
            await writer.WriteRowAsync(row);
        }
        await writer.FinalizeAsync();
    }

    [Benchmark(Description = "CSV write 10K rows")]
    public async Task CsvWrite10K()
    {
        string path = Path.Combine(_tempDirectory, $"bench_10k_{Guid.NewGuid():N}.csv");
        await using CsvOutputWriter writer = new(path);
        await writer.InitializeAsync(_schema);
        foreach (Row row in _rows10K)
        {
            await writer.WriteRowAsync(row);
        }
        await writer.FinalizeAsync();
    }

    [Benchmark(Description = "CSV write 10K rows with sharding (1000 per shard)")]
    public async Task CsvWrite10KSharded()
    {
        string basePath = Path.Combine(_tempDirectory, $"bench_sharded_{Guid.NewGuid():N}.csv");
        ShardStrategy strategy = new(ShardMode.SampleCount, 1_000);
        await using ShardingOutputWriter writer = new(
            path => new CsvOutputWriter(path),
            strategy,
            basePath);
        await writer.InitializeAsync(_schema);
        foreach (Row row in _rows10K)
        {
            await writer.WriteRowAsync(row);
        }
        await writer.FinalizeAsync();
    }
}
