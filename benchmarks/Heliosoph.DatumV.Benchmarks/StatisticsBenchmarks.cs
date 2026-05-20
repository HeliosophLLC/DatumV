using BenchmarkDotNet.Attributes;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Statistics;

namespace Heliosoph.DatumV.Benchmarks;

/// <summary>
/// Benchmarks for statistics collection overhead.
/// </summary>
[MemoryDiagnoser]
public class StatisticsBenchmarks
{
    private Row[] _rows1K = null!;
    private Row[] _rows10K = null!;
    private Arena _arena = null!;

    [GlobalSetup]
    public void Setup()
    {
        _arena = new Arena();
        _rows1K = SyntheticDataGenerator.GenerateRows(1_000);
        _rows10K = SyntheticDataGenerator.GenerateRows(10_000);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _arena.Dispose();
    }

    [Benchmark(Description = "Collect stats 1K rows")]
    public IReadOnlyDictionary<string, ColumnStatistics> CollectStats1K()
    {
        StatisticsCollector collector = new(topK: 10);
        foreach (Row row in _rows1K)
        {
            collector.AddRow(row, _arena);
        }
        return collector.GetStatistics();
    }

    [Benchmark(Description = "Collect stats 10K rows")]
    public IReadOnlyDictionary<string, ColumnStatistics> CollectStats10K()
    {
        StatisticsCollector collector = new(topK: 10);
        foreach (Row row in _rows10K)
        {
            collector.AddRow(row, _arena);
        }
        return collector.GetStatistics();
    }

}
