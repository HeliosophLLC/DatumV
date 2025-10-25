using DatumIngest.Pooling;
using DatumIngest.Ingestion;
using DatumIngest.Model;
using DatumIngest.Statistics;

namespace DatumIngest.Tests.Ingestion;

public sealed class BatchStatisticsCollectorTests
{
    private static RowBatch MakeBatch(Pool pool, string[] names, params DataValue[][] rows)
    {
        ColumnLookup columnLookup = new(names);
        RowBatch batch = pool.RentRowBatch(columnLookup, 1024);

        foreach (DataValue[] values in rows)
            batch.Add(values);

        return batch;
    }

    [Fact]
    public async Task CollectsStatisticsAndPassesThroughBatches()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        using Arena arena = new();
        string[] names = ["value"];

        RowBatch b1 = MakeBatch(pool, names,
            [DataValue.FromFloat32(1f)],
            [DataValue.FromFloat32(2f)]);
        RowBatch b2 = MakeBatch(pool, names,
            [DataValue.FromFloat32(3f)]);

        StatisticsCollector collector = new();
        BatchStatisticsCollector batchCollector = new(collector);
        List<float> values = [];

        await foreach (RowBatch batch in batchCollector.CollectAndPassthrough(Batches(b1, b2), arena))
        {
            for (int i = 0; i < batch.Count; i++)
                values.Add(batch[i][0].AsFloat32());
        }

        // Passthrough: all values came through.
        Assert.Equal([1f, 2f, 3f], values);

        // Statistics: collected across all batches.
        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Assert.Contains("value", stats.Keys);
        Assert.NotEmpty(stats["value"].Results);
    }

    [Fact]
    public async Task EmptyStream_ProducesEmptyStatistics()
    {
        using Arena arena = new();
        StatisticsCollector collector = new();
        BatchStatisticsCollector batchCollector = new(collector);

        await foreach (RowBatch _ in batchCollector.CollectAndPassthrough(Empty(), arena)) { }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Assert.Empty(stats);
    }

    private static async IAsyncEnumerable<RowBatch> Batches(params RowBatch[] batches)
    {
        foreach (RowBatch batch in batches)
            yield return batch;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RowBatch> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
