using BenchmarkDotNet.Attributes;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;

namespace Axon.QueryEngine.Benchmarks;

/// <summary>
/// Benchmarks for statistics collection overhead.
/// </summary>
[MemoryDiagnoser]
public class StatisticsBenchmarks
{
    private Row[] _rows1K = null!;
    private Row[] _rows10K = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rows1K = SyntheticDataGenerator.GenerateRows(1_000);
        _rows10K = SyntheticDataGenerator.GenerateRows(10_000);
    }

    [Benchmark(Description = "Collect stats 1K rows")]
    public IReadOnlyDictionary<string, ColumnStatistics> CollectStats1K()
    {
        StatisticsCollector collector = new(topK: 10);
        foreach (Row row in _rows1K)
        {
            collector.AddRow(row);
        }
        return collector.GetStatistics();
    }

    [Benchmark(Description = "Collect stats 10K rows")]
    public IReadOnlyDictionary<string, ColumnStatistics> CollectStats10K()
    {
        StatisticsCollector collector = new(topK: 10);
        foreach (Row row in _rows10K)
        {
            collector.AddRow(row);
        }
        return collector.GetStatistics();
    }

    [Benchmark(Description = "Merge two 5K collectors")]
    public IReadOnlyDictionary<string, ColumnStatistics> MergeCollectors()
    {
        StatisticsCollector collector1 = new(topK: 10);
        StatisticsCollector collector2 = new(topK: 10);

        for (int i = 0; i < 5_000; i++)
        {
            collector1.AddRow(_rows10K[i]);
        }
        for (int i = 5_000; i < 10_000; i++)
        {
            collector2.AddRow(_rows10K[i]);
        }

        collector1.Merge(collector2);
        return collector1.GetStatistics();
    }
}
